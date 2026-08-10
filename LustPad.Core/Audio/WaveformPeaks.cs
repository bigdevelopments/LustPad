namespace LustPad.Core.Audio;

/// <summary>
/// Builds min/max peak envelope columns for UI waveform overview.
/// </summary>
public static class WaveformPeaks
{
    public readonly record struct PeakColumn(float Min, float Max);

    public static PeakColumn[] Build(LoopProcessor.LoopResult audio, int columns = 512)
    {
        ArgumentNullException.ThrowIfNull(audio);
        columns = Math.Clamp(columns, 32, 4096);
        int frames = audio.FrameCount;
        int ch = audio.Channels;
        var peaks = new PeakColumn[columns];
        if (frames <= 0)
            return peaks;

        for (int c = 0; c < columns; c++)
        {
            int start = (int)((long)c * frames / columns);
            int end = (int)((long)(c + 1) * frames / columns);
            if (end <= start) end = start + 1;
            if (end > frames) end = frames;

            float min = 0, max = 0;
            for (int i = start; i < end; i++)
            {
                float s;
                if (ch == 1)
                    s = audio.Interleaved[i];
                else
                    s = 0.5f * (audio.Interleaved[i * 2] + audio.Interleaved[i * 2 + 1]);
                if (s < min) min = s;
                if (s > max) max = s;
            }
            peaks[c] = new PeakColumn(min, max);
        }

        return peaks;
    }

    /// <summary>Normalized 0..1 positions for markers.</summary>
    public static (float loopStart, float loopEnd, float crossfadeStart) MarkerFractions(
        LoopProcessor.LoopResult audio, float crossfadeSeconds)
    {
        if (audio.FrameCount <= 0)
            return (0, 1, 1);
        float loopStart = audio.LoopStartFrame / (float)audio.FrameCount;
        float loopEnd = 1f;
        int cf = (int)(Math.Clamp(crossfadeSeconds, 0.02f, 4f) * audio.SampleRate);
        cf = Math.Min(cf, audio.FrameCount / 4);
        float crossfadeStart = (audio.FrameCount - cf) / (float)audio.FrameCount;
        return (loopStart, loopEnd, Math.Clamp(crossfadeStart, 0f, 1f));
    }
}
