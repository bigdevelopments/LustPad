namespace LustPad.Core.Synthesis;

/// <summary>
/// Full-wet vocal path: parallel bandpass formant bank (ooh/ahh/ee).
/// The synth mixes this with the normal filtered-oscillator path via FormantAmount —
/// this class does not dry/wet blend by itself.
/// </summary>
internal sealed class FormantFilter
{
    // Adult-ish sung targets. Amps are linear gains on bandpass outputs.
    // Residual LP + residual mix keep closed vowels from sounding too thin.
    private static readonly VowelSpec[] Vowels =
    [
        //         F1   F2    F3   A1    A2    A3   residLpHz  residMix  bodyDb
        new("Oo", 300,  620, 2400, 1.55f, 0.55f, 0.12f,  900f, 0.12f,  5.0f),
        new("Oh", 450,  800, 2500, 1.35f, 0.75f, 0.22f, 1200f, 0.18f,  3.5f),
        new("Ah", 780, 1250, 2600, 1.15f, 1.40f, 0.45f, 2400f, 0.28f,  1.5f),
        new("Eh", 530, 1850, 2700, 0.95f, 1.35f, 0.55f, 3200f, 0.32f,  0.8f),
        new("Ee", 270, 2300, 3100, 0.85f, 1.50f, 0.65f, 4200f, 0.35f,  0.5f),
    ];

    private readonly float _sampleRate;
    private readonly BandPass[] _bp1 = [new(), new()];
    private readonly BandPass[] _bp2 = [new(), new()];
    private readonly BandPass[] _bp3 = [new(), new()];
    private readonly BiquadFilter _residL;
    private readonly BiquadFilter _residR;
    private readonly LowShelf[] _body = [new(), new()];

    private float _a1 = 1f, _a2 = 0.5f, _a3 = 0.2f;
    private float _residMix = 0.2f;
    private float _makeup = 1f;

    public FormantFilter(float sampleRate)
    {
        _sampleRate = sampleRate;
        _residL = new BiquadFilter(sampleRate);
        _residR = new BiquadFilter(sampleRate);
        _residL.SetLowPass(1200f, 0.7f);
        _residR.SetLowPass(1200f, 0.7f);
    }

    /// <param name="vowel">0 = Oo … 1 = Ee</param>
    /// <param name="shift">scales formant frequencies</param>
    /// <param name="resonance">0..1 → Q of bandpasses (more = more sung / peaky)</param>
    public void Configure(float vowel, float shift, float resonance)
    {
        Interpolate(vowel,
            out float f1, out float f2, out float f3,
            out float a1, out float a2, out float a3,
            out float residLp, out float residMix, out float bodyDb);

        shift = Math.Clamp(shift, 0.55f, 1.8f);
        f1 *= shift;
        f2 *= shift;
        f3 *= shift;
        residLp *= MathF.Sqrt(shift); // mild coupling

        // Higher Q = clearer vowel peaks (was too polite before)
        float qBase = 3.5f + Math.Clamp(resonance, 0f, 1f) * 10f;
        float q1 = qBase * 0.85f;
        float q2 = qBase;
        float q3 = qBase * 1.1f;

        _a1 = a1;
        _a2 = a2;
        _a3 = a3;
        _residMix = residMix;

        // Makeup so parallel BP sum sits near unity before amount mix
        float ampSum = a1 + a2 + a3 + 0.15f;
        _makeup = 1.15f / MathF.Sqrt(ampSum);

        _bp1[0].Set(_sampleRate, f1, q1);
        _bp2[0].Set(_sampleRate, f2, q2);
        _bp3[0].Set(_sampleRate, f3, q3);
        _bp1[1].Set(_sampleRate, f1 * 1.015f, q1);
        _bp2[1].Set(_sampleRate, f2 * 0.985f, q2);
        _bp3[1].Set(_sampleRate, f3 * 1.01f, q3);

        _residL.SetLowPass(Math.Clamp(residLp, 200f, _sampleRate * 0.45f), 0.65f);
        _residR.SetLowPass(Math.Clamp(residLp * 1.04f, 200f, _sampleRate * 0.45f), 0.65f);
        _body[0].Set(_sampleRate, 160f, bodyDb);
        _body[1].Set(_sampleRate, 155f, bodyDb);
    }

    /// <summary>Full-wet formant sculpt (caller mixes with the normal pad path).</summary>
    public (float left, float right) Process(float left, float right)
    {
        return (Sculpt(left, 0), Sculpt(right, 1));
    }

    private float Sculpt(float x, int channel)
    {
        // Parallel formant resonators (the actual vowel)
        float f =
            _bp1[channel].Process(x) * _a1 +
            _bp2[channel].Process(x) * _a2 +
            _bp3[channel].Process(x) * _a3;

        f *= _makeup;

        // Soft residual body (LP'd source) so closed vowels aren't thin
        float resid = channel == 0 ? _residL.Process(x) : _residR.Process(x);
        resid = _body[channel].Process(resid);
        f += resid * _residMix;

        // Soft clip peaks from high-Q resonators
        if (f > 1.2f || f < -1.2f)
            f = MathF.Tanh(f * 0.85f);

        return f;
    }

    public static string VowelLabel(float vowel)
    {
        vowel = Math.Clamp(vowel, 0f, 1f);
        float scaled = vowel * (Vowels.Length - 1);
        int i0 = (int)scaled;
        int i1 = Math.Min(i0 + 1, Vowels.Length - 1);
        float t = scaled - i0;
        return t < 0.5f ? Vowels[i0].Name : Vowels[i1].Name;
    }

    private static void Interpolate(
        float vowel,
        out float f1, out float f2, out float f3,
        out float a1, out float a2, out float a3,
        out float residLp, out float residMix, out float bodyDb)
    {
        vowel = Math.Clamp(vowel, 0f, 1f);
        float scaled = vowel * (Vowels.Length - 1);
        int i0 = (int)scaled;
        int i1 = Math.Min(i0 + 1, Vowels.Length - 1);
        float t = scaled - i0;

        var a = Vowels[i0];
        var b = Vowels[i1];
        f1 = Lerp(a.F1, b.F1, t);
        f2 = Lerp(a.F2, b.F2, t);
        f3 = Lerp(a.F3, b.F3, t);
        a1 = Lerp(a.A1, b.A1, t);
        a2 = Lerp(a.A2, b.A2, t);
        a3 = Lerp(a.A3, b.A3, t);
        residLp = Lerp(a.ResidLpHz, b.ResidLpHz, t);
        residMix = Lerp(a.ResidMix, b.ResidMix, t);
        bodyDb = Lerp(a.BodyDb, b.BodyDb, t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private readonly record struct VowelSpec(
        string Name,
        float F1, float F2, float F3,
        float A1, float A2, float A3,
        float ResidLpHz, float ResidMix, float BodyDb);

    /// <summary>RBJ constant peak-gain (0 dB) bandpass — stable makeup for formant sum.</summary>
    private sealed class BandPass
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _z1, _z2;

        public void Set(float sampleRate, float freqHz, float q)
        {
            freqHz = Math.Clamp(freqHz, 50f, sampleRate * 0.45f);
            q = Math.Clamp(q, 0.7f, 22f);
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float cos = MathF.Cos(w0);
            float sin = MathF.Sin(w0);
            float alpha = sin / (2f * q);

            // Constant 0 dB peak-gain bandpass
            float b0 = alpha;
            float b1 = 0f;
            float b2 = -alpha;
            float a0 = 1f + alpha;
            float a1 = -2f * cos;
            float a2 = 1f - alpha;

            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public float Process(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }

    private sealed class LowShelf
    {
        private float _b0 = 1, _b1, _b2, _a1, _a2;
        private float _z1, _z2;

        public void Set(float sampleRate, float freqHz, float gainDb)
        {
            if (MathF.Abs(gainDb) < 0.05f)
            {
                _b0 = 1; _b1 = 0; _b2 = 0; _a1 = 0; _a2 = 0;
                return;
            }

            freqHz = Math.Clamp(freqHz, 40f, sampleRate * 0.25f);
            float A = MathF.Pow(10f, gainDb / 40f);
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float cos = MathF.Cos(w0);
            float sin = MathF.Sin(w0);
            const float S = 1f;
            float alpha = sin / 2f * MathF.Sqrt((A + 1f / A) * (1f / S - 1f) + 2f);
            float twoSqrtAalpha = 2f * MathF.Sqrt(A) * alpha;

            float b0 = A * ((A + 1f) - (A - 1f) * cos + twoSqrtAalpha);
            float b1 = 2f * A * ((A - 1f) - (A + 1f) * cos);
            float b2 = A * ((A + 1f) - (A - 1f) * cos - twoSqrtAalpha);
            float a0 = (A + 1f) + (A - 1f) * cos + twoSqrtAalpha;
            float a1 = -2f * ((A - 1f) + (A + 1f) * cos);
            float a2 = (A + 1f) + (A - 1f) * cos - twoSqrtAalpha;

            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public float Process(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}
