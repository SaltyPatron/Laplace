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
}
