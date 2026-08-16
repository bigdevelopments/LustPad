namespace LustPad.Core.Synthesis;

internal sealed class AdsrEnvelope
{
    private readonly float _sampleRate;
    private enum Stage { Attack, Decay, Sustain, Release, Off }
    private Stage _stage = Stage.Attack;
    private float _level;
    private float _releaseStartLevel;
    private int _releaseSamplesRemaining;
    private float _releaseStep;

    public AdsrEnvelope(float sampleRate) => _sampleRate = sampleRate;

    public bool IsActive => _stage != Stage.Off;

    public void NoteOn()
    {
        _stage = Stage.Attack;
        _level = 0f;
    }

    /// <summary>Skip attack/decay/release — hold at sustain for a sampler-shaped loop.</summary>
    public void HoldSustain(float level)
    {
        _stage = Stage.Sustain;
        _level = Math.Clamp(level, 0f, 1f);
    }

    public void NoteOff(float releaseSeconds)
    {
        if (_stage is Stage.Off or Stage.Release)
            return;

        _releaseStartLevel = _level;
        releaseSeconds = Math.Max(0.01f, releaseSeconds);
        _releaseSamplesRemaining = Math.Max(1, (int)(releaseSeconds * _sampleRate));
        _releaseStep = _releaseStartLevel / _releaseSamplesRemaining;
        _stage = Stage.Release;
    }

    public float Next(float attackSec, float decaySec, float sustainLevel)
    {
        sustainLevel = Math.Clamp(sustainLevel, 0f, 1f);

        switch (_stage)
        {
            case Stage.Attack:
            {
                float attackSamples = Math.Max(1f, attackSec * _sampleRate);
                _level += 1f / attackSamples;
                if (_level >= 1f)
                {
                    _level = 1f;
                    _stage = Stage.Decay;
                }
                break;
            }
            case Stage.Decay:
            {
                float decaySamples = Math.Max(1f, decaySec * _sampleRate);
                float step = (1f - sustainLevel) / decaySamples;
                _level -= step;
                if (_level <= sustainLevel)
                {
                    _level = sustainLevel;
                    _stage = Stage.Sustain;
                }
                break;
            }
            case Stage.Sustain:
                _level = sustainLevel;
                break;
            case Stage.Release:
                _level -= _releaseStep;
                _releaseSamplesRemaining--;
                if (_level <= 0f || _releaseSamplesRemaining <= 0)
                {
                    _level = 0f;
                    _stage = Stage.Off;
                }
                break;
            case Stage.Off:
                _level = 0f;
                break;
        }

        return Math.Clamp(_level, 0f, 1f);
    }
}
