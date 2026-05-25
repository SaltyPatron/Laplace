namespace Laplace.Engine.Core;

/// <summary>
/// Super-Fibonacci spiral on S³ (Alexa, CVPR 2022) via the C engine — the
/// single source of math truth. Produces <c>n</c> low-discrepancy unit
/// quaternions; the substrate uses point <c>rank</c> as a codepoint's
/// coordinate, where <c>rank</c> is its DUCET collation order, so
/// collation-adjacent codepoints land near each other on the glome.
/// </summary>
public static unsafe class SuperFibonacci
{
    /// <summary>Fills <paramref name="outXyzw"/> (length 4·n, XYZW per point)
    /// with the super-Fibonacci spiral for <paramref name="n"/> points.</summary>
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

    /// <summary>Allocating convenience: returns a fresh 4·n array.</summary>
    public static double[] Generate(int n)
    {
        var buf = new double[4L * n <= int.MaxValue ? 4 * n : throw new ArgumentOutOfRangeException(nameof(n))];
        Generate(n, buf);
        return buf;
    }
}
