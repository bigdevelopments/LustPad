using System;
using System.Threading;
using LustPad.Core.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LustPad.Services;

/// <summary>
/// Preview playback via WinMM. The WaveOut device is kept alive while playing;
/// randomize / slider updates swap the loop buffer instead of Stop+Join, which
/// deadlocks the Avalonia UI thread against the WinMM callback after a few cycles
/// (especially on USB class devices).
/// </summary>
public sealed class AudioPreviewService : IDisposable
{
    private const int WaveOutLatencyMs = 400;
    private const int WaveOutBuffers = 4;

    private readonly object _gate = new();
    private IWavePlayer? _player;
    private LoopingSampleProvider? _provider;
    private bool _disposed;

    public string OutputDescription { get; private set; } = "";

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
            if (_provider is not null
                && _player is not null
                && _provider.WaveFormat.SampleRate == audio.SampleRate
                && _provider.WaveFormat.Channels == audio.Channels)
            {
                _provider.Swap(audio, loop);
                if (_player.PlaybackState != PlaybackState.Playing)
                    _player.Play();
                return;
            }

            StartDeviceUnlocked(audio, loop);
        }
    }

    public void Stop()
    {
        lock (_gate)
            StopUnlocked();
    }

    private void StartDeviceUnlocked(LoopProcessor.LoopResult audio, bool loop)
    {
        StopUnlocked();

        _provider = new LoopingSampleProvider(
            audio.Interleaved, audio.Channels, audio.SampleRate, audio.FrameCount,
            audio.LoopStartFrame, audio.LoopEndFrame, loop);

        var waveOut = new WaveOutEvent
        {
            DeviceNumber = -1,
            DesiredLatency = WaveOutLatencyMs,
            NumberOfBuffers = WaveOutBuffers,
        };
        waveOut.Init(new SampleToWaveProvider16(_provider));
        waveOut.PlaybackStopped += OnPlayerStopped;

        _player = waveOut;
        OutputDescription = $"WinMM · 16-bit · {WaveOutLatencyMs} ms";
        _player.Play();
    }

    private void StopUnlocked()
    {
        var player = _player;
        _player = null;
        _provider = null;
        OutputDescription = "";
        if (player is null)
            return;

        player.PlaybackStopped -= OnPlayerStopped;
        // waveOutReset/Join must not run on the Avalonia thread — it waits on the
        // WinMM callback, which can need this thread (or just never return on USB).
        ThreadPool.QueueUserWorkItem(static p =>
        {
            try
            {
                var wo = (IWavePlayer)p!;
                wo.Stop();
                wo.Dispose();
            }
            catch
            {
                // ignore teardown races
            }
        }, player);
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
        private readonly object _readGate = new();
        private float[] _data;
        private readonly int _channels;
        private int _loopStart;
        private int _loopEnd;
        private bool _loop;
        private int _frame;
        private bool _done;

        public WaveFormat WaveFormat { get; }

        public LoopingSampleProvider(
            float[] data, int channels, int sampleRate, int frames,
            int loopStart, int loopEnd, bool loop)
        {
            _data = data;
            _channels = channels;
            ApplyLoop(frames, loopStart, loopEnd, loop);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public void Swap(LoopProcessor.LoopResult audio, bool loop)
        {
            lock (_readGate)
            {
                _data = audio.Interleaved;
                ApplyLoop(audio.FrameCount, audio.LoopStartFrame, audio.LoopEndFrame, loop);
            }
        }

        private void ApplyLoop(int frames, int loopStart, int loopEnd, bool loop)
        {
            _loopStart = Math.Clamp(loopStart, 0, Math.Max(0, frames - 1));
            _loopEnd = Math.Clamp(loopEnd, _loopStart + 1, frames);
            _loop = loop;
            _frame = 0;
            _done = false;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_readGate)
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
}
