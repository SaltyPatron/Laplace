namespace Laplace.Engine.Core;

/// <summary>
/// 4D coordinate ops on the S³ frame, bound to the engine's <c>math4d</c> kernels.
/// Centroid is the canonical composite-coord rule used by <c>hash_composer</c> when it
/// composes a tier-≥1 entity from its children (id = Merkle, coord = centroid, hilbert =
/// encode(coord)) — exposed here so non-text decomposers compose composites the IDENTICAL
/// way, never a hand-rolled average that would diverge from the seeded text tiers.
/// </summary>
public static unsafe class Math4d
{
    /// <summary>Centroid of <paramref name="points"/> (n × 4, row-major XYZM) → 4 doubles.
    /// Empty input → all zeros (engine contract). Matches hash_composer's composite coord.</summary>
    public static double[] Centroid(ReadOnlySpan<double> points)
    {
        var outv = new double[4];
        nuint n = (nuint)(points.Length / 4);
        if (n == 0) return outv;
        fixed (double* p = points)
        fixed (double* o = outv)
            NativeInterop.Math4dCentroid(p, n, o);
        return outv;
    }
}
