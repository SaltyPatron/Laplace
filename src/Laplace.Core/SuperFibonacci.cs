namespace Laplace.Core;

using System;

using Laplace.Core.Abstractions;
using Laplace.Core.Native;

/// <summary>
/// Managed wrapper over <c>laplace_super_fibonacci_4d</c>. Used by
/// <c>UcdSeeder</c> (Track E / E6) to place every Unicode codepoint atom
/// (full 1,114,112 across 17 planes) on S³ in deterministic order.
/// Phase 2 / Track D / D2.
/// </summary>
public sealed class SuperFibonacci : ISuperFibonacci
{
    public Point4D At(int i, int total)
    {
        Span<double> q = stackalloc double[4];
        unsafe
        {
            fixed (double* qp = q)
            {
                NativeSuperFib.At(i, total, qp);
            }
        }
        return new Point4D(q[0], q[1], q[2], q[3]);
    }

    public Point4D[] Range(int startInclusive, int endExclusive, int total)
    {
        if (endExclusive < startInclusive)
        {
            throw new ArgumentException("endExclusive must be >= startInclusive.");
        }
        var n = endExclusive - startInclusive;
        var buf = new double[n * 4];
        unsafe
        {
            fixed (double* bp = buf)
            {
                NativeSuperFib.Range(startInclusive, endExclusive, total, bp);
            }
        }
        var result = new Point4D[n];
        for (int i = 0; i < n; ++i)
        {
            result[i] = new Point4D(buf[i * 4 + 0], buf[i * 4 + 1], buf[i * 4 + 2], buf[i * 4 + 3]);
        }
        return result;
    }
}
