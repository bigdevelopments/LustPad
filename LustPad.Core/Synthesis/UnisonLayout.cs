namespace LustPad.Core.Synthesis;

/// <summary>
/// Unison voice placement: edge-weighted detune (supersaw-style) and
/// attack bloom (outer voices fade in after the centre).
/// Pan stays linear so Stereo Spread still means an even field.
/// </summary>
public static class UnisonLayout
{
    /// <summary>Exponent &lt; 1 stacks voices toward ±detune instead of an even fence.</summary>
    public const float DetuneExponent = 0.58f;

    /// <summary>Linear voice index mapped to −1..1 (0 = centre).</summary>
    public static float UnitPosition(int voice, int voices)
    {
        if (voices <= 1) return 0f;
        voice = Math.Clamp(voice, 0, voices - 1);
        return voice / (float)(voices - 1) * 2f - 1f;
    }

    /// <summary>0 at centre, 1 at the extreme voices. Used for bloom delay.</summary>
    public static float Edge(int voice, int voices) => MathF.Abs(UnitPosition(voice, voices));

    /// <summary>−1..1 detune weight, denser near the extremes than a linear spread.</summary>
    public static float DetuneSpread(int voice, int voices)
    {
        float x = UnitPosition(voice, voices);
        if (MathF.Abs(x) < 1e-8f) return 0f;
        return MathF.CopySign(MathF.Pow(MathF.Abs(x), DetuneExponent), x);
    }

    /// <summary>
    /// Bloom length in seconds. Short on hold-loops; a slice of attack when baked.
    /// Always finishes before loop start so the sustain loop is a steady choir.
    /// </summary>
    public static float BloomSeconds(PadParameters p)
    {
        float bloom = Math.Clamp(p.AttackBloom, 0f, 1f);
        if (bloom < 0.001f)
            return 0f;

        float sec = p.SamplerEnvelope
            ? bloom * 0.075f
            : bloom * Math.Clamp(p.AttackSeconds * 0.18f, 0.025f, 0.11f);

        float loopStart = Math.Clamp(p.LoopStartSeconds, 0.05f, Math.Max(0.1f, p.DurationSeconds * 0.9f));
        float latestEnd = sec * 1.4f; // max delay + fade
        float budget = loopStart * 0.7f;
        if (latestEnd > budget && latestEnd > 1e-6f)
            sec *= budget / latestEnd;

        return sec;
    }

    public static float FadeSeconds(float bloomSeconds) =>
        bloomSeconds > 1e-5f ? Math.Max(0.006f, bloomSeconds * 0.4f) : 0f;

    public static float OnsetGain(int sampleIndex, int sampleRate, float delaySec, float fadeSec)
    {
        if (delaySec <= 1e-6f && fadeSec <= 1e-6f)
            return 1f;

        float t = sampleIndex / (float)Math.Max(1, sampleRate);
        if (t <= delaySec)
            return 0f;
        if (fadeSec <= 1e-6f)
            return 1f;

        float age = t - delaySec;
        if (age >= fadeSec)
            return 1f;

        return MathF.Sin(age / fadeSec * MathF.PI * 0.5f);
    }
}
