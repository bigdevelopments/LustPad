using System;
using System.Threading;
using LustPad.Core.Audio;
using NAudio.Wave;

namespace LustPad.Services;

/// <summary>
/// Plays generated float audio via WASAPI using NAudio.
/// Supports seamless software looping of the loop region for preview.
/// </summary>
public sealed class AudioPreviewService : IDisposable
{
    private readonly object _gate = new();
    private WaveOutEvent? _waveOut;
    private LoopingSampleProvider? _provider;
    private float[]? _buffer;
    private int _channels;
    private int _sampleRate;
    private int _frames;
    private int _loopStart;
    private int _loopEnd;
    private bool _disposed;

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
                return _waveOut?.PlaybackState == PlaybackState.Playing;
        }
    }

    public void Play(LoopProcessor.LoopResult audio, bool loop = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            StopUnlocked();

            _buffer = audio.Interleaved;
            _channels = audio.Channels;
            _sampleRate = audio.SampleRate;
            _frames = audio.FrameCount;
            _loopStart = audio.LoopStartFrame;
            _loopEnd = audio.LoopEndFrame;

            _provider = new LoopingSampleProvider(
                _buffer, _channels, _sampleRate, _frames, _loopStart, _loopEnd, loop);

            _waveOut = new WaveOutEvent { DesiredLatency = 100 };
            _waveOut.Init(_provider);
            _waveOut.Play();
        }
    }

    public void Stop()
    {
        lock (_gate)
            StopUnlocked();
    }

    private void StopUnlocked()
    {
        if (_waveOut is not null)
        {
            try
            {
                _waveOut.Stop();
                _waveOut.Dispose();
            }
            catch
            {
                // ignore teardown races
            }
            _waveOut = null;
        }
        _provider = null;
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
        private readonly int _frames;
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
            _frames = frames;
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

                int src = _frame * _channels;
                int dst = offset + framesWritten * _channels;
                for (int c = 0; c < _channels; c++)
                    buffer[dst + c] = _data[src + c];

                _frame++;
                framesWritten++;
            }

            return framesWritten * _channels;
        }
    }
}
