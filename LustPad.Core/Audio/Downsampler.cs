namespace LustPad.Core.Audio;

/// <summary>
/// Integer-factor downsample with a Kaiser-windowed sinc low-pass (offline quality).
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
        var kernel = BuildSincKernel(factor);
        int taps = kernel.Length;
        int half = taps / 2;

        for (int of = 0; of < outFrames; of++)
        {
            int center = of * factor;
            for (int c = 0; c < channels; c++)
            {
                double sum = 0;
                for (int t = 0; t < taps; t++)
                {
                    int src = center + t - half;
                    if ((uint)src >= (uint)inFrames)
                        continue;
                    sum += input.Interleaved[src * channels + c] * kernel[t];
                }
                output[of * channels + c] = (float)sum;
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

    /// <summary>
    /// Cutoff just below the output Nyquist (0.45 / factor of the input rate)
    /// so 96 kHz → 48 kHz does not fold 24–48 kHz back in.
    /// </summary>
    internal static float[] BuildSincKernel(int factor)
    {
        int taps = factor == 4 ? 129 : 65; // odd, integer centre
        float fc = 0.45f / factor; // cycles per input sample
        float mid = (taps - 1) * 0.5f;
        const float beta = 8.5f;
        float i0Beta = BesselI0(beta);

        var k = new float[taps];
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            float x = i - mid;
            float sinc = x == 0f
                ? 2f * fc
                : MathF.Sin(2f * MathF.PI * fc * x) / (MathF.PI * x);
            float n = x / mid;
            float win = BesselI0(beta * MathF.Sqrt(MathF.Max(0f, 1f - n * n))) / i0Beta;
            k[i] = sinc * win;
            sum += k[i];
        }

        float norm = (float)(1.0 / sum);
        for (int i = 0; i < taps; i++)
            k[i] *= norm;
        return k;
    }

    /// <summary>Abramowitz &amp; Stegun 9.8.1 / 9.8.2 — I₀(x).</summary>
    private static float BesselI0(float x)
    {
        float ax = MathF.Abs(x);
        if (ax < 3.75f)
        {
            float y = x / 3.75f;
            y *= y;
            return 1f + y * (3.5156229f + y * (3.0899424f + y * (1.2067492f
                + y * (0.2659732f + y * (0.0360768f + y * 0.0045813f)))));
        }

        float z = 3.75f / ax;
        return MathF.Exp(ax) / MathF.Sqrt(ax) * (0.39894228f + z * (0.01328592f
            + z * (0.00225319f + z * (-0.00157565f + z * (0.00916281f
            + z * (-0.02057706f + z * (0.02635537f + z * (-0.01647633f
            + z * 0.00392377f))))))));
    }
}
