namespace LustPad.Core.Export;

public enum ZoneRecipe
{
    /// <summary>Custom spacing from SfzExportOptions.Spacing.</summary>
    Custom = 0,
    /// <summary>Formant-dense: minor thirds, C2–C5.</summary>
    FormantDense = 1,
    /// <summary>Drone/sparse: octaves, A1–A3-ish.</summary>
    DroneSparse = 2,
    /// <summary>Full fifths map: C1–C6.</summary>
    FullFifths = 3,
}

public static class ZoneRecipes
{
    public static IReadOnlyList<(ZoneRecipe Recipe, string Label)> All { get; } =
    [
        (ZoneRecipe.Custom, "Custom spacing"),
        (ZoneRecipe.FormantDense, "Formant dense (m3 · C2–C5)"),
        (ZoneRecipe.DroneSparse, "Drone sparse (oct · A1–A3)"),
        (ZoneRecipe.FullFifths, "Full fifths (C1–C6)"),
    ];

    public static string Label(ZoneRecipe r) =>
        All.FirstOrDefault(x => x.Recipe == r).Label ?? r.ToString();

    public static SfzExportOptions Apply(ZoneRecipe recipe, SfzExportOptions? baseOptions = null)
    {
        var o = baseOptions is null
            ? new SfzExportOptions()
            : new SfzExportOptions
            {
                Spacing = baseOptions.Spacing,
                LowKey = baseOptions.LowKey,
                HighKey = baseOptions.HighKey,
                SuggestedReleaseSeconds = baseOptions.SuggestedReleaseSeconds,
                ShorterOuterZones = baseOptions.ShorterOuterZones,
                OuterDurationScale = baseOptions.OuterDurationScale,
                Recipe = recipe,
            };

        o.Recipe = recipe;
        switch (recipe)
        {
            case ZoneRecipe.FormantDense:
                o.Spacing = ZoneSpacing.MinorThird;
                o.LowKey = 36;  // C2
                o.HighKey = 72; // C5
                break;
            case ZoneRecipe.DroneSparse:
                o.Spacing = ZoneSpacing.Octave;
                o.LowKey = 33;  // A1
                o.HighKey = 57; // A3
                break;
            case ZoneRecipe.FullFifths:
                o.Spacing = ZoneSpacing.PerfectFifth;
                o.LowKey = 24;  // C1
                o.HighKey = 84; // C6
                break;
            case ZoneRecipe.Custom:
            default:
                break;
        }

        return o;
    }

    /// <summary>
    /// Scale duration for outer zones: center roots keep full duration;
    /// extremes approach <paramref name="outerScale"/> of full length.
    /// </summary>
    public static float DurationScaleForRoot(
        int root, IReadOnlyList<int> allRoots, bool shorterOuter, float outerScale)
    {
        if (!shorterOuter || allRoots.Count <= 1)
            return 1f;

        outerScale = Math.Clamp(outerScale, 0.35f, 1f);
        int min = allRoots[0];
        int max = allRoots[^1];
        if (max <= min)
            return 1f;

        // Distance from center of map 0..1
        float center = (min + max) * 0.5f;
        float half = (max - min) * 0.5f;
        float dist = MathF.Abs(root - center) / half; // 0 center, 1 edge
        dist = Math.Clamp(dist, 0f, 1f);
        return 1f - dist * (1f - outerScale);
    }
}
