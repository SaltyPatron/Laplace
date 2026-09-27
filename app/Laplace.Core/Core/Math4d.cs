namespace Laplace.Engine.Core;

public static unsafe class Math4d
{
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

    /// <summary>
    /// Intrinsic (Riemannian) barycentre on S3. Unlike Centroid, the result lies on the
    /// sphere at norm 1 rather than inside the 4-ball, so it stays defined where the
    /// Euclidean centroid is near the origin and its direction is float noise.
    /// Permutation-invariant: all three internal accumulations run in a canonical
    /// order, so a placement is reproducible under constituent reordering.
    /// </summary>
    public static double[] KarcherMean(
        ReadOnlySpan<double> points, double tol = 1e-12, int maxIters = 64)
    {
        var outv = new double[4];
        nuint n = (nuint)(points.Length / 4);
        if (n == 0) return outv;
        fixed (double* p = points)
        fixed (double* o = outv)
            NativeInterop.Math4dKarcherMean(p, n, null, tol, maxIters, o);
        return outv;
    }
}
