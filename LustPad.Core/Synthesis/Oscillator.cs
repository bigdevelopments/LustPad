namespace LustPad.Core.Synthesis;

/// <summary>
/// Band-limited-ish oscillator using polyBLEP for saw/square/pulse edges.
/// </summary>
internal sealed class Oscillator
{
    private double _phase;
    private readonly double _sampleRate;

    public Oscillator(double sampleRate, double phase = 0)
    {
        _sampleRate = sampleRate;
        _phase = phase;
    }

    /// <param name="pulseWidth">Duty cycle for <see cref="WaveformType.Pulse"/> (0.05–0.95).</param>
    public float Next(float frequencyHz, WaveformType waveform, float pulseWidth = 0.5f)
    {
        frequencyHz = Math.Clamp(frequencyHz, 1f, (float)(_sampleRate * 0.45));
        double dt = frequencyHz / _sampleRate;
        float sample = waveform switch
        {
            WaveformType.Saw => PolyBlepSaw(_phase, dt),
            WaveformType.Square => PolyBlepPulse(_phase, dt, 0.5),
            WaveformType.Pulse => PolyBlepPulse(_phase, dt, Math.Clamp(pulseWidth, 0.05f, 0.95f)),
            WaveformType.Triangle => Triangle(_phase),
            WaveformType.Sine => MathF.Sin((float)(_phase * Math.PI * 2.0)),
            WaveformType.Mixed => 0.55f * PolyBlepSaw(_phase, dt) + 0.45f * Triangle(_phase),
            _ => PolyBlepSaw(_phase, dt),
        };

        _phase += dt;
        if (_phase >= 1.0)
            _phase -= 1.0;

        return sample;
    }

    private static float Triangle(double phase)
    {
        float t = (float)phase;
        return 4f * MathF.Abs(t - 0.5f) - 1f;
    }

    private static float PolyBlepSaw(double t, double dt)
    {
        float value = (float)(2.0 * t - 1.0);
        return value - PolyBlep(t, dt);
    }

    /// <summary>PolyBLEP pulse: high for phase &lt; duty, low otherwise. Duty 0.5 ≡ square.</summary>
    private static float PolyBlepPulse(double t, double dt, double duty)
    {
        duty = Math.Clamp(duty, 0.05, 0.95);
        float value = t < duty ? 1f : -1f;
        // Rising edge at t=0
        value += PolyBlep(t, dt);
        // Falling edge at t=duty
        double tFall = t - duty;
        if (tFall < 0.0) tFall += 1.0;
        value -= PolyBlep(tFall, dt);
        return value;
    }

    private static float PolyBlep(double t, double dt)
    {
        if (t < dt)
        {
            t /= dt;
            return (float)(t + t - t * t - 1.0);
        }
        if (t > 1.0 - dt)
        {
            t = (t - 1.0) / dt;
            return (float)(t * t + t + t + 1.0);
        }
        return 0f;
    }
}
