using System.Threading;
using LustPad.Core.Synthesis;

namespace LustPad.Core.Audio;

/// <summary>
/// High-level entry: loop-lock → (optional oversampled) synthesize → loop join → optional downsample → write.
/// </summary>
public static class PadRenderer
{
    public static LoopProcessor.LoopResult Generate(
        PadParameters parameters, CancellationToken cancellationToken = default,
        bool interactive = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var locked = LoopEvolution.ApplyLoopLock(parameters);
        if (interactive)
        {
            // Same oscillators/FX as export; skip 2× print and loop-start search
            // so slider preview can keep up. Crossfade still runs at the requested start.
            if (ReferenceEquals(locked, parameters))
                locked = parameters.Clone();
            locked.OversampleFactor = 1;
            locked.OptimizeLoopPoint = false;
            locked.Archival96kHz = false;
        }

        int factor = Math.Clamp(locked.OversampleFactor, 1, 2);
        int workingRate = PadParameters.SampleRate * factor;

        var synth = new PadSynthesizer();
        var rendered = synth.Render(locked, workingRate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var looped = LoopProcessor.Apply(rendered, locked);

        // Keep 96 kHz only when archival is requested and we actually rendered 2×.
        bool keepHighRate = locked.Archival96kHz && factor >= 2;
        if (factor > 1 && !keepHighRate)
            looped = Downsampler.Downsample(looped, factor);

        return looped;
    }

    public static LoopProcessor.LoopResult GenerateAndSave(
        PadParameters parameters, string wavPath, CancellationToken cancellationToken = default)
    {
        var result = Generate(parameters, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        int bits = parameters.ExportBitDepth >= 24 ? 24 : 16;
        WavWriter.Write(wavPath, result, parameters.EmbedLoopPoints, bits);
        return result;
    }
}
