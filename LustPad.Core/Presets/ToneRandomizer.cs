namespace LustPad.Core.Presets;

/// <summary>
/// What to re-roll. Structure (note, duration, loop, export, quality) is never touched.
/// </summary>
public enum RandomizeScope
{
    /// <summary>Oscillators, filter, formants, noise, layer B, FX — a new pad colour.</summary>
    Tone = 0,
    /// <summary>Evolution / LFOs / drift / formant motion only.</summary>
    Motion = 1,
    /// <summary>Chorus, reverb, width, drive — space and glue.</summary>
    Space = 2,
    /// <summary>~±15% jitter around current tone values (keeps character).</summary>
    Subtle = 3,
}

/// <summary>
/// Controlled randomization for pad design. Never randomizes library structure:
/// MIDI note, duration, loop points, lock flags, oversample/bit-depth, stereo/export.
/// </summary>
public static class ToneRandomizer
{
    public static IReadOnlyList<string> ScopeLabels { get; } =
        ["Tone (colour)", "Motion only", "Space only", "Subtle (±)"];

    public static RandomizeScope ParseScope(string? label) => label switch
    {
        "Motion only" => RandomizeScope.Motion,
        "Space only" => RandomizeScope.Space,
        "Subtle (±)" => RandomizeScope.Subtle,
        _ => RandomizeScope.Tone,
    };

    public static string ScopeLabel(RandomizeScope scope) => scope switch
    {
        RandomizeScope.Motion => "Motion only",
        RandomizeScope.Space => "Space only",
        RandomizeScope.Subtle => "Subtle (±)",
        _ => "Tone (colour)",
    };

    public static PadParameters Randomize(PadParameters source, RandomizeScope scope, Random? rng = null)
    {
        rng ??= Random.Shared;
        var p = source.Clone();

        // Always new seed so unison phases / chorus differ even on subtle
        p.Seed = rng.Next(1, 999_999);

        switch (scope)
        {
            case RandomizeScope.Motion:
                RandomizeMotion(p, rng, full: true);
                break;
            case RandomizeScope.Space:
                RandomizeSpace(p, rng, full: true);
                break;
            case RandomizeScope.Subtle:
                JitterTone(p, rng, amount: 0.15f);
                JitterMotion(p, rng, amount: 0.12f);
                JitterSpace(p, rng, amount: 0.12f);
                break;
            default:
                RandomizeTone(p, rng);
                RandomizeMotion(p, rng, full: true);
                RandomizeSpace(p, rng, full: true);
                // Envelope shape mildly (still pad-like)
                p.AttackSeconds = Lerp(0.6f, 3.2f, rng);
                p.DecaySeconds = Lerp(0.4f, 2.0f, rng);
                p.SustainLevel = Lerp(0.7f, 0.95f, rng);
                break;
        }

        // Hard floor: structure must match source (belt-and-suspenders)
        PreserveStructure(source, p);
        return p;
    }

    /// <summary>Fields that must not change when randomizing (for tests / docs).</summary>
    public static readonly string[] PreservedFields =
    [
        nameof(PadParameters.MidiNote),
        nameof(PadParameters.FineTuneCents),
        nameof(PadParameters.DurationSeconds),
        nameof(PadParameters.LoopStartSeconds),
        nameof(PadParameters.CrossfadeSeconds),
        nameof(PadParameters.Stereo),
        nameof(PadParameters.EmbedLoopPoints),
        nameof(PadParameters.LockEvolutionToLoop),
        nameof(PadParameters.OptimizeLoopPoint),
        nameof(PadParameters.SamplerEnvelope),
        nameof(PadParameters.OversampleFactor),
        nameof(PadParameters.ExportBitDepth),
        nameof(PadParameters.Archival96kHz),
        nameof(PadParameters.Name),
        nameof(PadParameters.OutputGainDb),
        nameof(PadParameters.ReleaseSeconds),
    ];

    private static void PreserveStructure(PadParameters source, PadParameters p)
    {
        p.Name = source.Name;
        p.MidiNote = source.MidiNote;
        p.FineTuneCents = source.FineTuneCents;
        p.DurationSeconds = source.DurationSeconds;
        p.LoopStartSeconds = source.LoopStartSeconds;
        p.CrossfadeSeconds = source.CrossfadeSeconds;
        p.Stereo = source.Stereo;
        p.EmbedLoopPoints = source.EmbedLoopPoints;
        p.LockEvolutionToLoop = source.LockEvolutionToLoop;
        p.OptimizeLoopPoint = source.OptimizeLoopPoint;
        p.SamplerEnvelope = source.SamplerEnvelope;
        p.OversampleFactor = source.OversampleFactor;
        p.ExportBitDepth = source.ExportBitDepth;
        p.Archival96kHz = source.Archival96kHz;
        p.OutputGainDb = source.OutputGainDb;
        p.ReleaseSeconds = source.ReleaseSeconds; // sampler-facing, not tone
    }

    private static void RandomizeTone(PadParameters p, Random rng)
    {
        p.Waveform = (WaveformType)rng.Next(0, 6);
        p.UnisonVoices = rng.Next(4, 11);
        p.DetuneCents = Lerp(6f, 32f, rng);
        p.StereoSpread = Lerp(0.55f, 0.95f, rng);
        p.AttackBloom = Lerp(0.15f, 0.75f, rng);
        // Occasionally noise-led pads (low osc, higher noise is set below)
        p.OscLevel = Chance(rng, 0.12) ? Lerp(0.15f, 0.55f, rng) : Lerp(0.7f, 1.15f, rng);
        p.SubLevel = Lerp(0.05f, 0.45f, rng);
        p.FifthLevel = Chance(rng, 0.45) ? Lerp(0.04f, 0.2f, rng) : Lerp(0f, 0.06f, rng);
        p.OctaveLevel = Chance(rng, 0.4) ? Lerp(0.03f, 0.18f, rng) : Lerp(0f, 0.05f, rng);
        // PWM: more motion when Pulse is chosen
        p.PulseWidth = Lerp(0.2f, 0.8f, rng);
        p.PwmDepth = p.Waveform == WaveformType.Pulse
            ? Lerp(0.25f, 0.75f, rng)
            : Lerp(0.1f, 0.5f, rng);

        p.CutoffHz = Lerp(450f, 4200f, rng);
        p.Resonance = Lerp(0.08f, 0.45f, rng);
        p.FilterEnvAmount = Lerp(0.1f, 0.5f, rng);

        // Formants: sometimes off, sometimes vocal
        if (Chance(rng, 0.35))
        {
            p.FormantAmount = Lerp(0.55f, 0.95f, rng);
            p.Vowel = Lerp(0f, 0.55f, rng); // bias Oo–Ah
            p.FormantResonance = Lerp(0.35f, 0.65f, rng);
            p.FormantShift = Lerp(0.85f, 1.15f, rng);
            p.FormantSung = Chance(rng, 0.75);
        }
        else
        {
            p.FormantAmount = Lerp(0f, 0.25f, rng);
            p.FormantSung = true;
        }

        // Noise is a big part of pad "edge" — don't keep it timid.
        // ~12% off · ~40% air · ~30% edge · ~18% aggressive
        double noiseRoll = rng.NextDouble();
        if (noiseRoll < 0.12)
            p.NoiseLevel = 0f;
        else if (noiseRoll < 0.52)
            p.NoiseLevel = Lerp(0.08f, 0.28f, rng);   // soft air / breath
        else if (noiseRoll < 0.82)
            p.NoiseLevel = Lerp(0.28f, 0.55f, rng);   // clear edge
        else
            p.NoiseLevel = Lerp(0.5f, 0.9f, rng);     // gritty / noisy

        // Noise-led patches (low osc) almost always get strong noise
        if (p.OscLevel < 0.6f && p.NoiseLevel < 0.35f)
            p.NoiseLevel = Lerp(0.4f, 0.85f, rng);

        p.NoiseType = (NoiseType)rng.Next(0, 3);
        // Brighter cutoff when noise is loud so the edge cuts through
        p.NoiseCutoffHz = p.NoiseLevel > 0.35f
            ? Lerp(1800f, 7500f, rng)
            : Lerp(800f, 4500f, rng);
        p.NoiseStereo = Lerp(0.55f, 1f, rng);
        p.NoiseMotion = Lerp(0.08f, 0.55f, rng);

        p.LayerBLevel = Chance(rng, 0.5) ? Lerp(0.1f, 0.45f, rng) : Lerp(0f, 0.12f, rng);
        p.LayerBDetuneCents = Lerp(10f, 30f, rng);
        p.LayerBCutoffRatio = Lerp(0.9f, 1.9f, rng);
        p.LayerBWaveform = (WaveformType)rng.Next(0, 6);
        p.LayerBVoices = rng.Next(3, 7);
    }

    private static void RandomizeMotion(PadParameters p, Random rng, bool full)
    {
        p.Evolution = Lerp(0.25f, 0.85f, rng);
        p.FilterLfoRateHz = Lerp(0.03f, 0.2f, rng);
        p.FilterLfoDepth = Lerp(0.15f, 0.7f, rng);
        p.AmpLfoRateHz = Lerp(0.04f, 0.25f, rng);
        p.AmpLfoDepth = Lerp(0.04f, 0.2f, rng);
        p.DriftAmount = Lerp(0.1f, 0.55f, rng);
        p.DriftRateHz = Lerp(0.02f, 0.12f, rng);
        p.FormantMotion = Lerp(0.05f, 0.35f, rng);
        p.FormantMotionRateHz = Lerp(0.02f, 0.12f, rng);
        p.NoiseMotionRateHz = Lerp(0.02f, 0.1f, rng);
        p.PwmRateHz = Lerp(0.03f, 0.18f, rng);
        p.PwmDepth = Lerp(0.15f, 0.7f, rng);
        p.AttackBloom = Lerp(0.1f, 0.8f, rng);
        if (full)
            p.ChorusRateHz = Lerp(0.15f, 0.55f, rng);
    }

    private static void RandomizeSpace(PadParameters p, Random rng, bool full)
    {
        p.ChorusMix = Lerp(0.2f, 0.65f, rng);
        p.ChorusDepthMs = Lerp(2.0f, 6.5f, rng);
        p.ChorusMode = (ChorusMode)rng.Next(0, 3);
        if (full)
            p.ChorusRateHz = Lerp(0.15f, 0.7f, rng);
        p.ReverbMix = Lerp(0.22f, 0.55f, rng);
        p.ReverbDecay = Lerp(0.55f, 0.9f, rng);
        p.ReverbDamping = Lerp(0.15f, 0.65f, rng);
        p.ReverbPredelayMs = Lerp(4f, 35f, rng);
        p.Drive = Lerp(0.05f, 0.35f, rng);
        p.StereoWidth = Lerp(0.75f, 1.45f, rng);
    }

    private static void JitterTone(PadParameters p, Random rng, float amount)
    {
        p.DetuneCents = Jitter(p.DetuneCents, 4f, 40f, amount, rng);
        p.StereoSpread = Jitter(p.StereoSpread, 0.3f, 1f, amount, rng);
        p.AttackBloom = Jitter(p.AttackBloom, 0f, 1f, amount, rng);
        p.OscLevel = Jitter(p.OscLevel, 0f, 1.5f, amount, rng);
        p.SubLevel = Jitter(p.SubLevel, 0f, 0.6f, amount, rng);
        p.FifthLevel = Jitter(p.FifthLevel, 0f, 0.35f, amount, rng);
        p.OctaveLevel = Jitter(p.OctaveLevel, 0f, 0.3f, amount, rng);
        p.CutoffHz = Jitter(p.CutoffHz, 200f, 6000f, amount, rng);
        p.Resonance = Jitter(p.Resonance, 0.05f, 0.7f, amount, rng);
        p.FormantAmount = Jitter(p.FormantAmount, 0f, 1f, amount, rng);
        p.Vowel = Jitter(p.Vowel, 0f, 1f, amount, rng);
        p.FormantShift = Jitter(p.FormantShift, 0.7f, 1.4f, amount, rng);
        p.NoiseLevel = Jitter(p.NoiseLevel, 0f, 0.95f, amount, rng);
        p.NoiseCutoffHz = Jitter(p.NoiseCutoffHz, 400f, 9000f, amount, rng);
        p.LayerBLevel = Jitter(p.LayerBLevel, 0f, 0.7f, amount, rng);
        p.LayerBCutoffRatio = Jitter(p.LayerBCutoffRatio, 0.6f, 2.2f, amount, rng);
        p.PulseWidth = Jitter(p.PulseWidth, 0.1f, 0.9f, amount, rng);
        p.PwmDepth = Jitter(p.PwmDepth, 0f, 1f, amount, rng);
        if (Chance(rng, 0.15))
            p.Waveform = (WaveformType)rng.Next(0, 6);
        if (Chance(rng, amount))
            p.UnisonVoices = Math.Clamp(p.UnisonVoices + rng.Next(-2, 3), 3, 12);
    }

    private static void JitterMotion(PadParameters p, Random rng, float amount)
    {
        p.Evolution = Jitter(p.Evolution, 0.1f, 1f, amount, rng);
        p.FilterLfoRateHz = Jitter(p.FilterLfoRateHz, 0.02f, 0.4f, amount, rng);
        p.FilterLfoDepth = Jitter(p.FilterLfoDepth, 0.05f, 0.9f, amount, rng);
        p.DriftAmount = Jitter(p.DriftAmount, 0f, 0.8f, amount, rng);
        p.FormantMotion = Jitter(p.FormantMotion, 0f, 0.5f, amount, rng);
        p.AttackBloom = Jitter(p.AttackBloom, 0f, 1f, amount, rng);
        p.AmpLfoDepth = Jitter(p.AmpLfoDepth, 0f, 0.35f, amount, rng);
        p.PwmRateHz = Jitter(p.PwmRateHz, 0.02f, 0.3f, amount, rng);
        p.PwmDepth = Jitter(p.PwmDepth, 0f, 1f, amount, rng);
    }

    private static void JitterSpace(PadParameters p, Random rng, float amount)
    {
        p.ChorusMix = Jitter(p.ChorusMix, 0f, 0.8f, amount, rng);
        p.ReverbMix = Jitter(p.ReverbMix, 0f, 0.75f, amount, rng);
        p.ReverbDecay = Jitter(p.ReverbDecay, 0.3f, 0.95f, amount, rng);
        p.ReverbDamping = Jitter(p.ReverbDamping, 0f, 0.9f, amount, rng);
        p.StereoWidth = Jitter(p.StereoWidth, 0.5f, 1.8f, amount, rng);
        p.Drive = Jitter(p.Drive, 0f, 0.5f, amount, rng);
    }

    private static float Lerp(float a, float b, Random rng) =>
        a + (b - a) * (float)rng.NextDouble();

    private static float Jitter(float value, float min, float max, float amount, Random rng)
    {
        float span = (max - min) * amount;
        float delta = ((float)rng.NextDouble() * 2f - 1f) * span;
        return Math.Clamp(value + delta, min, max);
    }

    private static bool Chance(Random rng, double p) => rng.NextDouble() < p;
}
