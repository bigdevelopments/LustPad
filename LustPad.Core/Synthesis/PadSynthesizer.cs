using System.Threading;

namespace LustPad.Core.Synthesis;

/// <summary>
/// Offline pad renderer: unison oscillators, evolving filter, chorus, reverb, soft drive.
/// Output is interleaved stereo float (-1..1), or mono if params.Stereo is false.
/// </summary>
public sealed class PadSynthesizer
{
    /// <param name="workingSampleRate">Render rate (48000 or 96000 when oversampling).</param>
    public RenderedAudio Render(
        PadParameters p, int workingSampleRate = 0, CancellationToken cancellationToken = default)
    {
        int sampleRate = workingSampleRate > 0
            ? workingSampleRate
            : PadParameters.SampleRate * Math.Clamp(p.OversampleFactor, 1, 2);
        int channels = p.Stereo ? 2 : 1;
        // Long evolving pads: allow up to 2 minutes (offline render).
        float duration = Math.Clamp(p.DurationSeconds, 0.5f, 120f);
        int totalSamples = (int)(duration * sampleRate);

        // Extra tail for reverb / crossfade source material
        float extraSeconds = Math.Max(p.CrossfadeSeconds + 0.25f, 0.5f);
        int renderSamples = totalSamples + (int)(extraSeconds * sampleRate);

        var rng = new Random(p.Seed);
        int voices = Math.Clamp(p.UnisonVoices, 1, 16);

        var oscs = new Oscillator[voices];
        var pans = new float[voices];
        var detuneRatios = new float[voices];

        for (int v = 0; v < voices; v++)
        {
            oscs[v] = new Oscillator(sampleRate, rng.NextDouble());
            float t = voices == 1 ? 0.5f : v / (float)(voices - 1);
            // -1..1 pan positions across the field
            pans[v] = (t * 2f - 1f) * p.StereoSpread;
            float detuneCents = voices == 1
                ? 0f
                : (t * 2f - 1f) * p.DetuneCents;
            detuneRatios[v] = CentsToRatio(detuneCents);
        }

        // Dual layer B
        int bVoices = p.LayerBLevel > 0.001f ? Math.Clamp(p.LayerBVoices, 1, 8) : 0;
        var oscsB = new Oscillator[bVoices];
        var pansB = new float[bVoices];
        var detuneB = new float[bVoices];
        for (int v = 0; v < bVoices; v++)
        {
            oscsB[v] = new Oscillator(sampleRate, rng.NextDouble());
            float t = bVoices == 1 ? 0.5f : v / (float)(bVoices - 1);
            pansB[v] = (t * 2f - 1f) * p.StereoSpread * 1.05f;
            detuneB[v] = CentsToRatio((t * 2f - 1f) * p.LayerBDetuneCents);
        }
        var filterBL = new BiquadFilter(sampleRate);
        var filterBR = new BiquadFilter(sampleRate);

        var subOsc = new Oscillator(sampleRate, rng.NextDouble());
        var fifthOsc = new Oscillator(sampleRate, rng.NextDouble());
        var octaveOsc = new Oscillator(sampleRate, rng.NextDouble());

        var filterL = new BiquadFilter(sampleRate);
        var filterR = new BiquadFilter(sampleRate);
        var noiseFilterL = new BiquadFilter(sampleRate);
        var noiseFilterR = new BiquadFilter(sampleRate);
        var formants = new FormantFilter(sampleRate);
        var noise = new NoiseGenerator(p.Seed);
        var filterLfo = new Lfo(sampleRate, rng.NextDouble());
        var ampLfo = new Lfo(sampleRate, rng.NextDouble());
        var driftLfo = new Lfo(sampleRate, rng.NextDouble());
        var driftLfo2 = new Lfo(sampleRate, rng.NextDouble());
        var formantLfo = new Lfo(sampleRate, rng.NextDouble());
        var noiseLfo = new Lfo(sampleRate, rng.NextDouble());
        var pwmLfo = new Lfo(sampleRate, rng.NextDouble());

        var chorus = new Chorus(sampleRate, p.Seed);
        var reverb = new SimpleReverb(sampleRate, p.Seed);
        // Strip DC / sub-Hz bias (asymmetric pulse, PWM wander, filter/reverb drift)
        // after the full chain so peak normalize uses true AC headroom.
        var dcBlockL = new DcBlocker(sampleRate, cutoffHz: 10f);
        var dcBlockR = new DcBlocker(sampleRate, cutoffHz: 10f);

        // Noise filter is relatively static — set once; mild update if motion wants air open/close
        float noiseCut = Math.Clamp(p.NoiseCutoffHz, 80f, sampleRate * 0.45f);
        noiseFilterL.SetLowPass(noiseCut, 0.65f);
        noiseFilterR.SetLowPass(noiseCut * 1.03f, 0.65f);
        int noiseFilterUpdate = 0;

        // Formant coeffs are expensive relative to a multiply — update every N samples.
        const int formantUpdateEvery = 32;
        int formantUpdateCounter = 0;
        float lastVowelPos = float.NaN;
        var env = new AdsrEnvelope(sampleRate);
        env.NoteOn();

        // For loopable sample-library pads we hold sustain through the whole file.
        // Release is applied only as a short tail after the export body (in the extra render)
        // so the loop region never fades out. Sampler engines supply their own release.
        int releaseStart = totalSamples; // begin release at end of exported body
        bool released = false;

        float baseFreq = p.FrequencyHz;
        float evolution = Math.Clamp(p.Evolution, 0f, 1f);
        float outputGain = DbToLin(p.OutputGainDb);

        // Peak tracking for normalization headroom
        float peak = 1e-6f;
        var raw = new float[renderSamples * channels];

        // Q from resonance 0..1
        float q = 0.5f + p.Resonance * 4.5f;
        float lastCutoff = float.NaN;
        const int filterUpdateEvery = 16;

        for (int i = 0; i < renderSamples; i++)
        {
            // Cooperative cancel so background preview can abandon long jobs quickly.
            if ((i & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            if (!released && i >= releaseStart)
            {
                env.NoteOff(p.ReleaseSeconds);
                released = true;
            }

            float envLevel = env.Next(p.AttackSeconds, p.DecaySeconds, p.SustainLevel);

            // Slow drift / evolution — absolute-time LFOs so loop-lock phases match exactly
            float loopLenSec = Math.Max(0.5f, p.DurationSeconds - p.LoopStartSeconds);
            float driftRate1 = p.DriftRateHz;
            float driftRate2 = p.DriftRateHz > 0.0001f
                ? (p.LockEvolutionToLoop
                    ? Audio.LoopEvolution.SnapRate(p.DriftRateHz * 0.73f + 0.01f, loopLenSec)
                    : p.DriftRateHz * 0.73f + 0.01f)
                : 0f;
            float drift = driftLfo.SinAt(i, driftRate1)
                          * p.DriftAmount * evolution * 0.02f; // ~2% pitch at full
            float drift2 = driftLfo2.SinAt(i, driftRate2)
                           * p.DriftAmount * evolution * 0.012f;
            float pitchMod = 1f + drift + drift2;

            // Pulse width + PWM (shared for main Pulse + Layer B Pulse + fifth if Pulse)
            float pulseWidth = Math.Clamp(p.PulseWidth, 0.05f, 0.95f);
            bool needsPulse = p.Waveform == WaveformType.Pulse
                              || (bVoices > 0 && p.LayerBWaveform == WaveformType.Pulse);
            if (needsPulse && p.PwmDepth > 0.001f)
            {
                float pwmRate = p.PwmRateHz > 0.0001f ? p.PwmRateHz : 0.08f;
                float pwm = pwmLfo.SinAt(i, pwmRate);
                // Depth 1 ⇒ up to ±0.45 duty; evolution adds a little more motion
                float depth = Math.Clamp(p.PwmDepth, 0f, 1f) * (0.65f + 0.35f * evolution);
                pulseWidth = Math.Clamp(p.PulseWidth + pwm * depth * 0.45f, 0.05f, 0.95f);
            }

            float left = 0, right = 0;
            float oscLevel = Math.Clamp(p.OscLevel, 0f, 1.5f);
            if (oscLevel > 0.001f)
            {
                float voiceScale = 1f / MathF.Sqrt(voices) * oscLevel;
                for (int v = 0; v < voices; v++)
                {
                    float freq = baseFreq * detuneRatios[v] * pitchMod;
                    float sample = oscs[v].Next(freq, p.Waveform, pulseWidth) * voiceScale;
                    float pan = pans[v];
                    float lGain = MathF.Sqrt(0.5f * (1f - pan));
                    float rGain = MathF.Sqrt(0.5f * (1f + pan));
                    left += sample * lGain;
                    right += sample * rGain;
                }
            }

            // Layer B — second body/air stack (pre-main-filter path, own brighter filter)
            if (bVoices > 0)
            {
                float bScale = 1f / MathF.Sqrt(bVoices);
                float bL = 0, bR = 0;
                for (int v = 0; v < bVoices; v++)
                {
                    float freq = baseFreq * detuneB[v] * pitchMod * 1.003f;
                    float sample = oscsB[v].Next(freq, p.LayerBWaveform, pulseWidth) * bScale;
                    float pan = pansB[v];
                    bL += sample * MathF.Sqrt(0.5f * (1f - pan));
                    bR += sample * MathF.Sqrt(0.5f * (1f + pan));
                }
                float bCut = Math.Clamp(p.CutoffHz * Math.Clamp(p.LayerBCutoffRatio, 0.5f, 3f), 40f, sampleRate * 0.45f);
                filterBL.SetLowPass(bCut, 0.7f);
                filterBR.SetLowPass(bCut * 1.02f, 0.7f);
                bL = filterBL.Process(bL);
                bR = filterBR.Process(bR);
                float bGain = p.LayerBLevel * 0.55f;
                left += bL * bGain;
                right += bR * bGain;
            }

            if (p.SubLevel > 0.001f)
            {
                float sub = subOsc.Next(baseFreq * 0.5f * pitchMod, WaveformType.Sine) * p.SubLevel;
                left += sub * 0.7f;
                right += sub * 0.7f;
            }

            if (p.FifthLevel > 0.001f)
            {
                float fifth = fifthOsc.Next(baseFreq * CentsToRatio(700f) * pitchMod, p.Waveform, pulseWidth)
                              * p.FifthLevel * 0.5f;
                left += fifth * 0.6f;
                right += fifth * 0.8f;
            }

            if (p.OctaveLevel > 0.001f)
            {
                float oct = octaveOsc.Next(baseFreq * 2f * pitchMod, WaveformType.Triangle)
                            * p.OctaveLevel * 0.4f;
                left += oct * 0.75f;
                right += oct * 0.75f;
            }

            // Noise / air bed — filtered separately, mixed before main filter + formants
            // so breath takes the same vowel colour as the oscillators.
            if (p.NoiseLevel > 0.001f)
            {
                var (nL, nR) = noise.Next(p.NoiseType);
                float stereo = Math.Clamp(p.NoiseStereo, 0f, 1f);
                float mono = (nL + nR) * 0.5f;
                nL = mono * (1f - stereo) + nL * stereo;
                nR = mono * (1f - stereo) + nR * stereo;

                // Slow breath motion (rate may be loop-locked via NoiseMotionRateHz)
                float nMotion = 1f;
                if (p.NoiseMotion > 0.001f)
                {
                    float nRate = p.NoiseMotionRateHz > 0.0001f ? p.NoiseMotionRateHz : 0.05f;
                    float nLfo = noiseLfo.UnipolarAt(i, nRate);
                    nMotion = 1f - p.NoiseMotion * 0.45f + p.NoiseMotion * 0.9f * nLfo;
                }

                // Gently open/close noise air with evolution
                if (noiseFilterUpdate == 0 && p.NoiseMotion > 0.001f)
                {
                    float open = 1f + (nMotion - 1f) * 0.5f;
                    float nc = Math.Clamp(noiseCut * open, 80f, sampleRate * 0.45f);
                    noiseFilterL.SetLowPass(nc, 0.65f);
                    noiseFilterR.SetLowPass(nc * 1.03f, 0.65f);
                }
                noiseFilterUpdate++;
                if (noiseFilterUpdate >= 64)
                    noiseFilterUpdate = 0;

                nL = noiseFilterL.Process(nL);
                nR = noiseFilterR.Process(nR);

                // 0.5 so NoiseLevel ≈ 0.5–0.9 reads as real edge, not a whisper
                float nGain = p.NoiseLevel * nMotion * 0.5f;
                left += nL * nGain;
                right += nR * nGain;
            }

            // Parallel paths from the same oscillator/noise mix:
            //   normal  → main LPF (classic pad body)
            //   formant → bandpass vowel bank (needs full harmonic stack)
            // then crossfade by FormantAmount.
            float formAmt = Math.Clamp(p.FormantAmount, 0f, 1f);
            // Slight mid-lift so the blend is useful before the slider is pegged
            float formMix = formAmt <= 0f ? 0f : MathF.Pow(formAmt, 0.75f);

            float formL = left;
            float formR = right;
            if (formMix > 0.001f)
            {
                float vowelCenter = Math.Clamp(p.Vowel, 0f, 1f);
                float vowelPos = vowelCenter;
                if (p.FormantMotion > 0.001f)
                {
                    float motion = formantLfo.SinAt(i, p.FormantMotionRateHz);
                    float span = p.FormantSung
                        ? p.FormantMotion * 0.12f
                        : p.FormantMotion * 0.42f;
                    vowelPos = Math.Clamp(vowelCenter + motion * span, 0f, 1f);
                }

                if (formantUpdateCounter == 0 || float.IsNaN(lastVowelPos) ||
                    MathF.Abs(vowelPos - lastVowelPos) > 0.0015f)
                {
                    formants.Configure(vowelPos, p.FormantShift, p.FormantResonance);
                    lastVowelPos = vowelPos;
                }
                formantUpdateCounter++;
                if (formantUpdateCounter >= formantUpdateEvery)
                    formantUpdateCounter = 0;

                (formL, formR) = formants.Process(left, right);
            }

            // Normal path: filter envelope + LFO
            float filtLfo = filterLfo.UnipolarAt(i, p.FilterLfoRateHz);
            float depthScale = 0.7f + evolution * 0.9f;
            float lfoOctaves = (filtLfo - 0.5f) * 2f * p.FilterLfoDepth * 2.0f * depthScale;
            float envOctaves = envLevel * p.FilterEnvAmount * 2f;
            float cutoff = p.CutoffHz * MathF.Pow(2f, lfoOctaves + envOctaves - p.FilterEnvAmount);
            cutoff = Math.Clamp(cutoff, 40f, sampleRate * 0.45f);

            // Rebuild coeffs every N samples (offline quality; much cheaper than every sample)
            if ((i % filterUpdateEvery) == 0 || float.IsNaN(lastCutoff) ||
                MathF.Abs(cutoff - lastCutoff) > 8f)
            {
                filterL.SetLowPass(cutoff, q);
                filterR.SetLowPass(cutoff * 1.02f, q);
                lastCutoff = cutoff;
            }
            float normL = filterL.Process(left);
            float normR = filterR.Process(right);

            // Mix normal pad body with formant path
            float inv = 1f - formMix;
            left = normL * inv + formL * formMix;
            right = normR * inv + formR * formMix;

            // Soft drive
            if (p.Drive > 0.001f)
            {
                float drive = 1f + p.Drive * 4f;
                left = SoftClip(left * drive) / SoftClip(drive * 0.7f);
                right = SoftClip(right * drive) / SoftClip(drive * 0.7f);
            }

            // Amp LFO (subtle breathing) — absolute time for loop phase match
            float ampMod = 1f - p.AmpLfoDepth * evolution * 0.5f
                           + p.AmpLfoDepth * evolution * 0.5f
                             * ampLfo.UnipolarAt(i, p.AmpLfoRateHz);
            float amp = envLevel * ampMod * outputGain;
            left *= amp;
            right *= amp;

            // Chorus (Juno-style BBD; absolute-time LFO for loop lock)
            (left, right) = chorus.Process(
                left, right, p.ChorusMix, p.ChorusRateHz, p.ChorusDepthMs, p.ChorusMode, i);

            // Reverb (predelay + damping)
            (left, right) = reverb.Process(
                left, right, p.ReverbMix, p.ReverbDecay, p.ReverbDamping, p.ReverbPredelayMs);

            // Mid/side stereo width (after space so the room stays coherent)
            if (channels == 2)
            {
                float width = Math.Clamp(p.StereoWidth, 0f, 2f);
                float mid = 0.5f * (left + right);
                float side = 0.5f * (left - right) * width;
                left = mid + side;
                right = mid - side;
            }

            // AC-couple: kill ~0–few Hz bias so waveform stays centred and samples are library-safe
            left = dcBlockL.Process(left);
            right = dcBlockR.Process(right);

            if (channels == 1)
            {
                float mono = (left + right) * 0.5f;
                raw[i] = mono;
                peak = MathF.Max(peak, MathF.Abs(mono));
            }
            else
            {
                raw[i * 2] = left;
                raw[i * 2 + 1] = right;
                peak = MathF.Max(peak, MathF.Max(MathF.Abs(left), MathF.Abs(right)));
            }
        }

        // Normalize to -1 dBFS peak
        float targetPeak = 0.89f;
        float norm = peak > 1e-6f ? targetPeak / peak : 1f;
        for (int i = 0; i < raw.Length; i++)
            raw[i] *= norm;

        // Trim to requested duration (keep float buffer for loop processing on full render if needed)
        // We'll pass full render so loop crossfade can use the tail, then trim.
        return new RenderedAudio(raw, sampleRate, channels, renderSamples, totalSamples);
    }

    private static float CentsToRatio(float cents) => MathF.Pow(2f, cents / 1200f);

    private static float DbToLin(float db) => MathF.Pow(10f, db / 20f);

    private static float SoftClip(float x) => MathF.Tanh(x);
}

public sealed class RenderedAudio
{
    public float[] Interleaved { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int RenderedFrames { get; }
    public int OutputFrames { get; }

    public RenderedAudio(float[] interleaved, int sampleRate, int channels, int renderedFrames, int outputFrames)
    {
        Interleaved = interleaved;
        SampleRate = sampleRate;
        Channels = channels;
        RenderedFrames = renderedFrames;
        OutputFrames = outputFrames;
    }
}
