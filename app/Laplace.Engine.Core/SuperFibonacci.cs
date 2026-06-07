namespace Laplace.Engine.Core;

public static unsafe class SuperFibonacci
{
    public static void Generate(int n, Span<double> outXyzw)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (outXyzw.Length < 4L * n)
            throw new ArgumentException($"buffer must hold 4*{n} doubles", nameof(outXyzw));
        if (n == 0) return;
        fixed (double* p = outXyzw)
        {
            NativeInterop.SuperFibonacci((nuint)n, p);
        }
    }

    public static double[] Generate(int n)
    {
        var buf = new double[4L * n <= int.MaxValue ? 4 * n : throw new ArgumentOutOfRangeException(nameof(n))];
        Generate(n, buf);
        return buf;
    }
}
