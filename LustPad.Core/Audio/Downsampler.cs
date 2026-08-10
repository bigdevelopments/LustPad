namespace LustPad.Core.Audio;

/// <summary>
/// Integer-factor downsample with a simple multi-tap low-pass (offline quality).
/// Scales loop markers to the output rate.
/// </summary>
public static class Downsampler
{
    /// <param name="factor">Must be 2 or 4 (input rate / output rate).</param>
    public static LoopProcessor.LoopResult Downsample(LoopProcessor.LoopResult input, int factor)
    {
        if (factor <= 1)
            return input;
        if (factor is not (2 or 4))
            throw new ArgumentOutOfRangeException(nameof(factor), "Oversample factor must be 1, 2, or 4.");

        int channels = input.Channels;
        int inFrames = input.FrameCount;
        int outFrames = inFrames / factor;
        if (outFrames < 1)
            outFrames = 1;

        var output = new float[outFrames * channels];

        // Half-band-ish FIR coefficients (symmetric) for gentle anti-alias before decimation
        // Applied as moving average weighted over `factor * 4` taps.
        int taps = factor * 4;
        var kernel = BuildLowpassKernel(taps);

        for (int of = 0; of < outFrames; of++)
        {
            int center = of * factor;
            for (int c = 0; c < channels; c++)
            {
                float sum = 0, wsum = 0;
                for (int t = 0; t < taps; t++)
                {
                    int src = center - taps / 2 + t;
                    if ((uint)src >= (uint)inFrames)
                        continue;
                    float w = kernel[t];
                    sum += input.Interleaved[src * channels + c] * w;
                    wsum += w;
                }
                output[of * channels + c] = wsum > 1e-8f ? sum / wsum : 0f;
            }
        }

        int Scale(int frame) => Math.Clamp(frame / factor, 0, outFrames);

        return new LoopProcessor.LoopResult
        {
            Interleaved = output,
            Channels = channels,
            SampleRate = input.SampleRate / factor,
            FrameCount = outFrames,
            LoopStartFrame = Scale(input.LoopStartFrame),
            LoopEndFrame = outFrames,
            LoopStartAdjustmentFrames = input.LoopStartAdjustmentFrames / factor,
            MatchError = input.MatchError,
        };
    }

    private static float[] BuildLowpassKernel(int taps)
    {
        var k = new float[taps];
        float mid = (taps - 1) * 0.5f;
        float sum = 0;
        for (int i = 0; i < taps; i++)
        {
            float x = (i - mid) / mid;
            // Raised cosine window
            float w = 0.5f * (1f + MathF.Cos(x * MathF.PI));
            k[i] = w;
            sum += w;
        }
        for (int i = 0; i < taps; i++)
            k[i] /= sum;
        return k;
    }
}
