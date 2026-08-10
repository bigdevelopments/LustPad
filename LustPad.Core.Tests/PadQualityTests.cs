using LustPad.Core;
using LustPad.Core.Audio;
using LustPad.Core.Export;
using LustPad.Core.Presets;

namespace LustPad.Core.Tests;

public class PadQualityTests
{
    private static PadParameters ShortPad()
    {
        var p = PadParameters.CreateDefaultLush();
        p.DurationSeconds = 1.5f;
        p.LoopStartSeconds = 0.4f;
        p.CrossfadeSeconds = 0.15f;
        p.AttackSeconds = 0.2f;
        p.OptimizeLoopPoint = false; // speed
        return p;
    }

    private static double Rms(LoopProcessor.LoopResult a)
    {
        double s = 0;
        foreach (var v in a.Interleaved)
            s += v * (double)v;
        return Math.Sqrt(s / Math.Max(1, a.Interleaved.Length));
    }

    [Fact]
    public void Oversampled_render_delivers_48k_frame_count_and_non_silent_audio()
    {
        var p = ShortPad();
        p.OversampleFactor = 2;
        p.Archival96kHz = false;

        var result = PadRenderer.Generate(p);

        Assert.Equal(PadParameters.SampleRate, result.SampleRate);
        int expected = (int)(p.DurationSeconds * PadParameters.SampleRate);
        Assert.InRange(result.FrameCount, expected - 2, expected + 2);
        Assert.True(Rms(result) > 1e-4, "audio should not be silent");
        Assert.True(result.LoopStartFrame > 0);
        Assert.Equal(result.FrameCount, result.LoopEndFrame);
    }

    [Fact]
    public void Archival_96k_keeps_high_sample_rate()
    {
        var p = ShortPad();
        p.OversampleFactor = 2;
        p.Archival96kHz = true;

        var result = PadRenderer.Generate(p);
        Assert.Equal(96000, result.SampleRate);
        int expected = (int)(p.DurationSeconds * 96000);
        Assert.InRange(result.FrameCount, expected - 4, expected + 4);
    }

    [Fact]
    public void Wav_writer_16_and_24_bit_headers()
    {
        var p = ShortPad();
        p.OversampleFactor = 1;
        var audio = PadRenderer.Generate(p);

        string dir = Path.Combine(Path.GetTempPath(), "lustpad-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string w16 = Path.Combine(dir, "a16.wav");
            string w24 = Path.Combine(dir, "a24.wav");
            WavWriter.Write(w16, audio, embedLoopPoints: true, bitDepth: 16);
            WavWriter.Write(w24, audio, embedLoopPoints: true, bitDepth: 24);

            var f16 = WavWriter.ReadFormat(w16);
            var f24 = WavWriter.ReadFormat(w24);
            Assert.Equal(16, f16.bitsPerSample);
            Assert.Equal(24, f24.bitsPerSample);
            Assert.Equal(audio.SampleRate, f16.sampleRate);
            Assert.Equal(audio.SampleRate, f24.sampleRate);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Wav_archival_96k_write_has_correct_rate()
    {
        var p = ShortPad();
        p.OversampleFactor = 2;
        p.Archival96kHz = true;
        p.ExportBitDepth = 24;
        string dir = Path.Combine(Path.GetTempPath(), "lustpad-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "arch.wav");
            PadRenderer.GenerateAndSave(p, path);
            var fmt = WavWriter.ReadFormat(path);
            Assert.Equal(96000, fmt.sampleRate);
            Assert.Equal(24, fmt.bitsPerSample);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Dual_layer_changes_audio_energy()
    {
        var baseP = ShortPad();
        baseP.LayerBLevel = 0f;
        baseP.Seed = 99;
        var withLayer = baseP.Clone();
        withLayer.LayerBLevel = 0.8f;
        withLayer.LayerBDetuneCents = 30f;

        var a = PadRenderer.Generate(baseP);
        var b = PadRenderer.Generate(withLayer);

        double diff = 0;
        int n = Math.Min(a.Interleaved.Length, b.Interleaved.Length);
        for (int i = 0; i < n; i++)
        {
            double d = a.Interleaved[i] - b.Interleaved[i];
            diff += d * d;
        }
        diff = Math.Sqrt(diff / n);
        Assert.True(diff > 1e-4, $"expected dual-layer delta, got {diff}");
    }

    [Fact]
    public void Stereo_width_and_reverb_change_audio()
    {
        var baseP = ShortPad();
        baseP.ReverbMix = 0f;
        baseP.StereoWidth = 1f;
        baseP.ChorusMix = 0f;

        var wide = baseP.Clone();
        wide.StereoWidth = 1.8f;
        wide.ReverbMix = 0.7f;
        wide.ReverbDecay = 0.9f;

        var a = PadRenderer.Generate(baseP);
        var b = PadRenderer.Generate(wide);
        double diff = 0;
        int n = Math.Min(a.Interleaved.Length, b.Interleaved.Length);
        for (int i = 0; i < n; i++)
        {
            double d = a.Interleaved[i] - b.Interleaved[i];
            diff += d * d;
        }
        Assert.True(Math.Sqrt(diff / n) > 1e-4);
    }

    [Fact]
    public void Formant_oo_vs_ah_changes_spectrum_strongly()
    {
        static double BandEnergy(LoopProcessor.LoopResult a, float fLo, float fHi)
        {
            // Crude DFT-free proxy: 1st-order BP via goertzel-ish block mean of rectified
            // differentiator-smoothed energy is weak; use time-domain resonant probe instead.
            // Simple: count spectral proxy via zero-crossing + amplitude in filtered copy.
            // Better: FIR sliding energy via biquad bandpass on mono.
            int ch = a.Channels;
            int n = a.FrameCount;
            double sr = a.SampleRate;
            // One-pole complex resonator energy estimate
            double w = 2 * Math.PI * ((fLo + fHi) * 0.5) / sr;
            double r = 0.995;
            double cosw = Math.Cos(w);
            double coeff = 2 * r * cosw;
            double z1 = 0, z2 = 0;
            double e = 0;
            int start = n / 5;
            int end = n - n / 10;
            for (int i = start; i < end; i++)
            {
                double x = ch == 1
                    ? a.Interleaved[i]
                    : 0.5 * (a.Interleaved[i * 2] + a.Interleaved[i * 2 + 1]);
                double y = x + coeff * z1 - r * r * z2;
                z2 = z1;
                z1 = y;
                e += y * y;
            }
            return e / Math.Max(1, end - start);
        }

        var oo = ShortPad();
        oo.DurationSeconds = 2f;
        oo.FormantAmount = 0.95f;
        oo.Vowel = 0f; // Oo
        oo.FormantResonance = 0.7f;
        oo.FormantMotion = 0f;
        oo.CutoffHz = 5000f;
        oo.ChorusMix = 0f;
        oo.ReverbMix = 0f;
        oo.NoiseLevel = 0f;
        oo.Seed = 3;

        var ah = oo.Clone();
        ah.Vowel = 0.5f; // Ah

        var aOo = PadRenderer.Generate(oo);
        var aAh = PadRenderer.Generate(ah);

        // Oo emphasizes low F1/F2 cluster; Ah has more mid (F2 ~1.2 kHz)
        double ooLow = BandEnergy(aOo, 200, 700);
        double ahLow = BandEnergy(aAh, 200, 700);
        double ooMid = BandEnergy(aOo, 900, 1600);
        double ahMid = BandEnergy(aAh, 900, 1600);

        double ooRatio = ooMid / (ooLow + 1e-12);
        double ahRatio = ahMid / (ahLow + 1e-12);
        Assert.True(ahRatio > ooRatio * 1.15,
            $"Ah should be brighter mid/low than Oo (oo={ooRatio:F3}, ah={ahRatio:F3})");

        // Also raw sample difference must be large (not a no-op path)
        double diff = 0;
        int n = Math.Min(aOo.Interleaved.Length, aAh.Interleaved.Length);
        for (int i = 0; i < n; i++)
        {
            double d = aOo.Interleaved[i] - aAh.Interleaved[i];
            diff += d * d;
        }
        Assert.True(Math.Sqrt(diff / n) > 0.02, "Oo vs Ah must change the waveform substantially");
    }

    [Fact]
    public void Formant_sung_constrains_motion_vs_free()
    {
        var sung = ShortPad();
        sung.FormantAmount = 0.9f;
        sung.Vowel = 0f; // Oo
        sung.FormantMotion = 1f;
        sung.FormantMotionRateHz = 0.5f;
        sung.FormantSung = true;

        var free = sung.Clone();
        free.FormantSung = false;

        var a = PadRenderer.Generate(sung);
        var b = PadRenderer.Generate(free);
        double diff = 0;
        int n = Math.Min(a.Interleaved.Length, b.Interleaved.Length);
        for (int i = 0; i < n; i++)
        {
            double d = a.Interleaved[i] - b.Interleaved[i];
            diff += d * d;
        }
        Assert.True(Math.Sqrt(diff / n) > 1e-5, "sung vs free formant motion should differ");
    }

    [Fact]
    public void Character_macros_change_multiple_parameters()
    {
        var neutral = new PadParameters
        {
            Name = "N",
            UnisonVoices = 3,
            DetuneCents = 5,
            ChorusMix = 0.1f,
            ReverbMix = 0.1f,
            CutoffHz = 1500,
            FormantAmount = 0,
            NoiseLevel = 0,
            LayerBLevel = 0,
            StereoWidth = 1,
            Evolution = 0.2f,
        };

        foreach (CharacterMacro m in Enum.GetValues<CharacterMacro>())
        {
            var applied = CharacterMacros.Apply(m, neutral);
            int changes = 0;
            if (applied.UnisonVoices != neutral.UnisonVoices) changes++;
            if (Math.Abs(applied.DetuneCents - neutral.DetuneCents) > 0.01f) changes++;
            if (Math.Abs(applied.ChorusMix - neutral.ChorusMix) > 0.01f) changes++;
            if (Math.Abs(applied.ReverbMix - neutral.ReverbMix) > 0.01f) changes++;
            if (Math.Abs(applied.CutoffHz - neutral.CutoffHz) > 0.01f) changes++;
            if (Math.Abs(applied.FormantAmount - neutral.FormantAmount) > 0.01f) changes++;
            if (Math.Abs(applied.NoiseLevel - neutral.NoiseLevel) > 0.01f) changes++;
            if (Math.Abs(applied.LayerBLevel - neutral.LayerBLevel) > 0.01f) changes++;
            if (Math.Abs(applied.StereoWidth - neutral.StereoWidth) > 0.01f) changes++;
            if (Math.Abs(applied.Evolution - neutral.Evolution) > 0.01f) changes++;
            if (applied.FormantSung != neutral.FormantSung) changes++;
            Assert.True(changes >= 2, $"macro {m} should change multiple params, got {changes}");
        }
    }

    [Fact]
    public void Zone_recipes_return_multiple_roots_and_outer_duration_shorter()
    {
        var dense = ZoneRecipes.Apply(ZoneRecipe.FormantDense);
        var roots = SfzExporter.BuildRootNotes(dense, preferredRoot: 48);
        Assert.True(roots.Count >= 3, $"expected multiple roots, got {roots.Count}");

        var fifths = ZoneRecipes.Apply(ZoneRecipe.FullFifths);
        var roots5 = SfzExporter.BuildRootNotes(fifths, 48);
        Assert.True(roots5.Count >= 3);

        float centerScale = ZoneRecipes.DurationScaleForRoot(
            roots5[roots5.Count / 2], roots5, shorterOuter: true, outerScale: 0.65f);
        float edgeScale = ZoneRecipes.DurationScaleForRoot(
            roots5[0], roots5, shorterOuter: true, outerScale: 0.65f);
        Assert.True(edgeScale < centerScale + 0.001f);
        Assert.True(edgeScale <= 0.66f);
        Assert.True(centerScale >= 0.95f);
    }

    [Fact]
    public void Juno_chorus_modes_change_audio()
    {
        var dry = ShortPad();
        dry.ChorusMix = 0f;
        dry.ReverbMix = 0f;
        dry.Seed = 7;

        var junoI = dry.Clone();
        junoI.ChorusMix = 0.7f;
        junoI.ChorusMode = ChorusMode.JunoI;
        junoI.ChorusRateHz = 0.4f;
        junoI.ChorusDepthMs = 4f;

        var junoII = junoI.Clone();
        junoII.ChorusMode = ChorusMode.JunoII;

        var junoBoth = junoI.Clone();
        junoBoth.ChorusMode = ChorusMode.JunoIPlusII;

        var a = PadRenderer.Generate(dry);
        var b = PadRenderer.Generate(junoI);
        var c = PadRenderer.Generate(junoII);
        var d = PadRenderer.Generate(junoBoth);

        static double Diff(LoopProcessor.LoopResult x, LoopProcessor.LoopResult y)
        {
            double s = 0;
            int n = Math.Min(x.Interleaved.Length, y.Interleaved.Length);
            for (int i = 0; i < n; i++)
            {
                double d0 = x.Interleaved[i] - y.Interleaved[i];
                s += d0 * d0;
            }
            return Math.Sqrt(s / n);
        }

        Assert.True(Diff(a, b) > 1e-4, "Juno I should wet the signal");
        Assert.True(Diff(b, c) > 1e-5, "I vs II should differ");
        Assert.True(Diff(b, d) > 1e-5, "I vs I+II should differ");
    }

    [Fact]
    public void Loop_wrap_is_sample_continuous()
    {
        var p = ShortPad();
        p.DurationSeconds = 3f;
        p.LoopStartSeconds = 0.8f;
        p.CrossfadeSeconds = 0.25f;
        p.OptimizeLoopPoint = true;
        p.LockEvolutionToLoop = true;
        p.ChorusMix = 0.35f;
        p.ReverbMix = 0.3f;

        var audio = PadRenderer.Generate(p);
        int ch = audio.Channels;
        int start = audio.LoopStartFrame;
        int end = audio.LoopEndFrame; // exclusive
        Assert.True(start > 8 && end > start + 100);

        // After exclusive-end wrap: last played sample is end-1, next is start.
        // Continuity: end-1 should match lead-in (start-1), so the step into start
        // is comparable to a normal adjacent step at the loop start.
        float Last(int frame, int c) => audio.Interleaved[frame * ch + c];

        for (int c = 0; c < ch; c++)
        {
            float atEnd = Last(end - 1, c);
            float beforeStart = Last(start - 1, c);
            float atStart = Last(start, c);
            float nextStart = Last(start + 1, c);

            float wrapJump = MathF.Abs(atStart - atEnd);
            float naturalStep = MathF.Abs(nextStart - atStart);
            float leadMatch = MathF.Abs(atEnd - beforeStart);

            // End should have been blended onto the lead-in sample (start-1)
            Assert.True(leadMatch < 0.08f,
                $"ch{c}: |end-1 - start-1|={leadMatch} (crossfade should match lead-in)");

            // Wrap step should not be wildly larger than local motion (click detector)
            Assert.True(wrapJump < Math.Max(0.12f, naturalStep * 8f + 0.05f),
                $"ch{c}: wrap jump {wrapJump} vs natural step {naturalStep}");
        }
    }

    [Fact]
    public void Waveform_peaks_build_and_markers()
    {
        var p = ShortPad();
        var audio = PadRenderer.Generate(p);
        var peaks = WaveformPeaks.Build(audio, 128);
        Assert.Equal(128, peaks.Length);
        Assert.Contains(peaks, c => Math.Abs(c.Max) > 1e-5 || Math.Abs(c.Min) > 1e-5);

        var (ls, le, cf) = WaveformPeaks.MarkerFractions(audio, p.CrossfadeSeconds);
        Assert.InRange(ls, 0f, 1f);
        Assert.Equal(1f, le);
        Assert.True(cf <= 1f && cf >= ls);
    }

    [Fact]
    public void Render_has_negligible_dc_offset_including_pulse()
    {
        static double Mean(LoopProcessor.LoopResult a)
        {
            double s = 0;
            foreach (var v in a.Interleaved)
                s += v;
            return s / Math.Max(1, a.Interleaved.Length);
        }

        var saw = ShortPad();
        saw.Waveform = WaveformType.Saw;
        Assert.True(Math.Abs(Mean(PadRenderer.Generate(saw))) < 0.02, "saw should be near zero mean");

        var pulse = ShortPad();
        pulse.Waveform = WaveformType.Pulse;
        pulse.PulseWidth = 0.2f;
        pulse.PwmDepth = 0.8f;
        pulse.PwmRateHz = 0.15f;
        pulse.LockEvolutionToLoop = false;
        pulse.ChorusMix = 0.4f;
        pulse.ReverbMix = 0.35f;
        double mean = Mean(PadRenderer.Generate(pulse));
        Assert.True(Math.Abs(mean) < 0.02, $"pulse+PWM mean should be blocked, got {mean}");
    }

    [Fact]
    public void Pulse_width_and_pwm_change_audio()
    {
        var narrow = ShortPad();
        narrow.Waveform = WaveformType.Pulse;
        narrow.PulseWidth = 0.15f;
        narrow.PwmDepth = 0f;
        narrow.Seed = 42;
        narrow.ChorusMix = 0f;
        narrow.ReverbMix = 0f;

        var wide = narrow.Clone();
        wide.PulseWidth = 0.85f;

        var a = PadRenderer.Generate(narrow);
        var b = PadRenderer.Generate(wide);
        double diff = 0;
        int n = Math.Min(a.Interleaved.Length, b.Interleaved.Length);
        for (int i = 0; i < n; i++)
        {
            double d = a.Interleaved[i] - b.Interleaved[i];
            diff += d * d;
        }
        Assert.True(Math.Sqrt(diff / n) > 1e-4, "pulse width should change the tone");

        var staticPulse = narrow.Clone();
        staticPulse.PulseWidth = 0.5f;
        staticPulse.PwmDepth = 0f;

        var modulated = staticPulse.Clone();
        modulated.PwmDepth = 0.9f;
        modulated.PwmRateHz = 0.5f;
        modulated.LockEvolutionToLoop = false;

        var s = PadRenderer.Generate(staticPulse);
        var m = PadRenderer.Generate(modulated);
        diff = 0;
        n = Math.Min(s.Interleaved.Length, m.Interleaved.Length);
        for (int i = 0; i < n; i++)
        {
            double d = s.Interleaved[i] - m.Interleaved[i];
            diff += d * d;
        }
        Assert.True(Math.Sqrt(diff / n) > 1e-4, "PWM depth should modulate the pulse");
    }

    [Fact]
    public void Clone_preserves_layer_b_and_noise_motion_rate()
    {
        var p = ShortPad();
        p.LayerBWaveform = WaveformType.Pulse;
        p.LayerBVoices = 6;
        p.NoiseMotionRateHz = 0.11f;
        p.ChorusMode = ChorusMode.JunoIPlusII;
        p.PulseWidth = 0.3f;
        p.PwmDepth = 0.6f;
        p.PwmRateHz = 0.09f;

        var c = p.Clone();
        Assert.Equal(WaveformType.Pulse, c.LayerBWaveform);
        Assert.Equal(6, c.LayerBVoices);
        Assert.Equal(0.11f, c.NoiseMotionRateHz);
        Assert.Equal(ChorusMode.JunoIPlusII, c.ChorusMode);
        Assert.Equal(0.3f, c.PulseWidth);
        Assert.Equal(0.6f, c.PwmDepth);
        Assert.Equal(0.09f, c.PwmRateHz);
    }

    [Fact]
    public void Loop_evolution_snaps_pwm_and_noise_rates()
    {
        var p = ShortPad();
        p.DurationSeconds = 10f;
        p.LoopStartSeconds = 2f;
        p.LockEvolutionToLoop = true;
        p.PwmRateHz = 0.07f;
        p.NoiseMotionRateHz = 0.05f;
        p.ChorusRateHz = 0.33f;

        float loopLen = p.DurationSeconds - p.LoopStartSeconds;
        var locked = LoopEvolution.ApplyLoopLock(p);

        // Integer (or zero) cycles over the loop body
        float pwmCycles = locked.PwmRateHz * loopLen;
        float noiseCycles = locked.NoiseMotionRateHz * loopLen;
        float chorusCycles = locked.ChorusRateHz * loopLen;
        Assert.True(locked.PwmRateHz == 0f || Math.Abs(pwmCycles - MathF.Round(pwmCycles)) < 1e-3f);
        Assert.True(locked.NoiseMotionRateHz == 0f || Math.Abs(noiseCycles - MathF.Round(noiseCycles)) < 1e-3f);
        Assert.True(locked.ChorusRateHz == 0f || Math.Abs(chorusCycles - MathF.Round(chorusCycles)) < 1e-3f);
    }

    [Fact]
    public void Pad_renderer_honours_cancellation()
    {
        var p = ShortPad();
        p.DurationSeconds = 8f;
        p.OversampleFactor = 2;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PadRenderer.Generate(p, cts.Token));
    }

    [Fact]
    public void Tone_randomize_preserves_structure_and_changes_colour()
    {
        var src = ShortPad();
        src.MidiNote = 55;
        src.DurationSeconds = 12f;
        src.LoopStartSeconds = 3f;
        src.CrossfadeSeconds = 0.8f;
        src.OversampleFactor = 2;
        src.ExportBitDepth = 24;
        src.Name = "KeepMe";
        src.CutoffHz = 1000f;
        src.DetuneCents = 10f;

        var rng = new Random(12345);
        var tone = ToneRandomizer.Randomize(src, RandomizeScope.Tone, rng);

        Assert.Equal(src.MidiNote, tone.MidiNote);
        Assert.Equal(src.DurationSeconds, tone.DurationSeconds);
        Assert.Equal(src.LoopStartSeconds, tone.LoopStartSeconds);
        Assert.Equal(src.CrossfadeSeconds, tone.CrossfadeSeconds);
        Assert.Equal(src.OversampleFactor, tone.OversampleFactor);
        Assert.Equal(src.ExportBitDepth, tone.ExportBitDepth);
        Assert.Equal(src.Name, tone.Name);
        Assert.Equal(src.LockEvolutionToLoop, tone.LockEvolutionToLoop);

        // Colour should move
        Assert.True(
            Math.Abs(tone.CutoffHz - src.CutoffHz) > 1f ||
            Math.Abs(tone.DetuneCents - src.DetuneCents) > 0.5f ||
            tone.Waveform != src.Waveform ||
            tone.Seed != src.Seed,
            "tone randomize should change sonic parameters");

        var motion = ToneRandomizer.Randomize(src, RandomizeScope.Motion, new Random(7));
        Assert.Equal(src.CutoffHz, motion.CutoffHz);
        Assert.Equal(src.UnisonVoices, motion.UnisonVoices);
        Assert.True(
            Math.Abs(motion.FilterLfoRateHz - src.FilterLfoRateHz) > 1e-4 ||
            Math.Abs(motion.Evolution - src.Evolution) > 1e-4 ||
            Math.Abs(motion.DriftAmount - src.DriftAmount) > 1e-4);
    }
}
