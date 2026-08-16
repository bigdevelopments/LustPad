namespace LustPad.Core.Synthesis;

/// <summary>
/// Full-wet vocal path: parallel formant resonators + vowel tilt.
/// The synth mixes this with the normal filtered-oscillator path via FormantAmount —
/// this class does not dry/wet blend by itself.
///
/// The formant path is the vowel (not "pad plus a little EQ"). A through-path or
/// wide residual low-pass lets the unshaped fundamental dominate; after the synth's
/// peak-normalise the sliders then do almost nothing. Resonators only, then tilt.
/// </summary>
internal sealed class FormantFilter
{
    // Sung-ish adult targets. Gains are resonator peak dB relative to each other.
    // Tilt is a high shelf on the resonator sum — the broad ooh↔ahh↔ee gesture.
    private static readonly VowelSpec[] Vowels =
    [
        //         F1   F2    F3   G1    G2    G3   tiltHz  tiltDb  bodyDb
        new("Oo", 320,  780, 2410, 10.0f, 5.5f, 1.5f, 2300f, -8.0f,  2.5f),
        new("Oh", 460,  850, 2540,  9.0f, 6.5f, 2.0f, 2600f, -4.5f,  1.0f),
        new("Ah", 730, 1210, 2600,  7.0f, 11.0f, 3.5f, 3100f,  0.0f,  0.0f),
        new("Eh", 530, 1840, 2750,  6.0f, 11.0f, 4.5f, 3400f,  3.5f, -2.5f),
        new("Ee", 270, 2260, 3010,  5.0f, 12.5f, 5.5f, 3600f,  6.0f, -5.0f),
    ];

    private readonly float _sampleRate;
    private readonly BandPass[] _bp1 = [new(), new()];
    private readonly BandPass[] _bp2 = [new(), new()];
    private readonly BandPass[] _bp3 = [new(), new()];
    private readonly Shelf[] _tilt = [new(), new()];
    private readonly Shelf[] _body = [new(), new()];

    private float _a1 = 1f, _a2 = 0.7f, _a3 = 0.3f;
    private float _makeup = 1f;

    public FormantFilter(float sampleRate)
    {
        _sampleRate = sampleRate;
    }

    /// <param name="vowel">0 = Oo … 1 = Ee</param>
    /// <param name="shift">scales formant frequencies (head size)</param>
    /// <param name="resonance">0..1 → Q of the formant bells (more = more sung / peaky)</param>
    public void Configure(float vowel, float shift, float resonance)
    {
        Interpolate(vowel,
            out float f1, out float f2, out float f3,
            out float g1, out float g2, out float g3,
            out float tiltHz, out float tiltDb, out float bodyDb);

        shift = Math.Clamp(shift, 0.55f, 1.8f);
        f1 *= shift;
        f2 *= shift;
        f3 *= shift;
        tiltHz *= MathF.Sqrt(shift);

        // Wide enough at the low end that mix=1 is a vowel, not a whistle.
        // Q slider still has a wide, obvious range.
        float qBase = 1.7f + Math.Clamp(resonance, 0f, 1f) * 6.2f;
        float q1 = qBase * 0.8f;
        float q2 = qBase;
        float q3 = qBase * 1.08f;

        _a1 = DbToLin(g1);
        _a2 = DbToLin(g2);
        _a3 = DbToLin(g3);

        // Peak-match the loudest resonator so the path competes with the LPF pad
        // in a linear crossfade (otherwise 8% leftover pad buries the vowel).
        float peakLin = MathF.Max(_a1, MathF.Max(_a2, _a3));
        _makeup = 0.95f / MathF.Max(0.25f, peakLin);

        _bp1[0].Set(_sampleRate, f1, q1);
        _bp2[0].Set(_sampleRate, f2, q2);
        _bp3[0].Set(_sampleRate, f3, q3);
        _bp1[1].Set(_sampleRate, f1 * 1.014f, q1);
        _bp2[1].Set(_sampleRate, f2 * 0.986f, q2);
        _bp3[1].Set(_sampleRate, f3 * 1.01f, q3);

        _tilt[0].SetHigh(_sampleRate, tiltHz, tiltDb);
        _tilt[1].SetHigh(_sampleRate, tiltHz * 1.04f, tiltDb);
        _body[0].SetLow(_sampleRate, 210f, bodyDb);
        _body[1].SetLow(_sampleRate, 195f, bodyDb);
    }

    /// <summary>Full-wet formant sculpt (caller mixes with the normal pad path).</summary>
    public (float left, float right) Process(float left, float right)
    {
        return (Sculpt(left, 0), Sculpt(right, 1));
    }

    private float Sculpt(float x, int channel)
    {
        float f =
            _bp1[channel].Process(x) * _a1 +
            _bp2[channel].Process(x) * _a2 +
            _bp3[channel].Process(x) * _a3;

        f *= _makeup;
        f = _tilt[channel].Process(f);
        f = _body[channel].Process(f);

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

    private static float DbToLin(float db) => MathF.Pow(10f, db / 20f);

    private static void Interpolate(
        float vowel,
        out float f1, out float f2, out float f3,
        out float g1, out float g2, out float g3,
        out float tiltHz, out float tiltDb, out float bodyDb)
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
        g1 = Lerp(a.G1Db, b.G1Db, t);
        g2 = Lerp(a.G2Db, b.G2Db, t);
        g3 = Lerp(a.G3Db, b.G3Db, t);
        tiltHz = Lerp(a.TiltHz, b.TiltHz, t);
        tiltDb = Lerp(a.TiltDb, b.TiltDb, t);
        bodyDb = Lerp(a.BodyDb, b.BodyDb, t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private readonly record struct VowelSpec(
        string Name,
        float F1, float F2, float F3,
        float G1Db, float G2Db, float G3Db,
        float TiltHz, float TiltDb, float BodyDb);

    /// <summary>RBJ constant 0 dB peak-gain bandpass. Amplitude is applied by the caller.</summary>
    private sealed class BandPass
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _z1, _z2;

        public void Set(float sampleRate, float freqHz, float q)
        {
            freqHz = Math.Clamp(freqHz, 50f, sampleRate * 0.45f);
            q = Math.Clamp(q, 0.6f, 20f);
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float cos = MathF.Cos(w0);
            float sin = MathF.Sin(w0);
            float alpha = sin / (2f * q);

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

    /// <summary>RBJ low or high shelf.</summary>
    private sealed class Shelf
    {
        private float _b0 = 1, _b1, _b2, _a1, _a2;
        private float _z1, _z2;

        public void SetLow(float sampleRate, float freqHz, float gainDb) =>
            Set(sampleRate, freqHz, gainDb, high: false);

        public void SetHigh(float sampleRate, float freqHz, float gainDb) =>
            Set(sampleRate, freqHz, gainDb, high: true);

        private void Set(float sampleRate, float freqHz, float gainDb, bool high)
        {
            if (MathF.Abs(gainDb) < 0.04f)
            {
                _b0 = 1; _b1 = 0; _b2 = 0; _a1 = 0; _a2 = 0;
                return;
            }

            freqHz = Math.Clamp(freqHz, 40f, sampleRate * 0.42f);
            float A = MathF.Pow(10f, gainDb / 40f);
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float cos = MathF.Cos(w0);
            float sin = MathF.Sin(w0);
            const float S = 1f;
            float alpha = sin / 2f * MathF.Sqrt((A + 1f / A) * (1f / S - 1f) + 2f);
            float twoSqrtAalpha = 2f * MathF.Sqrt(A) * alpha;

            float b0, b1, b2, a0, a1, a2;
            if (high)
            {
                b0 = A * ((A + 1f) + (A - 1f) * cos + twoSqrtAalpha);
                b1 = -2f * A * ((A - 1f) + (A + 1f) * cos);
                b2 = A * ((A + 1f) + (A - 1f) * cos - twoSqrtAalpha);
                a0 = (A + 1f) - (A - 1f) * cos + twoSqrtAalpha;
                a1 = 2f * ((A - 1f) - (A + 1f) * cos);
                a2 = (A + 1f) - (A - 1f) * cos - twoSqrtAalpha;
            }
            else
            {
                b0 = A * ((A + 1f) - (A - 1f) * cos + twoSqrtAalpha);
                b1 = 2f * A * ((A - 1f) - (A + 1f) * cos);
                b2 = A * ((A + 1f) - (A - 1f) * cos - twoSqrtAalpha);
                a0 = (A + 1f) + (A - 1f) * cos + twoSqrtAalpha;
                a1 = -2f * ((A - 1f) + (A + 1f) * cos);
                a2 = (A + 1f) + (A - 1f) * cos - twoSqrtAalpha;
            }

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
