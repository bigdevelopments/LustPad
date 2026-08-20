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
    private readonly float _invSampleRate;
    private readonly float _maxDelaySamples;

    private float _lpfL;
    private float _lpfR;
    private readonly float _lpfCoeff;

    private bool _active;
    private float _dryGain = 1f;
    private float _wetGain;
    private float _rateA, _rateB, _baseMsA, _baseMsB, _depthA, _depthB;
    private bool _dual;

    public Chorus(float sampleRate, int seed)
    {
        _sampleRate = sampleRate;
        _invSampleRate = 1f / sampleRate;
        _size = Math.Max(128, (int)(sampleRate * 0.08));
        _buffer = new float[_size];
        _maxDelaySamples = Math.Min(_size - 2, sampleRate * 0.03f);
        _ = seed;

        float fc = 6500f;
        _lpfCoeff = 1f - MathF.Exp(-2f * MathF.PI * fc / sampleRate);
    }

    public void Prepare(
        float mix, float rateHz, float depthMs, ChorusMode mode,
        float loopLenSec, bool lockToLoop)
    {
        mix = Math.Clamp(mix, 0f, 1f);
        _active = mix > 0.001f;
        _dryGain = 1f - mix * 0.45f;
        _wetGain = mix * 0.72f;
        if (!_active)
            return;

        GetModeShape(mode, rateHz, depthMs,
            out _rateA, out _rateB,
            out _baseMsA, out _baseMsB,
            out _depthA, out _depthB,
            out _dual);

        if (lockToLoop && loopLenSec >= 0.5f)
        {
            _rateA = LoopEvolution.SnapRate(_rateA, loopLenSec);
            _rateB = _dual ? LoopEvolution.SnapRate(_rateB, loopLenSec) : _rateA;
        }
    }

    public (float left, float right) Process(float left, float right, long sampleIndex)
    {
        float mono = (left + right) * 0.5f;
        _buffer[_write] = mono;

        if (!_active)
        {
            AdvanceWrite();
            return (left, right);
        }

        float modA = SinAt(sampleIndex, _rateA, phase0: 0f);
        float wetL = Read(_baseMsA, modA, _depthA);
        float wetR = Read(_baseMsA, -modA, _depthA);
        if (_dual)
        {
            float modB = SinAt(sampleIndex, _rateB, phase0: 0.25f);
            wetL += Read(_baseMsB, modB, _depthB) * 0.85f;
            wetR += Read(_baseMsB, -modB, _depthB) * 0.85f;
            wetL *= 0.62f;
            wetR *= 0.62f;
        }

        _lpfL += _lpfCoeff * (wetL - _lpfL);
        _lpfR += _lpfCoeff * (wetR - _lpfR);

        AdvanceWrite();
        return (left * _dryGain + _lpfL * _wetGain, right * _dryGain + _lpfR * _wetGain);
    }

    private static void GetModeShape(
        ChorusMode mode, float rateHz, float depthMs,
        out float rateA, out float rateB,
        out float baseMsA, out float baseMsB,
        out float depthA, out float depthB,
        out bool dual)
    {
        float rate = Math.Clamp(rateHz, 0.02f, 3f);
        float depth = Math.Clamp(depthMs, 0.2f, 10f);

        switch (mode)
        {
            case ChorusMode.JunoII:
                dual = false;
                rateA = rate * 1.35f;
                rateB = rateA;
                baseMsA = 4.2f;
                baseMsB = baseMsA;
                depthA = depth * 0.75f;
                depthB = depthA;
                break;

            case ChorusMode.JunoIPlusII:
                dual = true;
                rateA = rate * 0.85f;
                rateB = rate * 1.4f + 0.05f;
                baseMsA = 5.4f;
                baseMsB = 7.1f;
                depthA = depth * 0.95f;
                depthB = depth * 0.65f;
                break;

            default:
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

    private float SinAt(long sampleIndex, float rateHz, float phase0) =>
        FastTrig.SinTurns(phase0 + rateHz * sampleIndex * _invSampleRate);

    private float Read(float baseMs, float bipolarMod, float depthMs)
    {
        float delayMs = Math.Clamp(baseMs + bipolarMod * depthMs, 0.8f, 28f);
        return ReadSamples(delayMs * 0.001f * _sampleRate);
    }

    private void AdvanceWrite()
    {
        _write++;
        if (_write >= _size)
            _write = 0;
    }

    private float ReadSamples(float delaySamples)
    {
        delaySamples = Math.Clamp(delaySamples, 1f, _maxDelaySamples);

        float readPos = _write - delaySamples;
        int size = _size;
        readPos %= size;
        if (readPos < 0)
            readPos += size;

        int i0 = (int)readPos;
        int i1 = i0 + 1;
        if (i1 >= size)
            i1 = 0;

        float frac = readPos - i0;
        return _buffer[i0] * (1f - frac) + _buffer[i1] * frac;
    }
}
