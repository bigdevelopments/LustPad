using System.Numerics;
using System.Runtime.CompilerServices;

namespace LustPad.Core.Synthesis;

/// <summary>
/// SIMD unison bank (AVX/SSE <see cref="Vector{T}"/> lanes). Each lane is one voice.
/// Scalar remainder handles leftover voices when count is not a multiple of the vector width.
/// </summary>
internal sealed class OscBank
{
    private readonly float[] _phase;
    private readonly float[] _freq;
    private readonly float[] _gainL;
    private readonly float[] _gainR;
    private readonly float[] _onset;
    private readonly int _voices;
    private readonly int _padded;
    private readonly float _invSr;

    public OscBank(int voices, float sampleRate, Random rng)
    {
        _voices = Math.Max(0, voices);
        int w = Math.Max(1, Vector<float>.Count);
        _padded = _voices == 0 ? 0 : (_voices + w - 1) / w * w;
        _phase = new float[Math.Max(_padded, 1)];
        _freq = new float[Math.Max(_padded, 1)];
        _gainL = new float[Math.Max(_padded, 1)];
        _gainR = new float[Math.Max(_padded, 1)];
        _onset = new float[Math.Max(_padded, 1)];
        _invSr = 1f / sampleRate;
        for (int i = 0; i < _voices; i++)
            _phase[i] = (float)rng.NextDouble();
        for (int i = 0; i < _onset.Length; i++)
            _onset[i] = 1f;
    }

    public void SetGains(ReadOnlySpan<float> panL, ReadOnlySpan<float> panR, float voiceScale)
    {
        for (int i = 0; i < _voices; i++)
        {
            _gainL[i] = panL[i] * voiceScale;
            _gainR[i] = panR[i] * voiceScale;
        }
    }

    public void SetOnsetAll(float value)
    {
        Array.Fill(_onset, value);
        for (int i = _voices; i < _padded; i++)
            _onset[i] = 0f;
    }

    public void SetOnset(int voice, float gain)
    {
        if ((uint)voice < (uint)_voices)
            _onset[voice] = gain;
    }

    public void Mix(
        ReadOnlySpan<float> detuneRatio,
        float baseFreq,
        WaveformType wave,
        float pulseWidth,
        ref float left,
        ref float right)
    {
        if (_voices <= 0)
            return;

        for (int i = 0; i < _voices; i++)
        {
            float f = baseFreq * detuneRatio[i];
            if (f < 1f) f = 1f;
            _freq[i] = f;
        }

        if (Vector.IsHardwareAccelerated && _padded >= Vector<float>.Count)
            MixVector(wave, pulseWidth, ref left, ref right);
        else
            MixScalar(wave, pulseWidth, ref left, ref right);
    }

    private void MixVector(WaveformType wave, float pulseWidth, ref float left, ref float right)
    {
        int w = Vector<float>.Count;
        float accL = 0, accR = 0;
        var invSr = new Vector<float>(_invSr);
        var pw = new Vector<float>(Math.Clamp(pulseWidth, 0.05f, 0.95f));

        for (int i = 0; i < _padded; i += w)
        {
            var phase = new Vector<float>(_phase, i);
            var freq = new Vector<float>(_freq, i);
            var dt = freq * invSr;
            var sample = Wave(wave, phase, dt, pw);
            sample *= new Vector<float>(_onset, i);
            accL += Vector.Sum(sample * new Vector<float>(_gainL, i));
            accR += Vector.Sum(sample * new Vector<float>(_gainR, i));

            phase += dt;
            phase -= Vector.Floor(phase);
            phase.CopyTo(_phase, i);
        }

        left += accL;
        right += accR;
    }

    private void MixScalar(WaveformType wave, float pulseWidth, ref float left, ref float right)
    {
        float pw = Math.Clamp(pulseWidth, 0.05f, 0.95f);
        for (int i = 0; i < _voices; i++)
        {
            float dt = _freq[i] * _invSr;
            float phase = _phase[i];
            float sample = wave switch
            {
                WaveformType.Square => PolyBlepPulse(phase, dt, 0.5f),
                WaveformType.Pulse => PolyBlepPulse(phase, dt, pw),
                WaveformType.Triangle => Triangle(phase),
                WaveformType.Sine => FastTrig.SinTurns(phase),
                WaveformType.Mixed => 0.55f * PolyBlepSaw(phase, dt) + 0.45f * Triangle(phase),
                _ => PolyBlepSaw(phase, dt),
            };
            sample *= _onset[i];
            left += sample * _gainL[i];
            right += sample * _gainR[i];
            phase += dt;
            if (phase >= 1f) phase -= 1f;
            _phase[i] = phase;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> Wave(WaveformType wave, Vector<float> phase, Vector<float> dt, Vector<float> pw) =>
        wave switch
        {
            WaveformType.Square => PolyBlepPulse(phase, dt, new Vector<float>(0.5f)),
            WaveformType.Pulse => PolyBlepPulse(phase, dt, pw),
            WaveformType.Triangle => Triangle(phase),
            WaveformType.Sine => SinTurns(phase),
            WaveformType.Mixed => PolyBlepSaw(phase, dt) * 0.55f + Triangle(phase) * 0.45f,
            _ => PolyBlepSaw(phase, dt),
        };

    private static Vector<float> Triangle(Vector<float> phase)
    {
        var t = phase - new Vector<float>(0.5f);
        t = Vector.Abs(t);
        return t * 4f - Vector<float>.One;
    }

    private static Vector<float> PolyBlepSaw(Vector<float> t, Vector<float> dt) =>
        t * 2f - Vector<float>.One - PolyBlep(t, dt);

    private static Vector<float> PolyBlepPulse(Vector<float> t, Vector<float> dt, Vector<float> duty)
    {
        var high = Vector.LessThan(t, duty);
        var value = Vector.ConditionalSelect(high, Vector<float>.One, new Vector<float>(-1f));
        value += PolyBlep(t, dt);
        var tFall = t - duty;
        tFall = Vector.ConditionalSelect(Vector.LessThan(tFall, Vector<float>.Zero), tFall + Vector<float>.One, tFall);
        value -= PolyBlep(tFall, dt);
        return value;
    }

    private static Vector<float> PolyBlep(Vector<float> t, Vector<float> dt)
    {
        var invDt = Vector<float>.One / dt;
        var c1 = Vector.LessThan(t, dt);
        var t1 = t * invDt;
        var r1 = t1 + t1 - t1 * t1 - Vector<float>.One;

        var c2 = Vector.GreaterThan(t, Vector<float>.One - dt);
        var t2 = (t - Vector<float>.One) * invDt;
        var r2 = t2 * t2 + t2 + t2 + Vector<float>.One;

        var r = Vector.ConditionalSelect(c1, r1, Vector<float>.Zero);
        return Vector.ConditionalSelect(c2, r2, r);
    }

    /// <summary>Lane-wise LUT sine (gather). Used only for the sine unison path.</summary>
    private static Vector<float> SinTurns(Vector<float> turns)
    {
        Span<float> tmp = stackalloc float[Vector<float>.Count];
        turns.CopyTo(tmp);
        for (int i = 0; i < tmp.Length; i++)
            tmp[i] = FastTrig.SinTurns(tmp[i]);
        return new Vector<float>(tmp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Triangle(float phase) => 4f * MathF.Abs(phase - 0.5f) - 1f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PolyBlepSaw(float t, float dt) => 2f * t - 1f - PolyBlep(t, dt);

    private static float PolyBlepPulse(float t, float dt, float duty)
    {
        float value = t < duty ? 1f : -1f;
        value += PolyBlep(t, dt);
        float tFall = t - duty;
        if (tFall < 0f) tFall += 1f;
        value -= PolyBlep(tFall, dt);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PolyBlep(float t, float dt)
    {
        if (t < dt)
        {
            t /= dt;
            return t + t - t * t - 1f;
        }
        if (t > 1f - dt)
        {
            t = (t - 1f) / dt;
            return t * t + t + t + 1f;
        }
        return 0f;
    }
}
