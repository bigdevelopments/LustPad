using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LustPad.Core;
using LustPad.Core.Audio;
using LustPad.Core.Export;
using LustPad.Core.Presets;
using LustPad.Services;

namespace LustPad.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly AudioPreviewService _preview = new();
    private LoopProcessor.LoopResult? _lastAudio;
    private bool _disposed;

    /// <summary>True while applying a full parameter snapshot (avoids N debounce storms).</summary>
    private bool _suspendAutoRender;

    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _renderCts;
    private int _paramsVersion;
    private int _readyVersion = -1;
    private TaskCompletionSource<bool>? _readyTcs;

    /// <summary>UI-only / non-audio properties — changing these must not re-render.</summary>
    private static readonly HashSet<string> AutoRenderIgnore = new(StringComparer.Ordinal)
    {
        nameof(StatusText),
        nameof(IsBusy),
        nameof(IsPlaying),
        nameof(IsRenderingPreview),
        nameof(PeakEnvelope),
        nameof(LoopStartFraction),
        nameof(CrossfadeStartFraction),
        nameof(JoinInfo),
        nameof(AbStatus),
        nameof(ZoneMapSummary),
        nameof(NoteLabel),
        nameof(VowelLabel),
        nameof(FrequencyHz),
        // Selection until Apply / Load is clicked
        nameof(SelectedBuiltInPreset),
        nameof(SelectedMacro),
        nameof(SelectedRandomizeScope),
        // SFZ map only (does not affect single-pad preview PCM)
        nameof(SelectedZoneSpacing),
        nameof(SelectedZoneRecipe),
        nameof(ZoneLowKey),
        nameof(ZoneHighKey),
        nameof(SfzReleaseSeconds),
        nameof(ShorterOuterZones),
    };

    public MainViewModel()
    {
        foreach (var (name, _) in PresetStore.BuiltInPresets)
            BuiltInPresetNames.Add(name);

        PropertyChanged += OnViewModelPropertyChanged;

        SelectedBuiltInPreset = BuiltInPresetNames[0];
        ApplyFromParameters(PadParameters.CreateDefaultLush());
        StatusText = "Rendering default pad…";
    }

    public ObservableCollection<string> BuiltInPresetNames { get; } = new();
    public ObservableCollection<string> WaveformNames { get; } = new(Enum.GetNames<WaveformType>());
    public ObservableCollection<string> NoiseTypeNames { get; } = new(Enum.GetNames<NoiseType>());
    public ObservableCollection<string> ChorusModeNames { get; } = new(
    [
        "Juno I",
        "Juno II",
        "Juno I+II",
    ]);
    public ObservableCollection<string> ZoneSpacingNames { get; } = new(
        Enum.GetValues<ZoneSpacing>().Select(SfzExporter.SpacingLabel));
    public ObservableCollection<string> ZoneRecipeNames { get; } = new(
        ZoneRecipes.All.Select(x => x.Label));
    public ObservableCollection<string> CharacterMacroNames { get; } = new(CharacterMacros.Names);
    public ObservableCollection<string> OversampleLabels { get; } = new(["1× (48 kHz)", "2× oversample"]);
    public ObservableCollection<string> BitDepthLabels { get; } = new(["16-bit", "24-bit"]);
    public ObservableCollection<string> RandomizeScopeNames { get; } = new(ToneRandomizer.ScopeLabels);

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPlaying;
    /// <summary>Background preview render in flight (UI stays interactive).</summary>
    [ObservableProperty] private bool _isRenderingPreview;
    [ObservableProperty] private string? _selectedBuiltInPreset;
    [ObservableProperty] private string _selectedWaveform = nameof(WaveformType.Saw);
    [ObservableProperty] private string _selectedNoiseType = nameof(NoiseType.Pink);
    [ObservableProperty] private string _selectedMacro = "Lush";
    [ObservableProperty] private string _selectedZoneRecipe = ZoneRecipes.Label(ZoneRecipe.Custom);
    [ObservableProperty] private string _selectedOversample = "1× (48 kHz)";
    [ObservableProperty] private string _selectedBitDepth = "16-bit";
    [ObservableProperty] private string _selectedRandomizeScope = ToneRandomizer.ScopeLabel(RandomizeScope.Tone);

    // Identity / pitch — export name (no UI; set from built-in / file load)
    private string _exportName = "Lush Pad";
    [ObservableProperty] private int _seed = 42;
    [ObservableProperty] private int _midiNote = 48;
    [ObservableProperty] private float _fineTuneCents;

    // Oscillators
    [ObservableProperty] private int _unisonVoices = 7;
    [ObservableProperty] private float _detuneCents = 18f;
    [ObservableProperty] private float _stereoSpread = 0.85f;
    [ObservableProperty] private float _attackBloom = 0.4f;
    [ObservableProperty] private float _oscLevel = 1f;
    [ObservableProperty] private float _subLevel = 0.25f;
    [ObservableProperty] private float _fifthLevel = 0.12f;
    [ObservableProperty] private float _octaveLevel = 0.08f;
    [ObservableProperty] private float _pulseWidth = 0.5f;
    [ObservableProperty] private float _pwmDepth = 0.35f;
    [ObservableProperty] private float _pwmRateHz = 0.08f;

    // Noise / air
    [ObservableProperty] private float _noiseLevel;
    [ObservableProperty] private float _noiseCutoffHz = 2800f;
    [ObservableProperty] private float _noiseStereo = 0.85f;
    [ObservableProperty] private float _noiseMotion = 0.2f;

    // Filter
    [ObservableProperty] private float _cutoffHz = 1400f;
    [ObservableProperty] private float _resonance = 0.25f;
    [ObservableProperty] private float _filterLfoRateHz = 0.08f;
    [ObservableProperty] private float _filterLfoDepth = 0.55f;
    [ObservableProperty] private float _filterEnvAmount = 0.35f;

    // Formants / vowel ("oooooh")
    [ObservableProperty] private float _formantAmount;
    [ObservableProperty] private float _vowel;
    [ObservableProperty] private float _formantResonance = 0.45f;
    [ObservableProperty] private float _formantShift = 1f;
    [ObservableProperty] private float _formantMotion = 0.15f;
    [ObservableProperty] private float _formantMotionRateHz = 0.06f;
    [ObservableProperty] private bool _formantSung = true;

    // Dual layer
    [ObservableProperty] private float _layerBLevel;
    [ObservableProperty] private float _layerBDetuneCents = 22f;
    [ObservableProperty] private float _layerBCutoffRatio = 1.45f;
    [ObservableProperty] private string _selectedLayerBWaveform = nameof(WaveformType.Triangle);
    [ObservableProperty] private int _layerBVoices = 4;
    /// <summary>Noise breath rate (engine field; not a main-panel slider).</summary>
    private float _noiseMotionRateHz = 0.05f;

    public string VowelLabel
    {
        get
        {
            string[] names = ["Oo", "Oh", "Ah", "Eh", "Ee"];
            float scaled = Math.Clamp(Vowel, 0f, 1f) * (names.Length - 1);
            int i = (int)MathF.Round(scaled);
            return names[Math.Clamp(i, 0, names.Length - 1)];
        }
    }

    partial void OnVowelChanged(float value) => OnPropertyChanged(nameof(VowelLabel));

    // Envelope
    [ObservableProperty] private float _attackSeconds = 1.8f;
    [ObservableProperty] private float _decaySeconds = 1.2f;
    [ObservableProperty] private float _sustainLevel = 0.85f;
    [ObservableProperty] private float _releaseSeconds = 2.5f;
    [ObservableProperty] private bool _samplerEnvelope;

    // Motion
    [ObservableProperty] private float _ampLfoRateHz = 0.12f;
    [ObservableProperty] private float _ampLfoDepth = 0.12f;
    [ObservableProperty] private float _driftAmount = 0.4f;
    [ObservableProperty] private float _driftRateHz = 0.05f;
    [ObservableProperty] private float _evolution = 0.5f;

    // Effects
    [ObservableProperty] private float _chorusMix = 0.45f;
    [ObservableProperty] private float _chorusRateHz = 0.4f;
    [ObservableProperty] private float _chorusDepthMs = 4.0f;
    [ObservableProperty] private string _selectedChorusMode = "Juno I";
    [ObservableProperty] private float _reverbMix = 0.35f;
    [ObservableProperty] private float _reverbDecay = 0.7f;
    [ObservableProperty] private float _reverbDamping = 0.35f;
    [ObservableProperty] private float _reverbPredelayMs = 12f;
    [ObservableProperty] private float _drive = 0.18f;
    [ObservableProperty] private float _outputGainDb = -3f;
    [ObservableProperty] private float _stereoWidth = 1f;

    // Export
    [ObservableProperty] private float _durationSeconds = 16f;
    [ObservableProperty] private float _loopStartSeconds = 3.5f;
    [ObservableProperty] private float _crossfadeSeconds = 0.75f;
    [ObservableProperty] private bool _stereo = true;
    [ObservableProperty] private bool _embedLoopPoints = true;
    [ObservableProperty] private bool _lockEvolutionToLoop = true;
    [ObservableProperty] private bool _optimizeLoopPoint = true;
    [ObservableProperty] private bool _archival96kHz;

    // SFZ / keyzones
    [ObservableProperty] private string _selectedZoneSpacing = SfzExporter.SpacingLabel(ZoneSpacing.PerfectFifth);
    [ObservableProperty] private int _zoneLowKey = 36;
    [ObservableProperty] private int _zoneHighKey = 84;
    [ObservableProperty] private float _sfzReleaseSeconds = 2f;
    [ObservableProperty] private bool _shorterOuterZones;

    // Waveform / A-B
    [ObservableProperty] private WaveformPeaks.PeakColumn[]? _peakEnvelope;
    [ObservableProperty] private float _loopStartFraction = 0.2f;
    [ObservableProperty] private float _crossfadeStartFraction = 0.9f;
    [ObservableProperty] private string _joinInfo = "Render to see loop markers.";
    [ObservableProperty] private string _abStatus = "A/B empty";

    private PadParameters? _slotA;
    private PadParameters? _slotB;

    public string ZoneMapSummary
    {
        get
        {
            var opts = BuildSfzOptions();
            var roots = SfzExporter.BuildRootNotes(opts, MidiNote);
            return $"{roots.Count} zone(s): {string.Join(", ", roots.Select(SfzExporter.NoteName))}";
        }
    }

    partial void OnSelectedZoneSpacingChanged(string value) => OnPropertyChanged(nameof(ZoneMapSummary));
    partial void OnSelectedZoneRecipeChanged(string value) => OnPropertyChanged(nameof(ZoneMapSummary));
    partial void OnZoneLowKeyChanged(int value) => OnPropertyChanged(nameof(ZoneMapSummary));
    partial void OnZoneHighKeyChanged(int value) => OnPropertyChanged(nameof(ZoneMapSummary));
    partial void OnMidiNoteChanged(int value)
    {
        OnPropertyChanged(nameof(NoteLabel));
        OnPropertyChanged(nameof(ZoneMapSummary));
    }

    public string NoteLabel => $"{MidiNoteName(MidiNote)} ({MidiNote})  ·  {FrequencyHz:F1} Hz";
    public float FrequencyHz =>
        440f * MathF.Pow(2f, (MidiNote - 69 + FineTuneCents / 100f) / 12f);

    partial void OnFineTuneCentsChanged(float value) => OnPropertyChanged(nameof(NoteLabel));

    public Func<IStorageProvider?>? StorageProviderAccessor { get; set; }

    public PadParameters ToParameters()
    {
        if (!Enum.TryParse<WaveformType>(SelectedWaveform, out var wave))
            wave = WaveformType.Saw;
        if (!Enum.TryParse<NoiseType>(SelectedNoiseType, out var noiseType))
            noiseType = NoiseType.Pink;
        if (!Enum.TryParse<WaveformType>(SelectedLayerBWaveform, out var waveB))
            waveB = WaveformType.Triangle;

        return new PadParameters
        {
            Name = string.IsNullOrWhiteSpace(_exportName) ? "Pad" : _exportName.Trim(),
            Seed = Seed,
            MidiNote = MidiNote,
            FineTuneCents = FineTuneCents,
            Waveform = wave,
            UnisonVoices = UnisonVoices,
            DetuneCents = DetuneCents,
            StereoSpread = StereoSpread,
            AttackBloom = AttackBloom,
            OscLevel = OscLevel,
            SubLevel = SubLevel,
            FifthLevel = FifthLevel,
            OctaveLevel = OctaveLevel,
            PulseWidth = PulseWidth,
            PwmDepth = PwmDepth,
            PwmRateHz = PwmRateHz,
            NoiseLevel = NoiseLevel,
            NoiseType = noiseType,
            NoiseCutoffHz = NoiseCutoffHz,
            NoiseStereo = NoiseStereo,
            NoiseMotion = NoiseMotion,
            NoiseMotionRateHz = _noiseMotionRateHz,
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
            LayerBWaveform = waveB,
            LayerBVoices = LayerBVoices,
            AttackSeconds = AttackSeconds,
            DecaySeconds = DecaySeconds,
            SustainLevel = SustainLevel,
            ReleaseSeconds = ReleaseSeconds,
            SamplerEnvelope = SamplerEnvelope,
            AmpLfoRateHz = AmpLfoRateHz,
            AmpLfoDepth = AmpLfoDepth,
            DriftAmount = DriftAmount,
            DriftRateHz = DriftRateHz,
            Evolution = Evolution,
            ChorusMix = ChorusMix,
            ChorusRateHz = ChorusRateHz,
            ChorusDepthMs = ChorusDepthMs,
            ChorusMode = ParseChorusMode(SelectedChorusMode),
            ReverbMix = ReverbMix,
            ReverbDecay = ReverbDecay,
            ReverbDamping = ReverbDamping,
            ReverbPredelayMs = ReverbPredelayMs,
            Drive = Drive,
            OutputGainDb = OutputGainDb,
            StereoWidth = StereoWidth,
            OversampleFactor = SelectedOversample.StartsWith("2") ? 2 : 1,
            ExportBitDepth = SelectedBitDepth.StartsWith("24") ? 24 : 16,
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

    public void ApplyFromParameters(PadParameters p)
    {
        _suspendAutoRender = true;
        try
        {
            _exportName = string.IsNullOrWhiteSpace(p.Name) ? "Pad" : p.Name.Trim();
            Seed = p.Seed;
            MidiNote = p.MidiNote;
            FineTuneCents = p.FineTuneCents;
            SelectedWaveform = p.Waveform.ToString();
            UnisonVoices = p.UnisonVoices;
            DetuneCents = p.DetuneCents;
            StereoSpread = p.StereoSpread;
            AttackBloom = p.AttackBloom;
            OscLevel = p.OscLevel;
            SubLevel = p.SubLevel;
            FifthLevel = p.FifthLevel;
            OctaveLevel = p.OctaveLevel;
            PulseWidth = p.PulseWidth;
            PwmDepth = p.PwmDepth;
            PwmRateHz = p.PwmRateHz;
            NoiseLevel = p.NoiseLevel;
            SelectedNoiseType = p.NoiseType.ToString();
            NoiseCutoffHz = p.NoiseCutoffHz;
            NoiseStereo = p.NoiseStereo;
            NoiseMotion = p.NoiseMotion;
            _noiseMotionRateHz = p.NoiseMotionRateHz;
            CutoffHz = p.CutoffHz;
            Resonance = p.Resonance;
            FilterLfoRateHz = p.FilterLfoRateHz;
            FilterLfoDepth = p.FilterLfoDepth;
            FilterEnvAmount = p.FilterEnvAmount;
            FormantAmount = p.FormantAmount;
            Vowel = p.Vowel;
            FormantResonance = p.FormantResonance;
            FormantShift = p.FormantShift;
            FormantMotion = p.FormantMotion;
            FormantMotionRateHz = p.FormantMotionRateHz;
            FormantSung = p.FormantSung;
            LayerBLevel = p.LayerBLevel;
            LayerBDetuneCents = p.LayerBDetuneCents;
            LayerBCutoffRatio = p.LayerBCutoffRatio;
            SelectedLayerBWaveform = p.LayerBWaveform.ToString();
            LayerBVoices = p.LayerBVoices;
            AttackSeconds = p.AttackSeconds;
            DecaySeconds = p.DecaySeconds;
            SustainLevel = p.SustainLevel;
            ReleaseSeconds = p.ReleaseSeconds;
            SamplerEnvelope = p.SamplerEnvelope;
            AmpLfoRateHz = p.AmpLfoRateHz;
            AmpLfoDepth = p.AmpLfoDepth;
            DriftAmount = p.DriftAmount;
            DriftRateHz = p.DriftRateHz;
            Evolution = p.Evolution;
            ChorusMix = p.ChorusMix;
            ChorusRateHz = p.ChorusRateHz;
            ChorusDepthMs = p.ChorusDepthMs;
            SelectedChorusMode = ChorusModeLabel(p.ChorusMode);
            ReverbMix = p.ReverbMix;
            ReverbDecay = p.ReverbDecay;
            ReverbDamping = p.ReverbDamping;
            ReverbPredelayMs = p.ReverbPredelayMs;
            Drive = p.Drive;
            OutputGainDb = p.OutputGainDb;
            StereoWidth = p.StereoWidth;
            SelectedOversample = p.OversampleFactor >= 2 ? "2× oversample" : "1× (48 kHz)";
            SelectedBitDepth = p.ExportBitDepth >= 24 ? "24-bit" : "16-bit";
            Archival96kHz = p.Archival96kHz;
            DurationSeconds = p.DurationSeconds;
            LoopStartSeconds = p.LoopStartSeconds;
            CrossfadeSeconds = p.CrossfadeSeconds;
            Stereo = p.Stereo;
            EmbedLoopPoints = p.EmbedLoopPoints;
            LockEvolutionToLoop = p.LockEvolutionToLoop;
            OptimizeLoopPoint = p.OptimizeLoopPoint;
            OnPropertyChanged(nameof(NoteLabel));
            OnPropertyChanged(nameof(ZoneMapSummary));
        }
        finally
        {
            _suspendAutoRender = false;
        }

        // Full patch change — render ASAP (no slider debounce).
        RequestPreviewRender(immediate: true);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _suspendAutoRender || IsBusy) return;
        if (e.PropertyName is null || AutoRenderIgnore.Contains(e.PropertyName)) return;
        // Hold-loop print: attack/decay/release are sampler suggestions only.
        if (SamplerEnvelope && e.PropertyName is nameof(AttackSeconds)
                or nameof(DecaySeconds) or nameof(ReleaseSeconds))
            return;
        RequestPreviewRender(immediate: false);
    }

    /// <summary>
    /// Invalidate the preview cache and (re)start a background render.
    /// Slider drags are debounced; preset loads use <paramref name="immediate"/>.
    /// </summary>
    private void RequestPreviewRender(bool immediate)
    {
        if (_disposed || IsBusy) return;

        _paramsVersion++;

        // Complete any previous waiter so ▶ cannot hang on an orphaned TCS.
        var previousTcs = _readyTcs;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _readyTcs = tcs;
        previousTcs?.TrySetResult(false);

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var debounceToken = _debounceCts.Token;

        _ = DebouncedPreviewRenderAsync(immediate, _paramsVersion, debounceToken, tcs);
    }

    private async Task DebouncedPreviewRenderAsync(
        bool immediate, int version, CancellationToken debounceToken, TaskCompletionSource<bool> tcs)
    {
        try
        {
            if (!immediate)
                await Task.Delay(400, debounceToken).ConfigureAwait(true);
            else
                await Task.Yield();
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetResult(false);
            return;
        }

        if (_disposed || version != _paramsVersion)
        {
            tcs.TrySetResult(false);
            return;
        }

        // Cancel any in-flight render from an older version.
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        var p = ToParameters();
        IsRenderingPreview = true;
        if (!IsBusy)
            StatusText = immediate
                ? "Rendering preview…"
                : "Rendering preview (background)…";

        try
        {
            var sw = Stopwatch.StartNew();
            var audio = await Task.Run(() => PadRenderer.Generate(p, ct), ct).ConfigureAwait(true);

            if (_disposed || ct.IsCancellationRequested || version != _paramsVersion)
            {
                tcs.TrySetResult(false);
                return;
            }

            _lastAudio = audio;
            _readyVersion = version;
            UpdateWaveformFrom(audio, p);
            sw.Stop();

            if (!IsBusy)
                StatusText =
                    $"Preview ready ({sw.ElapsedMilliseconds} ms) · press ▶ to play · {JoinInfo}";

            tcs.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetResult(false);
        }
        catch (Exception ex)
        {
            if (!_disposed && !IsBusy)
                StatusText = $"Background render failed: {ex.Message}";
            tcs.TrySetResult(false);
        }
        finally
        {
            // Only clear the flag if nothing newer took over.
            if (version == _paramsVersion)
                IsRenderingPreview = false;
        }
    }

    private bool IsPreviewReady =>
        _lastAudio is not null && _readyVersion == _paramsVersion;

    private void UpdateWaveformFrom(LoopProcessor.LoopResult audio, PadParameters p)
    {
        PeakEnvelope = WaveformPeaks.Build(audio, 400);
        var (ls, _, cf) = WaveformPeaks.MarkerFractions(audio, p.CrossfadeSeconds);
        LoopStartFraction = ls;
        CrossfadeStartFraction = cf;
        string adj = audio.LoopStartAdjustmentFrames != 0
            ? $", nudged {audio.LoopStartAdjustmentFrames / (double)audio.SampleRate * 1000:F0} ms"
            : "";
        JoinInfo =
            $"loop @ {audio.LoopStartFrame / (double)audio.SampleRate:F2}s → end · " +
            $"join err {audio.MatchError:F3}{adj} · {audio.SampleRate / 1000} kHz";
    }

    [RelayCommand]
    private void LoadBuiltIn()
    {
        if (SelectedBuiltInPreset is null) return;
        var match = PresetStore.BuiltInPresets
            .FirstOrDefault(x => x.Name == SelectedBuiltInPreset);
        if (match.Factory is null) return;
        var p = match.Factory();
        p.Name = match.Name; // keep export name in sync with built-in label
        ApplyFromParameters(p);
        StatusText = $"Loaded built-in: {match.Name}";
    }

    [RelayCommand]
    private void RandomizeSeed()
    {
        Seed = Random.Shared.Next(1, 999_999);
        StatusText = $"Seed → {Seed} (subtle voice phase / chorus differences)";
    }

    [RelayCommand]
    private void RandomizeTone()
    {
        var scope = ToneRandomizer.ParseScope(SelectedRandomizeScope);
        var before = ToParameters();
        var after = ToneRandomizer.Randomize(before, scope);
        ApplyFromParameters(after);
        StatusText = scope switch
        {
            RandomizeScope.Subtle =>
                $"Subtle randomize · seed {after.Seed} · note/duration/loop/export unchanged",
            RandomizeScope.Motion =>
                $"Motion randomized · seed {after.Seed} · timbre + structure kept",
            RandomizeScope.Space =>
                $"Space randomized · seed {after.Seed} · oscillators/filter kept",
            _ =>
                $"Tone randomized · seed {after.Seed} · note {after.MidiNote}, " +
                $"{after.DurationSeconds:F0}s, loop @ {after.LoopStartSeconds:F1}s kept",
        };
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (IsBusy) return; // export / SFZ in progress
        try
        {
            if (!IsPreviewReady)
            {
                if (!IsRenderingPreview)
                    RequestPreviewRender(immediate: true);

                StatusText = "Waiting for preview render…";
                var deadline = DateTime.UtcNow.AddMinutes(2);
                // Re-read _readyTcs each slice so a superseded request cannot hang ▶.
                while (!IsPreviewReady && !_disposed && DateTime.UtcNow < deadline)
                {
                    var tcs = _readyTcs;
                    if (tcs is null)
                    {
                        await Task.Delay(40).ConfigureAwait(true);
                        continue;
                    }

                    try
                    {
                        await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(true);
                    }
                    catch (TimeoutException)
                    {
                        // keep waiting until deadline or ready
                    }

                    if (IsPreviewReady)
                        break;
                    // Completed false → another render may already be scheduled
                    if (tcs.Task.IsCompleted && !IsPreviewReady && !IsRenderingPreview)
                        RequestPreviewRender(immediate: true);
                }

                if (!IsPreviewReady || _lastAudio is null)
                {
                    StatusText = "Preview not ready — try again in a moment.";
                    return;
                }
            }

            var audio = _lastAudio!;
            _preview.Play(audio, loop: true);
            IsPlaying = true;
            StatusText =
                $"Playing · {audio.FrameCount / (double)audio.SampleRate:F1}s · {JoinInfo}";
        }
        catch (Exception ex)
        {
            StatusText = $"Preview failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ApplyMacro()
    {
        var applied = CharacterMacros.Apply(CharacterMacros.Parse(SelectedMacro), ToParameters());
        ApplyFromParameters(applied);
        StatusText = $"Applied character macro: {SelectedMacro} (still editable)";
    }

    [RelayCommand]
    private void StoreSlotA()
    {
        _slotA = ToParameters();
        AbStatus = $"A stored ({_slotA.Name}) · B {(_slotB is null ? "empty" : "ready")}";
        StatusText = "Stored current settings in A.";
    }

    [RelayCommand]
    private void StoreSlotB()
    {
        _slotB = ToParameters();
        AbStatus = $"A {(_slotA is null ? "empty" : "ready")} · B stored ({_slotB.Name})";
        StatusText = "Stored current settings in B.";
    }

    [RelayCommand]
    private async Task PreviewSlotAAsync()
    {
        if (_slotA is null)
        {
            StatusText = "Slot A is empty — Store A first.";
            return;
        }
        ApplyFromParameters(_slotA);
        await PreviewAsync();
    }

    [RelayCommand]
    private async Task PreviewSlotBAsync()
    {
        if (_slotB is null)
        {
            StatusText = "Slot B is empty — Store B first.";
            return;
        }
        ApplyFromParameters(_slotB);
        await PreviewAsync();
    }

    [RelayCommand]
    private void StopPreview()
    {
        _preview.Stop();
        IsPlaying = false;
        StatusText = "Stopped.";
    }

    [RelayCommand]
    private async Task ExportWavAsync()
    {
        if (IsBusy) return;
        var sp = StorageProviderAccessor?.Invoke();
        if (sp is null)
        {
            StatusText = "Storage provider unavailable.";
            return;
        }

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export pad WAV",
            SuggestedFileName = SanitizeFileName(_exportName) + ".wav",
            DefaultExtension = "wav",
            FileTypeChoices =
            [
                new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] },
            ],
        }).ConfigureAwait(true);

        if (file is null) return;

        // Snapshot after the picker so we export what the user currently has set.
        var p = ToParameters();
        int exportVersion = _paramsVersion;

        try
        {
            IsBusy = true;
            // Pause auto-preview while exporting (CPU + avoid cache clobber).
            _debounceCts?.Cancel();
            StatusText = "Exporting WAV…";
            var path = file.TryGetLocalPath()
                       ?? throw new InvalidOperationException("Could not resolve file path.");

            var sw = Stopwatch.StartNew();
            var result = await Task.Run(() => PadRenderer.GenerateAndSave(p, path)).ConfigureAwait(true);
            sw.Stop();

            // Only refresh preview cache if params still match this export snapshot.
            if (exportVersion == _paramsVersion)
            {
                _lastAudio = result;
                _readyVersion = exportVersion;
                UpdateWaveformFrom(result, p);
            }

            StatusText =
                $"Exported {path} · {result.SampleRate / 1000} kHz {(p.Stereo ? "stereo" : "mono")} " +
                $"{p.ExportBitDepth}-bit · loop {result.LoopStartFrame}–{result.LoopEndFrame} · " +
                $"{sw.ElapsedMilliseconds} ms";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            // Catch up preview if something changed during export.
            if (!IsPreviewReady)
                RequestPreviewRender(immediate: true);
        }
    }

    [RelayCommand]
    private async Task ExportSfzAsync()
    {
        if (IsBusy) return;
        var sp = StorageProviderAccessor?.Invoke();
        if (sp is null)
        {
            StatusText = "Storage provider unavailable.";
            return;
        }

        var folder = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose parent folder for SFZ instrument",
            AllowMultiple = false,
        }).ConfigureAwait(true);

        if (folder.Count == 0) return;

        string? parent = folder[0].TryGetLocalPath();
        if (parent is null)
        {
            StatusText = "Could not resolve folder path.";
            return;
        }

        var p = ToParameters();
        string instrumentDir = Path.Combine(parent, SanitizeFileName(p.Name));
        var options = BuildSfzOptions();

        try
        {
            IsBusy = true;
            _debounceCts?.Cancel();
            var progress = new Progress<string>(msg => StatusText = msg);
            var result = await Task.Run(() =>
                SfzExporter.Export(p, instrumentDir, options, progress)).ConfigureAwait(true);

            StatusText =
                $"SFZ exported · {result.ZoneCount} zones · {result.SfzPath} · " +
                $"{result.ElapsedMilliseconds} ms · load the .sfz in any SFZ host";
        }
        catch (Exception ex)
        {
            StatusText = $"SFZ export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            if (!IsPreviewReady)
                RequestPreviewRender(immediate: true);
        }
    }

    private SfzExportOptions BuildSfzOptions()
    {
        var recipe = ZoneRecipe.Custom;
        foreach (var (r, label) in ZoneRecipes.All)
        {
            if (label == SelectedZoneRecipe)
            {
                recipe = r;
                break;
            }
        }

        var opts = new SfzExportOptions
        {
            Spacing = ParseZoneSpacing(SelectedZoneSpacing),
            LowKey = ZoneLowKey,
            HighKey = ZoneHighKey,
            SuggestedReleaseSeconds = SfzReleaseSeconds,
            ShorterOuterZones = ShorterOuterZones,
            OuterDurationScale = 0.65f,
            Recipe = recipe,
        };
        if (recipe != ZoneRecipe.Custom)
            opts = ZoneRecipes.Apply(recipe, opts);
        return opts;
    }

    private static ZoneSpacing ParseZoneSpacing(string? label)
    {
        foreach (ZoneSpacing z in Enum.GetValues<ZoneSpacing>())
        {
            if (SfzExporter.SpacingLabel(z) == label)
                return z;
        }
        return ZoneSpacing.PerfectFifth;
    }

    private static ChorusMode ParseChorusMode(string? label) => label switch
    {
        "Juno II" => ChorusMode.JunoII,
        "Juno I+II" => ChorusMode.JunoIPlusII,
        _ => ChorusMode.JunoI,
    };

    private static string ChorusModeLabel(ChorusMode mode) => mode switch
    {
        ChorusMode.JunoII => "Juno II",
        ChorusMode.JunoIPlusII => "Juno I+II",
        _ => "Juno I",
    };

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        var sp = StorageProviderAccessor?.Invoke();
        if (sp is null) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save pad preset",
            SuggestedFileName = SanitizeFileName(_exportName) + ".lustpad.json",
            DefaultExtension = "json",
            FileTypeChoices =
            [
                new FilePickerFileType("LustPad preset") { Patterns = ["*.lustpad.json", "*.json"] },
            ],
        }).ConfigureAwait(true);

        if (file is null) return;

        try
        {
            var path = file.TryGetLocalPath()
                       ?? throw new InvalidOperationException("Could not resolve file path.");
            PresetStore.Save(path, ToParameters());
            StatusText = $"Preset saved: {path}";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadPresetAsync()
    {
        var sp = StorageProviderAccessor?.Invoke();
        if (sp is null) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load pad preset",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("LustPad preset") { Patterns = ["*.lustpad.json", "*.json"] },
            ],
        }).ConfigureAwait(true);

        if (files.Count == 0) return;

        try
        {
            var path = files[0].TryGetLocalPath()
                       ?? throw new InvalidOperationException("Could not resolve file path.");
            var p = PresetStore.Load(path);
            // Filename fallback only when name is missing (do not rename intentional "Lush Pad").
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                string baseName = Path.GetFileNameWithoutExtension(path);
                if (baseName.EndsWith(".lustpad", StringComparison.OrdinalIgnoreCase))
                    baseName = Path.GetFileNameWithoutExtension(baseName);
                if (!string.IsNullOrWhiteSpace(baseName))
                    p.Name = baseName;
            }
            ApplyFromParameters(p);
            StatusText = $"Preset loaded: {_exportName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "pad" : name.Trim();
    }

    private static string MidiNoteName(int note)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        int n = Math.Clamp(note, 0, 127);
        int octave = n / 12 - 1;
        return $"{names[n % 12]}{octave}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PropertyChanged -= OnViewModelPropertyChanged;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _readyTcs?.TrySetResult(false);
        _preview.Dispose();
    }
}
