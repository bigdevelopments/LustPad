namespace LustPad.Core.Audio;

/// <summary>
/// Makes pad "evolution" periodic over the loop body so loop end joins loop start
/// in tone (filter LFO, formant motion, drift, etc.), not only in waveform amplitude.
/// </summary>
public static class LoopEvolution
{
    /// <summary>
    /// Returns a copy of <paramref name="p"/> with motion rates snapped so each
    /// LFO completes an integer number of cycles across the loop region
    /// [LoopStartSeconds, DurationSeconds]. Phase at loop end then matches phase
    /// at loop start automatically.
    /// </summary>
    public static PadParameters ApplyLoopLock(PadParameters p)
    {
        if (!p.LockEvolutionToLoop)
            return p;

        float duration = Math.Clamp(p.DurationSeconds, 0.5f, 120f);
        float loopStart = Math.Clamp(p.LoopStartSeconds, 0.05f, duration * 0.9f);
        float loopLen = duration - loopStart;
        if (loopLen < 0.5f)
            return p;

        var locked = p.Clone();

        locked.FilterLfoRateHz = SnapRate(p.FilterLfoRateHz, loopLen);
        locked.AmpLfoRateHz = SnapRate(p.AmpLfoRateHz, loopLen);
        locked.DriftRateHz = SnapRate(p.DriftRateHz, loopLen);
        locked.FormantMotionRateHz = SnapRate(p.FormantMotionRateHz, loopLen);
        locked.ChorusRateHz = SnapRate(p.ChorusRateHz, loopLen);
        locked.PwmRateHz = SnapRate(
            p.PwmRateHz > 0.001f ? p.PwmRateHz : 0.08f,
            loopLen);
        // Noise motion uses a derived rate inside the synth; expose via locking a base.
        // We store the snapped "breath" rate in AmpLfo-adjacent fashion by adjusting NoiseMotion
        // only when rate would be free-running — handled in synth via snapped noise rate from params.
        // Pack snapped noise LFO rate into a dedicated field if present; else synth recomputes.
        locked.NoiseMotionRateHz = SnapRate(
            p.NoiseMotionRateHz > 0.001f ? p.NoiseMotionRateHz : 0.05f,
            loopLen);

        return locked;
    }

    /// <summary>
    /// Snap a rate so rate * loopSeconds ≈ integer cycles.
    /// Very slow motion that cannot complete ~⅓ cycle becomes static (0 Hz) over the loop
    /// rather than leaving a partial sweep that jumps on wrap.
    /// </summary>
    public static float SnapRate(float rateHz, float loopSeconds)
    {
        if (loopSeconds < 0.25f || rateHz <= 0.0001f)
            return 0f;

        float cycles = rateHz * loopSeconds;
        float nearest = MathF.Round(cycles);

        if (nearest < 1f)
        {
            // Partial cycle → either one full slow cycle, or freeze
            nearest = cycles >= 0.35f ? 1f : 0f;
        }

        // Cap how fast we snap upward (don't turn a gentle 0.1 Hz into something wild)
        float snapped = nearest / loopSeconds;
        if (snapped > rateHz * 2.5f && nearest > 1f)
        {
            // Prefer fewer cycles rather than a big jump in speed
            nearest = MathF.Max(1f, MathF.Floor(cycles));
            snapped = nearest / loopSeconds;
        }

        return snapped;
    }

    public static string DescribeLock(PadParameters original, PadParameters locked, float loopSeconds)
    {
        static string Line(string name, float a, float b, float loop)
        {
            if (MathF.Abs(a - b) < 1e-5f && a <= 0.0001f)
                return $"{name}: static over loop";
            float ca = a * loop;
            float cb = b * loop;
            return $"{name}: {a:F3}→{b:F3} Hz ({ca:F2}→{cb:F1} cycles)";
        }

        return string.Join(" · ",
            Line("filter LFO", original.FilterLfoRateHz, locked.FilterLfoRateHz, loopSeconds),
            Line("formant", original.FormantMotionRateHz, locked.FormantMotionRateHz, loopSeconds),
            Line("drift", original.DriftRateHz, locked.DriftRateHz, loopSeconds),
            Line("PWM", original.PwmRateHz, locked.PwmRateHz, loopSeconds),
            Line("chorus", original.ChorusRateHz, locked.ChorusRateHz, loopSeconds));
    }
}
