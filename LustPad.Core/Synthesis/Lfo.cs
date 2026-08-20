namespace LustPad.Core.Synthesis;

internal sealed class Lfo
{
    private double _phase; // used only by incremental Next()
    private readonly double _phase0;
    private readonly double _sampleRate;

    public Lfo(double sampleRate, double phase = 0)
    {
        _sampleRate = sampleRate;
        _phase0 = phase - Math.Floor(phase);
        _phase = _phase0;
    }

    /// <summary>
    /// Absolute-time sine from sample index — no phase drift over multi-minute renders.
    /// Critical for loop locking: rate * loopSamples exact integer cycles ⇒ end phase == start phase.
    /// </summary>
    public float SinAt(long sampleIndex, float rateHz)
    {
        if (rateHz <= 0.0000001f)
            return FastTrig.SinTurns(_phase0); // frozen

        return FastTrig.SinTurns(_phase0 + rateHz * sampleIndex / _sampleRate);
    }

    public float UnipolarAt(long sampleIndex, float rateHz) => 0.5f + 0.5f * SinAt(sampleIndex, rateHz);

    /// <summary>Incremental API (chorus, etc.). Prefer <see cref="SinAt"/> for pad evolution.</summary>
    public float Next(float rateHz)
    {
        rateHz = Math.Max(0f, rateHz);
        float value = FastTrig.SinTurns(_phase);
        _phase += rateHz / _sampleRate;
        if (_phase >= 1.0)
            _phase -= Math.Floor(_phase);
        return value;
    }

    public float NextUnipolar(float rateHz) => 0.5f + 0.5f * Next(rateHz);
}
