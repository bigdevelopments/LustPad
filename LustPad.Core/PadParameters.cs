namespace LustPad.Core;

/// <summary>
/// All synthesis and export settings for a lush pad. JSON-serializable for presets.
/// </summary>
public sealed class PadParameters
{
    public const int SampleRate = 48_000;

    // Identity
    public string Name { get; set; } = "Lush Pad";
    public int Seed { get; set; } = 42;

    // Pitch
    public int MidiNote { get; set; } = 48; // C3
    public float FineTuneCents { get; set; } = 0f;

    // Oscillators / unison
    public WaveformType Waveform { get; set; } = WaveformType.Saw;
    public int UnisonVoices { get; set; } = 7;
    public float DetuneCents { get; set; } = 18f;
    public float StereoSpread { get; set; } = 0.85f;
    /// <summary>Main unison oscillator bank level (0 = noise/sub/layers only).</summary>
    public float OscLevel { get; set; } = 1f;
    public float SubLevel { get; set; } = 0.25f;      // octave-down sine
    public float FifthLevel { get; set; } = 0.12f;    // +7 semitones layer
    public float OctaveLevel { get; set; } = 0.08f;   // +12 soft layer

    // Pulse / PWM (used when Waveform or Layer B is Pulse)
    /// <summary>Base pulse duty cycle (0.05–0.95). 0.5 ≈ square.</summary>
    public float PulseWidth { get; set; } = 0.5f;
    /// <summary>How far the PWM LFO moves duty around PulseWidth (0 = static pulse).</summary>
    public float PwmDepth { get; set; } = 0.35f;
    /// <summary>PWM LFO rate (Hz). Snapped when lock-evolution-to-loop is on.</summary>
    public float PwmRateHz { get; set; } = 0.08f;

    // Noise / air
    /// <summary>Noise bed level (0 = off). Soft pink air helps ooh/ahh and atmosphere.</summary>
    public float NoiseLevel { get; set; } = 0f;
    public NoiseType NoiseType { get; set; } = NoiseType.Pink;
    /// <summary>Dedicated low-pass on the noise path (Hz), independent of the main filter.</summary>
    public float NoiseCutoffHz { get; set; } = 2800f;
    /// <summary>0 = mono noise, 1 = fully decorrelated L/R.</summary>
    public float NoiseStereo { get; set; } = 0.85f;
    /// <summary>Slow level motion on the noise bed (breath / wind).</summary>
    public float NoiseMotion { get; set; } = 0.2f;
    /// <summary>Base rate for noise breath LFO (snapped when loop-lock is on).</summary>
    public float NoiseMotionRateHz { get; set; } = 0.05f;

    // Filter
    public float CutoffHz { get; set; } = 1400f;
    public float Resonance { get; set; } = 0.25f;
    public float FilterLfoRateHz { get; set; } = 0.08f;
    public float FilterLfoDepth { get; set; } = 0.55f; // 0-1 of octaves-ish sweep
    public float FilterEnvAmount { get; set; } = 0.35f;

    // Formants / vowel colour ("oooooh")
    /// <summary>0 = bypass, 1 = full formant path (vocal ooh/ahh).</summary>
    public float FormantAmount { get; set; } = 0f;
    /// <summary>0 = Oo … 0.5 = Ah … 1 = Ee</summary>
    public float Vowel { get; set; } = 0f;
    /// <summary>How peaked / "sung" the formants are.</summary>
    public float FormantResonance { get; set; } = 0.45f;
    /// <summary>Scales all formant frequencies (1 = natural adult tract).</summary>
    public float FormantShift { get; set; } = 1f;
    /// <summary>Slow vowel morph depth for evolving "ooooh → aah" motion.</summary>
    public float FormantMotion { get; set; } = 0.15f;
    public float FormantMotionRateHz { get; set; } = 0.06f;

    // Amplitude envelope (seconds)
    public float AttackSeconds { get; set; } = 1.8f;
    public float DecaySeconds { get; set; } = 1.2f;
    public float SustainLevel { get; set; } = 0.85f;
    public float ReleaseSeconds { get; set; } = 2.5f;

    // Motion / evolution
    public float AmpLfoRateHz { get; set; } = 0.12f;
    public float AmpLfoDepth { get; set; } = 0.12f;
    public float DriftAmount { get; set; } = 0.4f;   // slow detune wander
    public float DriftRateHz { get; set; } = 0.05f;
    public float Evolution { get; set; } = 0.5f;     // overall motion macro

    // Dual layer (second body / air stack)
    /// <summary>0 = off. Second detuned layer mixed under the main pad.</summary>
    public float LayerBLevel { get; set; } = 0f;
    public float LayerBDetuneCents { get; set; } = 22f;
    /// <summary>Cutoff multiplier for layer B relative to main filter (1.5 = brighter).</summary>
    public float LayerBCutoffRatio { get; set; } = 1.45f;
    public WaveformType LayerBWaveform { get; set; } = WaveformType.Triangle;
    public int LayerBVoices { get; set; } = 4;

    // Effects
    public float ChorusMix { get; set; } = 0.45f;
    public float ChorusRateHz { get; set; } = 0.4f;
    public float ChorusDepthMs { get; set; } = 4.0f;
    /// <summary>Juno-style topology: I (slow), II (fast), I+II (thick).</summary>
    public ChorusMode ChorusMode { get; set; } = ChorusMode.JunoI;
    public float ReverbMix { get; set; } = 0.35f;
    public float ReverbDecay { get; set; } = 0.7f;
    /// <summary>0 = bright tails, 1 = dark/damped highs in the reverb.</summary>
    public float ReverbDamping { get; set; } = 0.35f;
    public float ReverbPredelayMs { get; set; } = 12f;
    public float Drive { get; set; } = 0.18f;
    public float OutputGainDb { get; set; } = -3f;
    /// <summary>Mid/side width: 0 = mono, 1 = natural, &gt;1 = exaggerated sides.</summary>
    public float StereoWidth { get; set; } = 1f;

    // Formant sung constraint
    /// <summary>When true, formant motion stays near the chosen vowel (sung) instead of wide morph.</summary>
    public bool FormantSung { get; set; } = true;

    // Quality / export format
    /// <summary>1 = native 48 kHz, 2 = internal 96 kHz then downsample (or keep for archival).</summary>
    public int OversampleFactor { get; set; } = 1;
    /// <summary>16 or 24 bit PCM export.</summary>
    public int ExportBitDepth { get; set; } = 16;
    /// <summary>When true and oversample ≥ 2, keep 96 kHz output (no downsample to 48 k).</summary>
    public bool Archival96kHz { get; set; } = false;

    // Export / loop
    /// <summary>
    /// Full sample length. Long evolving pads want 16–30s+ so the loop body
    /// (after attack) has room for slow LFO / formant motion cycles.
    /// </summary>
    public float DurationSeconds { get; set; } = 16f;
    public float LoopStartSeconds { get; set; } = 3.5f; // after attack settles
    public float CrossfadeSeconds { get; set; } = 0.75f;
    public bool Stereo { get; set; } = true;
    public bool EmbedLoopPoints { get; set; } = true;

    /// <summary>
    /// When true, LFO/motion rates are snapped so evolution completes whole cycles
    /// over the loop body — loop end matches loop start in tone, not only amplitude.
    /// </summary>
    public bool LockEvolutionToLoop { get; set; } = true;

    /// <summary>
    /// When true, nudge loop start (±search window) to minimise end/start mismatch
    /// before applying the crossfade.
    /// </summary>
    public bool OptimizeLoopPoint { get; set; } = true;

    public float FrequencyHz =>
        440f * MathF.Pow(2f, (MidiNote - 69 + FineTuneCents / 100f) / 12f);

    public PadParameters Clone()
    {
        return new PadParameters
        {
            Name = Name,
            Seed = Seed,
            MidiNote = MidiNote,
            FineTuneCents = FineTuneCents,
            Waveform = Waveform,
            UnisonVoices = UnisonVoices,
            DetuneCents = DetuneCents,
            StereoSpread = StereoSpread,
            OscLevel = OscLevel,
            SubLevel = SubLevel,
            FifthLevel = FifthLevel,
            OctaveLevel = OctaveLevel,
            PulseWidth = PulseWidth,
            PwmDepth = PwmDepth,
            PwmRateHz = PwmRateHz,
            NoiseLevel = NoiseLevel,
            NoiseType = NoiseType,
            NoiseCutoffHz = NoiseCutoffHz,
            NoiseStereo = NoiseStereo,
            NoiseMotion = NoiseMotion,
            NoiseMotionRateHz = NoiseMotionRateHz,
            CutoffHz = CutoffHz,
            Resonance = Resonance,
            FilterLfoRateHz = FilterLfoRateHz,
            FilterLfoDepth = FilterLfoDepth,
            FilterEnvAmount = FilterEnvAmount,
            FormantAmount = FormantAmount,
            Vowel = Vowel,
            FormantResonance = FormantResonance,
            FormantShift = FormantShift,
            FormantMotion = FormantMotion,
            FormantMotionRateHz = FormantMotionRateHz,
            FormantSung = FormantSung,
            LayerBLevel = LayerBLevel,
            LayerBDetuneCents = LayerBDetuneCents,
            LayerBCutoffRatio = LayerBCutoffRatio,
            LayerBWaveform = LayerBWaveform,
            LayerBVoices = LayerBVoices,
            AttackSeconds = AttackSeconds,
            DecaySeconds = DecaySeconds,
            SustainLevel = SustainLevel,
            ReleaseSeconds = ReleaseSeconds,
            AmpLfoRateHz = AmpLfoRateHz,
            AmpLfoDepth = AmpLfoDepth,
            DriftAmount = DriftAmount,
            DriftRateHz = DriftRateHz,
            Evolution = Evolution,
            ChorusMix = ChorusMix,
            ChorusRateHz = ChorusRateHz,
            ChorusDepthMs = ChorusDepthMs,
            ChorusMode = ChorusMode,
            ReverbMix = ReverbMix,
            ReverbDecay = ReverbDecay,
            ReverbDamping = ReverbDamping,
            ReverbPredelayMs = ReverbPredelayMs,
            Drive = Drive,
            OutputGainDb = OutputGainDb,
            StereoWidth = StereoWidth,
            OversampleFactor = OversampleFactor,
            ExportBitDepth = ExportBitDepth,
            Archival96kHz = Archival96kHz,
            DurationSeconds = DurationSeconds,
            LoopStartSeconds = LoopStartSeconds,
            CrossfadeSeconds = CrossfadeSeconds,
            Stereo = Stereo,
            EmbedLoopPoints = EmbedLoopPoints,
            LockEvolutionToLoop = LockEvolutionToLoop,
            OptimizeLoopPoint = OptimizeLoopPoint,
        };
    }

    public static PadParameters CreateDefaultLush() => new();

    public static PadParameters CreateWarmDrone() => new()
    {
        Name = "Warm Drone",
        Waveform = WaveformType.Saw,
        UnisonVoices = 5,
        DetuneCents = 8f,
        CutoffHz = 800f,
        Resonance = 0.15f,
        FilterLfoRateHz = 0.04f,
        FilterLfoDepth = 0.35f,
        AttackSeconds = 3f,
        SubLevel = 0.4f,
        FifthLevel = 0f,
        ChorusMix = 0.3f,
        ReverbMix = 0.45f,
        Evolution = 0.3f,
    };

    public static PadParameters CreateShimmer() => new()
    {
        Name = "Shimmer Pad",
        Waveform = WaveformType.Saw,
        UnisonVoices = 9,
        DetuneCents = 28f,
        CutoffHz = 2800f,
        Resonance = 0.35f,
        FilterLfoRateHz = 0.15f,
        FilterLfoDepth = 0.7f,
        AttackSeconds = 1.2f,
        OctaveLevel = 0.2f,
        FifthLevel = 0.18f,
        ChorusMix = 0.55f,
        ChorusDepthMs = 6f,
        ReverbMix = 0.4f,
        Evolution = 0.75f,
        DriftAmount = 0.55f,
        NoiseLevel = 0.08f,
        NoiseType = NoiseType.White,
        NoiseCutoffHz = 4500f,
        NoiseMotion = 0.35f,
    };

    public static PadParameters CreateDarkPad() => new()
    {
        Name = "Dark Pad",
        MidiNote = 36,
        Waveform = WaveformType.Triangle,
        UnisonVoices = 6,
        DetuneCents = 12f,
        CutoffHz = 550f,
        Resonance = 0.4f,
        FilterLfoRateHz = 0.06f,
        FilterLfoDepth = 0.45f,
        AttackSeconds = 2.5f,
        SubLevel = 0.5f,
        Drive = 0.3f,
        ChorusMix = 0.25f,
        ReverbMix = 0.5f,
        ReverbDecay = 0.85f,
        NoiseLevel = 0.06f,
        NoiseType = NoiseType.Brown,
        NoiseCutoffHz = 900f,
        NoiseMotion = 0.15f,
    };

    /// <summary>Rounded vocal "oooooh" choir pad via strong Oo formants.</summary>
    public static PadParameters CreateOohChoir() => new()
    {
        Name = "Ooh Choir",
        MidiNote = 52, // E3 — sweet vocal pad range
        Waveform = WaveformType.Saw,
        UnisonVoices = 8,
        DetuneCents = 12f,
        StereoSpread = 0.88f,
        SubLevel = 0.22f,
        FifthLevel = 0.04f,
        OctaveLevel = 0.02f,
        CutoffHz = 2800f, // let formant shelf do the darkening
        Resonance = 0.12f,
        FilterLfoRateHz = 0.04f,
        FilterLfoDepth = 0.18f,
        FilterEnvAmount = 0.15f,
        FormantAmount = 0.92f,
        Vowel = 0.0f, // pure Oo
        FormantResonance = 0.5f,
        FormantShift = 0.92f,
        FormantMotion = 0.12f, // gentle — keep it reading as Oo
        FormantMotionRateHz = 0.035f,
        AttackSeconds = 2.5f,
        DecaySeconds = 1.0f,
        SustainLevel = 0.92f,
        ChorusMix = 0.38f,
        ChorusRateHz = 0.25f,
        ChorusDepthMs = 4.5f,
        ReverbMix = 0.4f,
        ReverbDecay = 0.8f,
        Drive = 0.08f,
        Evolution = 0.35f,
        DriftAmount = 0.2f,
        NoiseLevel = 0.12f,
        NoiseType = NoiseType.Pink,
        NoiseCutoffHz = 2200f,
        NoiseStereo = 0.9f,
        NoiseMotion = 0.25f,
        DurationSeconds = 20f,
        LoopStartSeconds = 4f,
        CrossfadeSeconds = 0.6f,
    };

    /// <summary>Open-mouthed "ahhhh" pad — brighter F1/F2, open shelf.</summary>
    public static PadParameters CreateAhhhPad() => new()
    {
        Name = "Ahhh Pad",
        MidiNote = 50, // D3
        Waveform = WaveformType.Saw,
        UnisonVoices = 7,
        DetuneCents = 16f,
        StereoSpread = 0.9f,
        SubLevel = 0.14f,
        FifthLevel = 0.1f,
        OctaveLevel = 0.06f,
        CutoffHz = 3600f,
        Resonance = 0.16f,
        FilterLfoRateHz = 0.07f,
        FilterLfoDepth = 0.3f,
        FilterEnvAmount = 0.25f,
        FormantAmount = 0.9f,
        Vowel = 0.5f, // Ah centre
        FormantResonance = 0.48f,
        FormantShift = 1.0f,
        FormantMotion = 0.2f,
        FormantMotionRateHz = 0.05f,
        AttackSeconds = 2.0f,
        DecaySeconds = 1.1f,
        SustainLevel = 0.88f,
        ChorusMix = 0.45f,
        ChorusRateHz = 0.32f,
        ChorusDepthMs = 5.5f,
        ReverbMix = 0.45f,
        ReverbDecay = 0.82f,
        Drive = 0.14f,
        Evolution = 0.5f,
        DriftAmount = 0.3f,
        NoiseLevel = 0.16f,
        NoiseType = NoiseType.Pink,
        NoiseCutoffHz = 3600f,
        NoiseStereo = 0.9f,
        NoiseMotion = 0.3f,
        DurationSeconds = 20f,
        LoopStartSeconds = 3.5f,
        CrossfadeSeconds = 0.55f,
    };
}

public enum WaveformType
{
    Saw = 0,
    Square = 1,
    Triangle = 2,
    Sine = 3,
    Mixed = 4, // saw + triangle blend
    /// <summary>Variable-duty pulse; use PulseWidth + PwmDepth / PwmRateHz for PWM.</summary>
    Pulse = 5,
}

/// <summary>Juno-106/60-style chorus topologies.</summary>
public enum ChorusMode
{
    /// <summary>Slower, deeper single BBD — default lush pad swirl.</summary>
    JunoI = 0,
    /// <summary>Faster, lighter single BBD.</summary>
    JunoII = 1,
    /// <summary>Both lines (thick classic I+II).</summary>
    JunoIPlusII = 2,
}

public enum NoiseType
{
    White = 0,
    Pink = 1,
    Brown = 2,
}
