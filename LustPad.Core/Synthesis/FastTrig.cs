namespace LustPad.Core.Synthesis;

/// <summary>
/// Loop-lock-safe sine via a 2048-point table. Absolute-time LFOs stay periodic
/// over the loop body; we just skip <see cref="MathF.Sin"/> in the inner loop.
/// </summary>
internal static class FastTrig
{
    public const int Size = 2048;
    private static readonly float[] Table = Create();

    private static float[] Create()
    {
        var t = new float[Size + 1];
        for (int i = 0; i <= Size; i++)
            t[i] = MathF.Sin(i * (2f * MathF.PI / Size));
        t[Size] = 0f; // == sin(0), with a duplicate slot so idx=2047.x can lerp to 2048
        return t;
    }

    /// <param name="turns">Phase in cycles (any real). 1.0 ≡ 2π.</param>
    public static float SinTurns(double turns)
    {
        double t = turns - Math.Floor(turns);
        if (t < 0.0) t += 1.0;
        double idx = t * Size;
        int i = (int)idx;
        float frac = (float)(idx - i);
        return Table[i] + (Table[i + 1] - Table[i]) * frac;
    }
}
