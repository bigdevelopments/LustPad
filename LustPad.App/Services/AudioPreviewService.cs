using System;
using LustPad.Core.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LustPad.Services;

/// <summary>
/// Plays generated float audio for preview via WinMM.
/// WASAPI shared looked like the modern path but went silent on Focusrite USB;
/// WaveOutEvent is what actually produced sound on that box. IEEE float at
/// 100 ms was the choppy part — 16-bit PCM and a fat buffer are the fix.
/// </summary>
public sealed class AudioPreviewService : IDisposable
{
    // Preview is pre-rendered; this only delays the start of playback.
    // 4 × ~100 ms gives USB / 13th-gen DPC spikes room to miss a callback.
    private const int WaveOutLatencyMs = 400;
    private const int WaveOutBuffers = 4;

    private readonly object _gate = new();
    private IWavePlayer? _player;
    private LoopingSampleProvider? _provider;
    private bool _disposed;

    /// <summary>Backend shown in the status bar while playing.</summary>
    public string OutputDescription { get; private set; } = "";

    /// <summary>Raised when the player stops. <paramref name="exception"/> is set on a device error.</summary>
    public event Action<Exception?>? Stopped;

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
                return _player?.PlaybackState == PlaybackState.Playing;
        }
    }

    public void Play(LoopProcessor.LoopResult audio, bool loop = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            StopUnlocked();

            _provider = new LoopingSampleProvider(
                audio.Interleaved, audio.Channels, audio.SampleRate, audio.FrameCount,
                audio.LoopStartFrame, audio.LoopEndFrame, loop);

            var waveOut = new WaveOutEvent
            {
                DeviceNumber = -1, // WAVE_MAPPER — same default device as before
                DesiredLatency = WaveOutLatencyMs,
                NumberOfBuffers = WaveOutBuffers,
            };
            // USB class drivers (Focusrite) are unreliable with IEEE float over MME.
            waveOut.Init(new SampleToWaveProvider16(_provider));
            waveOut.PlaybackStopped += OnPlayerStopped;

            _player = waveOut;
            OutputDescription = $"WinMM · 16-bit · {WaveOutLatencyMs} ms";
            _player.Play();
        }
    }

    public void Stop()
    {
        lock (_gate)
            StopUnlocked();
    }

    private void StopUnlocked()
    {
        if (_player is not null)
        {
            try
            {
                _player.PlaybackStopped -= OnPlayerStopped;
                _player.Stop();
                _player.Dispose();
            }
            catch
            {
                // ignore teardown races
            }
            _player = null;
        }
        _provider = null;
        OutputDescription = "";
    }

    private void OnPlayerStopped(object? sender, StoppedEventArgs e)
    {
        Stopped?.Invoke(e.Exception);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
            StopUnlocked();
    }

    private sealed class LoopingSampleProvider : ISampleProvider
    {
        private readonly float[] _data;
        private readonly int _channels;
        private readonly int _loopStart;
        private readonly int _loopEnd;
        private readonly bool _loop;
        private int _frame;
        private bool _done;

        public WaveFormat WaveFormat { get; }

        public LoopingSampleProvider(
            float[] data, int channels, int sampleRate, int frames,
            int loopStart, int loopEnd, bool loop)
        {
            _data = data;
            _channels = channels;
            _loopStart = Math.Clamp(loopStart, 0, frames - 1);
            _loopEnd = Math.Clamp(loopEnd, _loopStart + 1, frames);
            _loop = loop;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_done) return 0;

            int framesRequested = count / _channels;
            int framesWritten = 0;

            while (framesWritten < framesRequested)
            {
                if (_frame >= _loopEnd)
                {
                    if (_loop)
                        _frame = _loopStart;
                    else
                    {
                        _done = true;
                        break;
                    }
                }

                int take = Math.Min(_loopEnd - _frame, framesRequested - framesWritten);
                int src = _frame * _channels;
                int dst = offset + framesWritten * _channels;
                Array.Copy(_data, src, buffer, dst, take * _channels);
                _frame += take;
                framesWritten += take;
            }

            return framesWritten * _channels;
        }
    }
}
