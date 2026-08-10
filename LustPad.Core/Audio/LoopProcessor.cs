using LustPad.Core.Synthesis;

namespace LustPad.Core.Audio;

/// <summary>
/// Builds a seamless sampler loop:
/// 1) optional search for a loop-start that best matches the loop end (tone continuity),
/// 2) equal-power crossfade so the wrap has no click.
///
/// Pair with <see cref="LoopEvolution.ApplyLoopLock"/> so LFO phases also match —
/// crossfade alone cannot hide a half-finished filter sweep.
/// </summary>
public static class LoopProcessor
{
    public sealed class LoopResult
    {
        public required float[] Interleaved { get; init; }
        public int Channels { get; init; }
        public int SampleRate { get; init; }
        public int FrameCount { get; init; }
        public int LoopStartFrame { get; init; }
        public int LoopEndFrame { get; init; }
        /// <summary>How far (frames) the optimizer moved loop start from the user value.</summary>
        public int LoopStartAdjustmentFrames { get; init; }
        public double MatchError { get; init; }
    }

    public static LoopResult Apply(RenderedAudio audio, PadParameters p)
    {
        int channels = audio.Channels;
        int sampleRate = audio.SampleRate;
        int outFrames = audio.OutputFrames;

        int nominalStart = (int)(Math.Clamp(p.LoopStartSeconds, 0.05f, p.DurationSeconds * 0.9f) * sampleRate);
        nominalStart = Math.Clamp(nominalStart, 0, outFrames - sampleRate / 10);

        // Longer crossfades hide residual filter/chorus/noise state mismatch after period-lock.
        float maxCf = Math.Min(4f, p.DurationSeconds * 0.2f);
        int crossfade = (int)(Math.Clamp(p.CrossfadeSeconds, 0.02f, maxCf) * sampleRate);

        int loopLength = outFrames - nominalStart;
        if (crossfade * 2 >= loopLength)
            crossfade = Math.Max(1, loopLength / 4);

        // Need a lead-in of `crossfade` samples before loop start for a seamless wrap
        // (last sample of file must approach the sample just before loopStart).
        if (nominalStart < crossfade)
            crossfade = Math.Max(1, nominalStart);

        // Working copy of the export body
        var output = new float[outFrames * channels];
        for (int i = 0; i < outFrames; i++)
        {
            for (int c = 0; c < channels; c++)
                output[i * channels + c] = audio.Interleaved[i * channels + c];
        }

        int loopStart = nominalStart;
        double matchError = 0;

        if (p.OptimizeLoopPoint && loopLength > crossfade * 3 && nominalStart > crossfade)
        {
            // Search ±0.35s (or 8% of loop) around the requested start for best end↔lead-in match
            int searchRadius = Math.Min(
                (int)(0.35f * sampleRate),
                Math.Max(crossfade, loopLength / 12));
            (loopStart, matchError) = FindBestLoopStart(
                output, channels, outFrames, nominalStart, searchRadius, crossfade);
        }
        else
        {
            matchError = MeasureMismatch(output, channels, outFrames, loopStart, crossfade);
        }

        // Keep crossfade within the lead-in available before the chosen loop start.
        if (loopStart < crossfade)
            crossfade = Math.Max(1, loopStart);

        // Equal-power crossfade of the FILE END toward the PRE-LOOP lead-in:
        //   end window  = [outFrames - cf, outFrames)
        //   lead window = [loopStart - cf, loopStart)
        // Sampler plays exclusive end then jumps to loopStart, so the last sample must
        // land on the lead-in sample immediately before loopStart (not on loopStart+i).
        // Old code blended toward loopStart+i, so the wrap jumped from ~start+cf to start → click.
        for (int i = 0; i < crossfade; i++)
        {
            // t=0 → keep end; t=1 → fully lead-in (so last frame ≈ sample at loopStart-1)
            float t = (i + 1) / (float)crossfade;
            float fadeOut = MathF.Cos(t * MathF.PI * 0.5f);
            float fadeIn = MathF.Sin(t * MathF.PI * 0.5f);

            int endIdx = outFrames - crossfade + i;
            int leadIdx = loopStart - crossfade + i;

            for (int c = 0; c < channels; c++)
            {
                float a = output[endIdx * channels + c];
                float b = output[leadIdx * channels + c];
                output[endIdx * channels + c] = a * fadeOut + b * fadeIn;
            }
        }

        // Tiny fade-in at file start only (attack), not at loop point
        int edge = Math.Min(64, outFrames / 8);
        for (int i = 0; i < edge; i++)
        {
            float g = i / (float)edge;
            for (int c = 0; c < channels; c++)
                output[i * channels + c] *= g;
        }

        return new LoopResult
        {
            Interleaved = output,
            Channels = channels,
            SampleRate = sampleRate,
            FrameCount = outFrames,
            LoopStartFrame = loopStart,
            LoopEndFrame = outFrames,
            LoopStartAdjustmentFrames = loopStart - nominalStart,
            MatchError = matchError,
        };
    }

    /// <summary>
    /// Minimise squared error between the loop-end window and a candidate start window.
    /// Uses energy-normalised error so overall level drift doesn't dominate.
    /// Steps coarsely then refines — cheap enough for multi-minute offline renders.
    /// </summary>
    private static (int bestStart, double error) FindBestLoopStart(
        float[] data, int channels, int outFrames,
        int nominalStart, int searchRadius, int crossfade)
    {
        // loopStart must leave room for lead-in [start-cf, start) and end crossfade region
        int minStart = Math.Max(crossfade, nominalStart - searchRadius);
        int maxStart = Math.Min(outFrames - crossfade - 1, nominalStart + searchRadius);
        if (minStart >= maxStart)
            return (nominalStart, MeasureMismatch(data, channels, outFrames, nominalStart, crossfade));

        int coarseStep = Math.Max(1, sampleStep(crossfade));
        int best = nominalStart;
        double bestErr = double.MaxValue;

        for (int start = minStart; start <= maxStart; start += coarseStep)
        {
            double err = MeasureMismatch(data, channels, outFrames, start, crossfade);
            if (err < bestErr)
            {
                bestErr = err;
                best = start;
            }
        }

        // Refine around coarse best
        int refineLo = Math.Max(minStart, best - coarseStep);
        int refineHi = Math.Min(maxStart, best + coarseStep);
        for (int start = refineLo; start <= refineHi; start++)
        {
            double err = MeasureMismatch(data, channels, outFrames, start, crossfade);
            if (err < bestErr)
            {
                bestErr = err;
                best = start;
            }
        }

        // Prefer near-zero at the wrap pair (last end sample / first loop sample) when close
        best = PreferNearZeroCrossing(data, channels, best, refineLo, refineHi, outFrames, crossfade, bestErr);

        return (best, bestErr);

        static int sampleStep(int cf) => cf > 2000 ? 16 : cf > 500 ? 8 : 4;
    }

    private static int PreferNearZeroCrossing(
        float[] data, int channels, int best, int lo, int hi,
        int outFrames, int crossfade, double bestErr)
    {
        int chosen = best;
        // Prefer small amplitude at the sample that becomes the wrap neighbour (loopStart-1)
        float bestZ = MathF.Abs(Mono(data, channels, Math.Max(0, best - 1)));
        int zLo = Math.Max(lo, best - 32);
        int zHi = Math.Min(hi, best + 32);
        for (int s = zLo; s <= zHi; s++)
        {
            double err = MeasureMismatch(data, channels, outFrames, s, crossfade);
            if (err > bestErr * 1.05)
                continue;
            float z = MathF.Abs(Mono(data, channels, Math.Max(0, s - 1)));
            if (z < bestZ)
            {
                bestZ = z;
                chosen = s;
            }
        }
        return chosen;
    }

    private static float Mono(float[] data, int channels, int frame)
    {
        int i = frame * channels;
        if (channels == 1) return data[i];
        return 0.5f * (data[i] + data[i + 1]);
    }

    /// <summary>
    /// Energy-normalised mismatch between the file-end window and the pre-loop lead-in
    /// (what must agree for a click-free exclusive-end wrap to <paramref name="loopStart"/>).
    /// </summary>
    private static double MeasureMismatch(
        float[] data, int channels, int outFrames, int loopStart, int crossfade)
    {
        if (crossfade < 1 || loopStart < crossfade || outFrames < crossfade)
            return double.MaxValue;

        double err = 0;
        double e0 = 0, e1 = 0;
        for (int i = 0; i < crossfade; i++)
        {
            int endIdx = outFrames - crossfade + i;
            int leadIdx = loopStart - crossfade + i;
            for (int c = 0; c < channels; c++)
            {
                float a = data[endIdx * channels + c];
                float b = data[leadIdx * channels + c];
                float d = a - b;
                err += d * d;
                e0 += a * a;
                e1 += b * b;
            }
        }

        // Normalise by energy so quieter candidates aren't artificially favoured
        double denom = Math.Sqrt(e0 * e1) + 1e-12;
        return err / denom;
    }
}
