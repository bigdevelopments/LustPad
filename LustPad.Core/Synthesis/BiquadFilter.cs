using System.Runtime.CompilerServices;

namespace LustPad.Core.Synthesis;

/// <summary>RBJ cookbook low-pass biquad with safe coefficient updates.</summary>
internal sealed class BiquadFilter
{
    private readonly float _sampleRate;
    private float _b0, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    public BiquadFilter(float sampleRate)
    {
        _sampleRate = sampleRate;
        SetLowPass(1000f, 0.707f);
    }

    public void SetLowPass(float cutoffHz, float q)
    {
        cutoffHz = Math.Clamp(cutoffHz, 20f, _sampleRate * 0.45f);
        q = Math.Clamp(q, 0.1f, 10f);

        float w0 = 2f * MathF.PI * cutoffHz / _sampleRate;
        float cos = MathF.Cos(w0);
        float sin = MathF.Sin(w0);
        float alpha = sin / (2f * q);

        float b0 = (1f - cos) * 0.5f;
        float b1 = 1f - cos;
        float b2 = (1f - cos) * 0.5f;
        float a0 = 1f + alpha;
        float a1 = -2f * cos;
        float a2 = 1f - alpha;

        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    public void Reset()
    {
        _z1 = 0;
        _z2 = 0;
    }
}
