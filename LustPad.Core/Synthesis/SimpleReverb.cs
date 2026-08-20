using LustPad.Core.Audio;

namespace LustPad.Core.Synthesis;

/// <summary>
/// Feedback delay network: longer prime delays, Householder diffusion,
/// HF damping, and slow loop-lockable delay modulation (less metallic, more air).
/// </summary>
internal sealed class SimpleReverb
{
    private const int Size = 12;

    private readonly ModDelay[] _lines;
    private readonly float[] _dampState = new float[Size];
    private readonly float[] _modRate = new float[Size];
    private readonly float[] _modPhase0 = new float[Size];
    private readonly float[] _scratch = new float[Size];
    private readonly DelayLine _predelayL;
    private readonly DelayLine _predelayR;
    private readonly float _sampleRate;
    private readonly float _invSampleRate;
    private readonly float _modDepthSamples;
    private bool _modReady;
    private bool _active;
    private float _mix, _g, _dampCoeff, _inj;
    private int _preSamples;

    public SimpleReverb(float sampleRate, int seed)
    {
        _sampleRate = sampleRate;
        _invSampleRate = 1f / sampleRate;
        // Longer than the old ~19–97 ms tank — pad space, not a small room.
        int[] ms = [37, 43, 53, 67, 79, 97, 113, 131, 149, 173, 193, 223];
        var rng = new Random(seed ^ 0x5f3759df);
        _lines = new ModDelay[Size];
        _modDepthSamples = 0.00028f * sampleRate; // ~0.28 ms

        for (int i = 0; i < Size; i++)
        {
            float jitter = 0.93f + (float)rng.NextDouble() * 0.14f;
            int len = Math.Max(8, (int)(ms[i] * 0.001f * sampleRate * jitter));
            int extra = Math.Max(8, (int)(_modDepthSamples * 2f) + 4);
            _lines[i] = new ModDelay(len + extra, len);
            _modRate[i] = 0.052f + i * 0.012f;
            _modPhase0[i] = (float)rng.NextDouble();
        }

        _predelayL = new DelayLine(Math.Max(2, (int)(0.08f * sampleRate)));
        _predelayR = new DelayLine(Math.Max(2, (int)(0.08f * sampleRate)));
    }

    public void Prepare(
        float mix, float decay, float damping, float predelayMs,
        float loopLenSec, bool lockToLoop)
    {
        _mix = Math.Clamp(mix, 0f, 1f);
        _active = _mix > 0.001f;
        decay = Math.Clamp(decay, 0.1f, 0.96f);
        damping = Math.Clamp(damping, 0f, 0.95f);
        _dampCoeff = 1f - damping * 0.55f;
        _g = Math.Clamp(0.52f + decay * 0.45f, 0.4f, 0.97f);
        _inj = 0.38f / MathF.Sqrt(Size);
        _preSamples = Math.Clamp((int)(predelayMs * 0.001f * _sampleRate), 1, _predelayL.Length - 2);
        if (_active)
            EnsureModRates(loopLenSec, lockToLoop);
    }

    public (float left, float right) Process(float left, float right, long sampleIndex)
    {
        if (!_active)
            return (left, right);

        _predelayL.Write(left);
        _predelayR.Write(right);
        float inL = _predelayL.ReadDelayed(_preSamples);
        float inR = _predelayR.ReadDelayed(_preSamples);
        float input = (inL + inR) * 0.5f;

        float dampCoeff = _dampCoeff;
        float g = _g;
        float inj = input * _inj;

        float sum = 0f;
        for (int i = 0; i < Size; i++)
        {
            float mod = _modDepthSamples * SinAt(sampleIndex, _modRate[i], _modPhase0[i]);
            float y = _lines[i].Read(mod);
            _dampState[i] += dampCoeff * (y - _dampState[i]);
            _scratch[i] = _dampState[i];
            sum += _scratch[i];
        }

        // Householder diffusion: H = I − (2/N) 11ᵀ — orthonormal, cheap, breaks metallic repeats.
        float house = (2f / Size) * sum;
        float accL = 0, accR = 0;
        for (int i = 0; i < Size; i++)
        {
            float w = (_scratch[i] - house) * g + inj;
            _lines[i].Write(w);
            if ((i & 1) == 0) accL += _scratch[i];
            else accR += _scratch[i];
        }

        float norm = 1f / (Size * 0.5f);
        accL *= norm;
        accR *= norm;

        float wetL = accL * 0.85f + accR * 0.15f;
        float wetR = accR * 0.85f + accL * 0.15f;

        float dry = 1f - _mix;
        return (
            left * dry + wetL * _mix,
            right * dry + wetR * _mix
        );
    }

    private void EnsureModRates(float loopLenSec, bool lockToLoop)
    {
        if (_modReady)
            return;
        if (lockToLoop && loopLenSec >= 0.5f)
        {
            for (int i = 0; i < Size; i++)
                _modRate[i] = LoopEvolution.SnapRate(_modRate[i], loopLenSec);
        }
        _modReady = true;
    }

    private float SinAt(long sampleIndex, float rateHz, float phase0) =>
        FastTrig.SinTurns(phase0 + rateHz * sampleIndex * _invSampleRate);

    private sealed class ModDelay
    {
        private readonly float[] _buf;
        private readonly int _baseDelay;
        private int _w;

        public ModDelay(int capacity, int baseDelay)
        {
            _buf = new float[Math.Max(capacity, baseDelay + 4)];
            _baseDelay = Math.Clamp(baseDelay, 2, _buf.Length - 3);
        }

        public void Write(float value)
        {
            _buf[_w] = value;
            _w++;
            if (_w >= _buf.Length)
                _w = 0;
        }

        public float Read(float extraSamples)
        {
            float delay = _baseDelay + extraSamples;
            delay = Math.Clamp(delay, 1f, _buf.Length - 2);
            double readPos = _w - (double)delay;
            int size = _buf.Length;
            readPos %= size;
            if (readPos < 0)
                readPos += size;

            int i0 = (int)readPos;
            int i1 = i0 + 1;
            if (i1 >= size)
                i1 = 0;
            float frac = (float)(readPos - i0);
            return _buf[i0] * (1f - frac) + _buf[i1] * frac;
        }
    }

    private sealed class DelayLine
    {
        private readonly float[] _buf;
        private int _pos;

        public int Length => _buf.Length;

        public DelayLine(int length) => _buf = new float[length];

        public float ReadDelayed(int delaySamples)
        {
            delaySamples = Math.Clamp(delaySamples, 0, _buf.Length - 1);
            int i = _pos - delaySamples;
            if (i < 0) i += _buf.Length;
            return _buf[i];
        }

        public void Write(float value)
        {
            _buf[_pos] = value;
            _pos++;
            if (_pos >= _buf.Length)
                _pos = 0;
        }
    }
}
