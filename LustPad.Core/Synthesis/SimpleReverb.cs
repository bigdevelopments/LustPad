namespace LustPad.Core.Synthesis;

/// <summary>
/// Feedback delay network with predelay and high-frequency damping for lush pad space.
/// </summary>
internal sealed class SimpleReverb
{
    private readonly DelayLine[] _lines;
    private readonly float[] _feedback;
    private readonly float[] _dampState;
    private readonly DelayLine _predelayL;
    private readonly DelayLine _predelayR;
    private readonly float _sampleRate;

    public SimpleReverb(float sampleRate, int seed)
    {
        _sampleRate = sampleRate;
        // Longer, more diffuse network than the original 6-tap sketch
        int[] ms = [19, 23, 29, 37, 43, 53, 61, 71, 83, 97];
        var rng = new Random(seed ^ 0x5f3759df);
        _lines = new DelayLine[ms.Length];
        _feedback = new float[ms.Length];
        _dampState = new float[ms.Length];

        for (int i = 0; i < ms.Length; i++)
        {
            float jitter = 0.88f + (float)rng.NextDouble() * 0.24f;
            int len = Math.Max(2, (int)(ms[i] * 0.001f * sampleRate * jitter));
            _lines[i] = new DelayLine(len);
            _feedback[i] = 0.52f + i * 0.028f;
        }

        int pre = Math.Max(1, (int)(0.001f * sampleRate)); // default 1 ms; Process updates conceptually via mix of length
        _predelayL = new DelayLine(Math.Max(2, (int)(0.08f * sampleRate))); // up to 80 ms
        _predelayR = new DelayLine(Math.Max(2, (int)(0.08f * sampleRate)));
        _ = pre;
    }

    public (float left, float right) Process(
        float left, float right, float mix, float decay, float damping, float predelayMs)
    {
        mix = Math.Clamp(mix, 0f, 1f);
        decay = Math.Clamp(decay, 0.1f, 0.96f);
        damping = Math.Clamp(damping, 0f, 0.95f);
        if (mix <= 0.001f)
            return (left, right);

        int preSamples = Math.Clamp((int)(predelayMs * 0.001f * _sampleRate), 1, _predelayL.Length - 2);
        _predelayL.Write(left);
        _predelayR.Write(right);
        float inL = _predelayL.ReadDelayed(preSamples);
        float inR = _predelayR.ReadDelayed(preSamples);
        float input = (inL + inR) * 0.5f;

        float accL = 0, accR = 0;
        float dampCoeff = 1f - damping * 0.55f;

        for (int i = 0; i < _lines.Length; i++)
        {
            float fb = _feedback[i] * decay;
            float delayed = _lines[i].Read();
            // One-pole high damping in the loop
            _dampState[i] += dampCoeff * (delayed - _dampState[i]);
            float y = input + _dampState[i] * fb;
            y *= 0.985f;
            _lines[i].Write(y);

            if ((i & 1) == 0) accL += y;
            else accR += y;
        }

        float norm = 1f / (_lines.Length * 0.5f);
        accL *= norm;
        accR *= norm;

        // Mild stereo cross-feed for width inside the space
        float wetL = accL * 0.85f + accR * 0.15f;
        float wetR = accR * 0.85f + accL * 0.15f;

        return (
            left * (1f - mix) + wetL * mix,
            right * (1f - mix) + wetR * mix
        );
    }

    private sealed class DelayLine
    {
        private readonly float[] _buf;
        private int _pos;

        public int Length => _buf.Length;

        public DelayLine(int length) => _buf = new float[length];

        public float Read() => _buf[_pos];

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
