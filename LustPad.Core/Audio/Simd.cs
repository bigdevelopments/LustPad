using System.Numerics;
using System.Runtime.CompilerServices;

namespace LustPad.Core.Audio;

/// <summary>
/// Hardware-accelerated float loops (SSE/AVX via <see cref="Vector{T}"/>).
/// Falls back to scalar automatically when the JIT has no SIMD.
/// </summary>
internal static class Simd
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Scale(Span<float> data, float gain)
    {
        if (gain == 1f || data.IsEmpty)
            return;

        if (Vector.IsHardwareAccelerated && data.Length >= Vector<float>.Count)
        {
            var vGain = new Vector<float>(gain);
            int w = Vector<float>.Count;
            int i = 0;
            int last = data.Length - w;
            for (; i <= last; i += w)
            {
                var v = new Vector<float>(data.Slice(i, w));
                (v * vGain).CopyTo(data.Slice(i, w));
            }
            for (; i < data.Length; i++)
                data[i] *= gain;
            return;
        }

        for (int i = 0; i < data.Length; i++)
            data[i] *= gain;
    }

    /// <summary>Energy-normalised Σ(a−b)² over two equal-length contiguous windows.</summary>
    public static double SumSqDiff(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n == 0)
            return 0;

        double err = 0, e0 = 0, e1 = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            int w = Vector<float>.Count;
            int last = n - w;
            var vErr = Vector<float>.Zero;
            var vE0 = Vector<float>.Zero;
            var vE1 = Vector<float>.Zero;
            for (; i <= last; i += w)
            {
                var va = new Vector<float>(a.Slice(i, w));
                var vb = new Vector<float>(b.Slice(i, w));
                var d = va - vb;
                vErr += d * d;
                vE0 += va * va;
                vE1 += vb * vb;
            }
            err = Vector.Sum(vErr);
            e0 = Vector.Sum(vE0);
            e1 = Vector.Sum(vE1);
        }

        for (; i < n; i++)
        {
            float d = a[i] - b[i];
            err += d * d;
            e0 += a[i] * a[i];
            e1 += b[i] * b[i];
        }

        return err / (Math.Sqrt(e0 * e1) + 1e-12);
    }
}
