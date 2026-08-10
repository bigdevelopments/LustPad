namespace LustPad.Core.Synthesis;

/// <summary>
/// One-pole DC blocker / high-pass. Removes static offset and sub-Hz wander
/// (e.g. asymmetric pulse duty and slow PWM bias) without touching pad fundamentals.
/// </summary>
internal sealed class DcBlocker
{
    private readonly float _r;
    private float _x1;
    private float _y1;

    /// <param name="cutoffHz">
    /// High-pass corner. ~5–20 Hz strips DC and PWM-rate bias; musical content stays.
    /// </param>
    public DcBlocker(float sampleRate, float cutoffHz = 10f)
    {
        cutoffHz = Math.Clamp(cutoffHz, 0.5f, 80f);
        sampleRate = Math.Max(sampleRate, 1f);
        // y[n] = x[n] - x[n-1] + R * y[n-1],  R = exp(-2π fc / fs)
        _r = MathF.Exp(-2f * MathF.PI * cutoffHz / sampleRate);
    }

    public float Process(float x)
    {
        float y = x - _x1 + _r * _y1;
        _x1 = x;
        _y1 = y;
        return y;
    }

    public void Reset()
    {
        _x1 = 0;
        _y1 = 0;
    }
}
