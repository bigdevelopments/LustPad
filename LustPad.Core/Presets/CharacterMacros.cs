namespace LustPad.Core.Presets;

public enum CharacterMacro
{
    Lush = 0,
    Dark = 1,
    Airy = 2,
    Vocal = 3,
}

/// <summary>
/// Multi-parameter character transforms. Applied onto a clone; user can edit afterward.
/// </summary>
public static class CharacterMacros
{
    public static IReadOnlyList<string> Names { get; } =
        ["Lush", "Dark", "Airy", "Vocal"];

    public static PadParameters Apply(CharacterMacro macro, PadParameters source)
    {
        var p = source.Clone();
        switch (macro)
        {
            case CharacterMacro.Lush:
                p.Name = string.IsNullOrWhiteSpace(p.Name) ? "Lush" : p.Name;
                p.UnisonVoices = Math.Max(p.UnisonVoices, 7);
                p.DetuneCents = Math.Clamp(p.DetuneCents < 10 ? 18f : p.DetuneCents, 12f, 28f);
                p.ChorusMix = Math.Max(p.ChorusMix, 0.45f);
                p.ChorusMode = ChorusMode.JunoIPlusII;
                p.ChorusRateHz = Math.Clamp(p.ChorusRateHz, 0.25f, 0.55f);
                p.ReverbMix = Math.Max(p.ReverbMix, 0.38f);
                p.ReverbDecay = Math.Max(p.ReverbDecay, 0.72f);
                p.StereoWidth = Math.Max(p.StereoWidth, 1.15f);
                p.Evolution = Math.Max(p.Evolution, 0.45f);
                p.CutoffHz = Math.Clamp(p.CutoffHz, 1000f, 2800f);
                p.LayerBLevel = Math.Max(p.LayerBLevel, 0.2f);
                p.PwmDepth = Math.Max(p.PwmDepth, 0.25f);
                break;

            case CharacterMacro.Dark:
                p.CutoffHz = Math.Min(p.CutoffHz, 700f);
                p.Resonance = Math.Max(p.Resonance, 0.35f);
                p.SubLevel = Math.Max(p.SubLevel, 0.4f);
                p.Drive = Math.Max(p.Drive, 0.25f);
                p.NoiseType = NoiseType.Brown;
                p.NoiseLevel = Math.Max(p.NoiseLevel, 0.06f);
                p.NoiseCutoffHz = Math.Min(p.NoiseCutoffHz, 1200f);
                p.ReverbMix = Math.Max(p.ReverbMix, 0.48f);
                p.ReverbDecay = Math.Max(p.ReverbDecay, 0.82f);
                p.ReverbDamping = Math.Max(p.ReverbDamping, 0.55f);
                p.FormantAmount = Math.Min(p.FormantAmount, 0.25f);
                p.Evolution = Math.Min(p.Evolution, 0.4f);
                p.StereoWidth = Math.Clamp(p.StereoWidth, 0.7f, 1.0f);
                break;

            case CharacterMacro.Airy:
                p.NoiseLevel = Math.Max(p.NoiseLevel, 0.18f);
                p.NoiseType = NoiseType.Pink;
                p.NoiseCutoffHz = Math.Max(p.NoiseCutoffHz, 3500f);
                p.CutoffHz = Math.Max(p.CutoffHz, 2400f);
                p.ChorusMix = Math.Max(p.ChorusMix, 0.4f);
                p.ReverbMix = Math.Max(p.ReverbMix, 0.42f);
                p.ReverbPredelayMs = Math.Max(p.ReverbPredelayMs, 18f);
                p.StereoWidth = Math.Max(p.StereoWidth, 1.25f);
                p.LayerBLevel = Math.Max(p.LayerBLevel, 0.25f);
                p.LayerBCutoffRatio = Math.Max(p.LayerBCutoffRatio, 1.6f);
                p.Evolution = Math.Max(p.Evolution, 0.55f);
                // Gentle PWM motion when using pulse (ignored for other waves)
                p.PwmDepth = Math.Max(p.PwmDepth, 0.4f);
                p.PwmRateHz = Math.Clamp(p.PwmRateHz <= 0.02f ? 0.1f : p.PwmRateHz, 0.04f, 0.2f);
                break;

            case CharacterMacro.Vocal:
                p.FormantAmount = Math.Max(p.FormantAmount, 0.85f);
                p.FormantSung = true;
                p.FormantResonance = Math.Max(p.FormantResonance, 0.5f);
                p.FormantMotion = Math.Clamp(p.FormantMotion, 0.08f, 0.22f);
                p.NoiseLevel = Math.Max(p.NoiseLevel, 0.1f);
                p.NoiseType = NoiseType.Pink;
                p.FilterLfoDepth = Math.Min(p.FilterLfoDepth, 0.3f);
                p.UnisonVoices = Math.Max(p.UnisonVoices, 6);
                p.DetuneCents = Math.Clamp(p.DetuneCents, 8f, 16f);
                p.ReverbMix = Math.Max(p.ReverbMix, 0.4f);
                break;
        }

        return p;
    }

    public static CharacterMacro Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "dark" => CharacterMacro.Dark,
        "airy" => CharacterMacro.Airy,
        "vocal" => CharacterMacro.Vocal,
        _ => CharacterMacro.Lush,
    };
}
