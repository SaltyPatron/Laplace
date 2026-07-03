namespace Laplace.Engine.Core;

public static unsafe class ScoreLaw
{
    public const long FpScale = 1_000_000_000L;

    public static long ScoreFp(double signedMagnitude, double arenaScale)
        => NativeInterop.LaplaceScoreFp(signedMagnitude, arenaScale);

    public static double InverseFp(long scoreFp, double arenaScale)
        => NativeInterop.LaplaceScoreInverseFp(scoreFp, arenaScale);

    public static void ScoreBatchFp(ReadOnlySpan<float> weights, double arenaScale, Span<long> scoresOut)
    {
        if (scoresOut.Length < weights.Length)
            throw new ArgumentException("scoresOut shorter than weights", nameof(scoresOut));
        if (weights.IsEmpty) return;
        fixed (float* w = weights)
        fixed (long* o = scoresOut)
            NativeInterop.LaplaceScoreBatchFp(w, (nuint)weights.Length, arenaScale, o);
    }
}
