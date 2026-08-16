using LustPad.Core.Audio;

namespace LustPad.Core.Synthesis;

/// <summary>
/// Juno-106-style stereo BBD chorus: mono sum → modulated delay(s) →
/// L/R with inverted LFO phase, mild BBD low-pass, dry/wet mix.
/// Modes mirror the classic I / II / I+II topologies.
/// </summary>
internal sealed class Chorus
{
    private readonly float[] _buffer;
    private readonly int _size;
    private int _write;
    private readonly float _sampleRate;
    private readonly float _maxDelaySamples;

    // One-pole BBD bandwidth (dark swirl, not bright modern chorus)
    private float _lpfL;
    private float _lpfR;
    private readonly float _lpfCoeff;

    public Chorus(float sampleRate, int seed)
    {
        _sampleRate = sampleRate;
        // 80 ms ring — well above max Juno-ish delay (~15 ms modulated)
        _size = Math.Max(128, (int)(sampleRate * 0.08));
        _buffer = new float[_size];
        _maxDelaySamples = Math.Min(_size - 2, sampleRate * 0.03f); // 30 ms cap
        _ = seed; // reserved for future BBD noise / calibration per-seed

        // ~6.5 kHz one-pole @ 48 k (scales with rate)
        float fc = 6500f;
        _lpfCoeff = 1f - MathF.Exp(-2f * MathF.PI * fc / sampleRate);
    }

    /// <param name="sampleIndex">Absolute sample index for loop-locked LFO phase.</param>
    public (float left, float right) Process(
        float left, float right,
        float mix, float rateHz, float depthMs,
        ChorusMode mode, long sampleIndex,
        float loopLenSec = 0f, bool lockToLoop = false)
    {
        mix = Math.Clamp(mix, 0f, 1f);

        // Mono feed — classic BBD chorus sums before the bucket line
        float mono = (left + right) * 0.5f;
        _buffer[_write] = mono;

        if (mix <= 0.001f)
        {
            AdvanceWrite();
            return (left, right);
        }

        // Mode shapes base delay, rate scale, and topology
        GetModeShape(mode, rateHz, depthMs,
            out float rateA, out float rateB,
            out float baseMsA, out float baseMsB,
            out float depthA, out float depthB,
            out bool dual);

        // Snap *after* I / II / I+II scale so the wrap is still an integer cycle.
        if (lockToLoop && loopLenSec >= 0.5f)
        {
            rateA = LoopEvolution.SnapRate(rateA, loopLenSec);
            rateB = dual ? LoopEvolution.SnapRate(rateB, loopLenSec) : rateA;
        }

        // LFO: opposite phase on L/R (180°) for the wide Juno image
        float modA = SinAt(sampleIndex, rateA, phase0: 0f);
        float modAOpp = -modA;
        float modB = 0f, modBOpp = 0f;
        if (dual)
        {
            modB = SinAt(sampleIndex, rateB, phase0: 0.25f);
            modBOpp = -modB;
        }

        float wetL = Read(baseMsA, modA, depthA);
        float wetR = Read(baseMsA, modAOpp, depthA);
        if (dual)
        {
            wetL += Read(baseMsB, modB, depthB) * 0.85f;
            wetR += Read(baseMsB, modBOpp, depthB) * 0.85f;
            wetL *= 0.62f;
            wetR *= 0.62f;
        }

        // BBD-ish bandwidth limit on wet only
        _lpfL += _lpfCoeff * (wetL - _lpfL);
        _lpfR += _lpfCoeff * (wetR - _lpfR);
        wetL = _lpfL;
        wetR = _lpfR;

        AdvanceWrite();

        // Juno sits fairly wet when "on"; keep dry present for pad headroom
        float dryGain = 1f - mix * 0.45f;
        float wetGain = mix * 0.72f;
        return (left * dryGain + wetL * wetGain, right * dryGain + wetR * wetGain);
    }

    private static void GetModeShape(
        ChorusMode mode, float rateHz, float depthMs,
        out float rateA, out float rateB,
        out float baseMsA, out float baseMsB,
        out float depthA, out float depthB,
        out bool dual)
    {
        // User rate/depth remain expressive; modes set Juno-like centres.
        float rate = Math.Clamp(rateHz, 0.02f, 3f);
        float depth = Math.Clamp(depthMs, 0.2f, 10f);

        switch (mode)
        {
            case ChorusMode.JunoII:
                // Faster, slightly shallower — "II"
                dual = false;
                rateA = rate * 1.35f;
                rateB = rateA;
                baseMsA = 4.2f;
                baseMsB = baseMsA;
                depthA = depth * 0.75f;
                depthB = depthA;
                break;

            case ChorusMode.JunoIPlusII:
                // Both lines — thick classic I+II
                dual = true;
                rateA = rate * 0.85f;          // slower primary
                rateB = rate * 1.4f + 0.05f; // faster secondary
                baseMsA = 5.4f;
                baseMsB = 7.1f;
                depthA = depth * 0.95f;
                depthB = depth * 0.65f;
                break;

            default: // JunoI
                // Slower, deeper swirl — default lush
                dual = false;
                rateA = rate * 0.9f;
                rateB = rateA;
                baseMsA = 5.6f;
                baseMsB = baseMsA;
                depthA = depth;
                depthB = depthA;
                break;
        }
    }

    private float SinAt(long sampleIndex, float rateHz, float phase0)
    {
        if (rateHz <= 1e-7f)
            return MathF.Sin(phase0 * MathF.PI * 2f);
        double phase = phase0 + rateHz * sampleIndex / _sampleRate;
        phase -= Math.Floor(phase);
        return MathF.Sin((float)(phase * Math.PI * 2.0));
    }

    private float Read(float baseMs, float bipolarMod, float depthMs)
    {
        float delayMs = baseMs + bipolarMod * depthMs;
        delayMs = Math.Clamp(delayMs, 0.8f, 28f);
        float delaySamples = delayMs * 0.001f * _sampleRate;
        return ReadSamples(delaySamples);
    }

    private void AdvanceWrite()
    {
        _write++;
        if (_write >= _size)
            _write = 0;
    }

    private float ReadSamples(float delaySamples)
    {
        if (_size < 2)
            return 0f;

        if (float.IsNaN(delaySamples) || float.IsInfinity(delaySamples))
            delaySamples = 1f;

        delaySamples = Math.Clamp(delaySamples, 1f, _maxDelaySamples);

        double readPos = _write - (double)delaySamples;
        readPos %= _size;
        if (readPos < 0)
            readPos += _size;

        int i0 = (int)readPos;
        if ((uint)i0 >= (uint)_size)
            i0 = 0;

        int i1 = i0 + 1;
        if (i1 >= _size)
            i1 = 0;

        float frac = (float)(readPos - i0);
        if (frac < 0f) frac = 0f;
        else if (frac > 1f) frac = 1f;

        return _buffer[i0] * (1f - frac) + _buffer[i1] * frac;
    }
}
