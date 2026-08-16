using System.Globalization;
using System.Text;
using System.Threading;
using LustPad.Core.Audio;
using LustPad.Core.Presets;

namespace LustPad.Core.Export;

public enum ZoneSpacing
{
    /// <summary>One sample at the current MIDI note only.</summary>
    Single = 0,
    /// <summary>~Every minor third (3 st) — good for formant pads.</summary>
    MinorThird = 3,
    /// <summary>~Every major third (4 st).</summary>
    MajorThird = 4,
    /// <summary>~Every perfect fourth (5 st).</summary>
    PerfectFourth = 5,
    /// <summary>~Every perfect fifth (7 st).</summary>
    PerfectFifth = 7,
    /// <summary>One sample per octave (12 st) — lightest multi-sample.</summary>
    Octave = 12,
}

public sealed class SfzExportOptions
{
    public ZoneSpacing Spacing { get; set; } = ZoneSpacing.PerfectFifth;
    /// <summary>Lowest key to cover (inclusive).</summary>
    public int LowKey { get; set; } = 36; // C2
    /// <summary>Highest key to cover (inclusive).</summary>
    public int HighKey { get; set; } = 84; // C6
    /// <summary>Suggested ampeg_release in the SFZ (sampler ADSR; not baked into audio).</summary>
    public float SuggestedReleaseSeconds { get; set; } = 2.0f;
    /// <summary>Named recipe; when not Custom, overrides spacing/range.</summary>
    public ZoneRecipe Recipe { get; set; } = ZoneRecipe.Custom;
    /// <summary>Shorten duration on outer (edge) zones to save disk.</summary>
    public bool ShorterOuterZones { get; set; } = false;
    /// <summary>Outer zone duration as fraction of full (e.g. 0.65).</summary>
    public float OuterDurationScale { get; set; } = 0.65f;
}

public sealed class SfzExportResult
{
    public required string FolderPath { get; init; }
    public required string SfzPath { get; init; }
    public int ZoneCount { get; init; }
    public IReadOnlyList<int> RootNotes { get; init; } = [];
    public long ElapsedMilliseconds { get; init; }
}

/// <summary>
/// Renders multi-sampled WAVs + an SFZ map into a folder:
///   MyPad/
///     MyPad.sfz
///     samples/
///       MyPad_C2.wav
///       MyPad_G2.wav
///       ...
/// </summary>
public static class SfzExporter
{
    public static SfzExportResult Export(
        PadParameters template,
        string folderPath,
        SfzExportOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SfzExportOptions();
        if (options.Recipe != ZoneRecipe.Custom)
            options = ZoneRecipes.Apply(options.Recipe, options);

        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        Directory.CreateDirectory(folderPath);
        string samplesDir = Path.Combine(folderPath, "samples");
        Directory.CreateDirectory(samplesDir);

        string baseName = SanitizeFileName(string.IsNullOrWhiteSpace(template.Name) ? "Pad" : template.Name);
        var roots = BuildRootNotes(options, template.MidiNote);

        int workers = ZoneParallelism(template, roots.Count);
        progress?.Report(
            roots.Count == 1
                ? $"Rendering zone: {NoteName(roots[0])}…"
                : $"Rendering {roots.Count} zones ({workers} at a time)…");

        var regions = new RegionInfo[roots.Count];
        int completed = 0;
        var parallel = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = workers,
        };

        try
        {
            Parallel.For(0, roots.Count, parallel, i =>
            {
                parallel.CancellationToken.ThrowIfCancellationRequested();
                int root = roots[i];
                var p = ZoneParameters(template, root, roots, options);

                var audio = PadRenderer.Generate(p, parallel.CancellationToken);

                string fileName = $"{baseName}_{NoteName(root)}.wav";
                string wavPath = Path.Combine(samplesDir, fileName);
                int bits = p.ExportBitDepth >= 24 ? 24 : 16;
                WavWriter.Write(wavPath, audio, p.EmbedLoopPoints, bits);

                regions[i] = new RegionInfo(
                    fileName,
                    root,
                    audio.LoopStartFrame,
                    audio.LoopEndFrame,
                    audio.SampleRate);

                int done = Interlocked.Increment(ref completed);
                progress?.Report($"Rendered {done}/{roots.Count}: {NoteName(root)}");
            });
        }
        catch (AggregateException ex)
        {
            throw UnwrapParallel(ex);
        }

        var regionList = regions.ToList();
        AssignKeyRanges(regionList, options.LowKey, options.HighKey);

        string sfzPath = Path.Combine(folderPath, baseName + ".sfz");
        File.WriteAllText(sfzPath, BuildSfzText(baseName, template, regionList, options), Encoding.UTF8);

        // Also drop a JSON preset so the instrument can be re-opened in LustPad
        try
        {
            PresetStore.Save(Path.Combine(folderPath, baseName + ".lustpad.json"), template);
        }
        catch (Exception ex)
        {
            progress?.Report($"SFZ ok; companion preset save failed: {ex.Message}");
        }

        sw.Stop();
        progress?.Report("Done.");

        return new SfzExportResult
        {
            FolderPath = folderPath,
            SfzPath = sfzPath,
            ZoneCount = regionList.Count,
            RootNotes = roots,
            ElapsedMilliseconds = sw.ElapsedMilliseconds,
        };
    }

    private static PadParameters ZoneParameters(
        PadParameters template, int root, IReadOnlyList<int> roots, SfzExportOptions options)
    {
        var p = template.Clone();
        p.MidiNote = root;
        float durScale = ZoneRecipes.DurationScaleForRoot(
            root, roots, options.ShorterOuterZones, options.OuterDurationScale);
        if (durScale < 0.999f)
        {
            p.DurationSeconds = Math.Max(2f, p.DurationSeconds * durScale);
            p.LoopStartSeconds = Math.Min(p.LoopStartSeconds * durScale, p.DurationSeconds * 0.45f);
            p.LoopStartSeconds = Math.Max(0.5f, p.LoopStartSeconds);
        }
        return p;
    }

    /// <summary>
    /// Independent zones render in parallel. Cap workers so 2× / 96 kHz jobs
    /// do not allocate a dozen full-length buffers at once.
    /// </summary>
    internal static int ZoneParallelism(PadParameters p, int zoneCount)
    {
        if (zoneCount <= 1)
            return 1;
        int cap = p.OversampleFactor >= 2 || p.Archival96kHz ? 3 : 6;
        return Math.Clamp(Environment.ProcessorCount, 1, cap);
    }

    private static Exception UnwrapParallel(AggregateException ex)
    {
        foreach (var inner in ex.Flatten().InnerExceptions)
        {
            if (inner is OperationCanceledException)
                return inner;
        }
        return ex.Flatten().InnerExceptions[0];
    }

    public static List<int> BuildRootNotes(SfzExportOptions options, int preferredRoot)
    {
        int low = Math.Clamp(options.LowKey, 0, 127);
        int high = Math.Clamp(options.HighKey, 0, 127);
        if (high < low)
            (low, high) = (high, low);

        if (options.Spacing == ZoneSpacing.Single)
            return [Math.Clamp(preferredRoot, low, high)];

        int step = (int)options.Spacing;
        if (step < 1) step = 12;

        // Align grid so preferred root is on the map when possible
        int start = preferredRoot;
        while (start - step >= low)
            start -= step;

        var roots = new List<int>();
        for (int n = start; n <= high; n += step)
        {
            if (n >= low)
                roots.Add(n);
        }

        if (roots.Count == 0)
            roots.Add(Math.Clamp(preferredRoot, low, high));

        // Ensure preferred is included even if off-grid
        if (!roots.Contains(Math.Clamp(preferredRoot, low, high)))
        {
            roots.Add(Math.Clamp(preferredRoot, low, high));
            roots.Sort();
        }

        return roots;
    }

    private static void AssignKeyRanges(List<RegionInfo> regions, int mapLow, int mapHigh)
    {
        mapLow = Math.Clamp(mapLow, 0, 127);
        mapHigh = Math.Clamp(mapHigh, 0, 127);
        if (regions.Count == 0) return;

        regions.Sort((a, b) => a.Root.CompareTo(b.Root));

        for (int i = 0; i < regions.Count; i++)
        {
            int lo, hi;
            if (regions.Count == 1)
            {
                lo = mapLow;
                hi = mapHigh;
            }
            else if (i == 0)
            {
                lo = mapLow;
                hi = Midpoint(regions[i].Root, regions[i + 1].Root);
            }
            else if (i == regions.Count - 1)
            {
                lo = Midpoint(regions[i - 1].Root, regions[i].Root) + 1;
                hi = mapHigh;
            }
            else
            {
                lo = Midpoint(regions[i - 1].Root, regions[i].Root) + 1;
                hi = Midpoint(regions[i].Root, regions[i + 1].Root);
            }

            lo = Math.Clamp(lo, 0, 127);
            hi = Math.Clamp(hi, 0, 127);
            if (hi < lo) hi = lo;

            regions[i] = regions[i] with { LoKey = lo, HiKey = hi };
        }
    }

    private static int Midpoint(int a, int b) => (a + b) / 2;

    private static string BuildSfzText(
        string baseName,
        PadParameters template,
        List<RegionInfo> regions,
        SfzExportOptions options)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        sb.AppendLine($"// LustPad SFZ instrument — {baseName}");
        sb.AppendLine($"// Generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"// Zones: {regions.Count}  ·  loop end = file end  ·  release via ampeg_release");
        sb.AppendLine($"// Source preset MIDI (editor): {template.MidiNote}  formants={template.FormantAmount:F2} evolution={template.Evolution:F2}");
        sb.AppendLine();
        sb.AppendLine("<control>");
        sb.AppendLine("default_path=samples/");
        sb.AppendLine();
        sb.AppendLine("<global>");
        if (template.SamplerEnvelope)
        {
            // Hold-loop print: both ends of the amp envelope belong to the sampler.
            sb.AppendLine("// Amp envelope: sample is a hold loop; shape attack/release in the sampler");
            sb.AppendLine(string.Create(inv, $"ampeg_attack={Math.Clamp(template.AttackSeconds, 0.001f, 20f):F2}"));
            sb.AppendLine("ampeg_decay=0");
            sb.AppendLine("ampeg_sustain=100");
            sb.AppendLine(string.Create(inv, $"ampeg_release={Math.Clamp(template.ReleaseSeconds, 0.05f, 20f):F2}"));
        }
        else
        {
            sb.AppendLine("// Amp envelope: attack is in the sample; release is the sampler's job");
            sb.AppendLine("ampeg_attack=0.001");
            sb.AppendLine("ampeg_decay=0");
            sb.AppendLine("ampeg_sustain=100");
            sb.AppendLine(string.Create(inv, $"ampeg_release={options.SuggestedReleaseSeconds:F2}"));
        }
        sb.AppendLine("loop_mode=loop_continuous");
        sb.AppendLine();
        sb.AppendLine("<group>");
        sb.AppendLine();

        foreach (var r in regions)
        {
            // SFZ loop_end is typically the last sample index (inclusive), matching our smpl style
            int loopEndInclusive = Math.Max(r.LoopStart, r.LoopEnd - 1);

            sb.AppendLine("<region>");
            sb.AppendLine($"sample={r.FileName}");
            sb.AppendLine($"pitch_keycenter={r.Root}");
            sb.AppendLine($"lokey={r.LoKey}");
            sb.AppendLine($"hikey={r.HiKey}");
            sb.AppendLine("loop_mode=loop_continuous");
            sb.AppendLine($"loop_start={r.LoopStart}");
            sb.AppendLine($"loop_end={loopEndInclusive}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string NoteName(int midi)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        int n = Math.Clamp(midi, 0, 127);
        int oct = n / 12 - 1;
        // Filesystem-safe: C#3 → Cs3
        string note = names[n % 12].Replace("#", "s");
        return $"{note}{oct}";
    }

    public static string SpacingLabel(ZoneSpacing s) => s switch
    {
        ZoneSpacing.Single => "Single note (current)",
        ZoneSpacing.MinorThird => "Minor 3rd (formant-friendly)",
        ZoneSpacing.MajorThird => "Major 3rd",
        ZoneSpacing.PerfectFourth => "Perfect 4th",
        ZoneSpacing.PerfectFifth => "Perfect 5th (recommended)",
        ZoneSpacing.Octave => "Octave (lightest)",
        _ => s.ToString(),
    };

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return string.IsNullOrWhiteSpace(name) ? "Pad" : name;
    }

    private sealed record RegionInfo(
        string FileName,
        int Root,
        int LoopStart,
        int LoopEnd,
        int SampleRate,
        int LoKey = 0,
        int HiKey = 127);
}
