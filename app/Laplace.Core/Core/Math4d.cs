using Laplace.Native;

namespace Laplace.Engine.Core;

public static unsafe class Math4d
{
    public static double[] Centroid(ReadOnlySpan<double> points)
    {
        var outv = new double[4];
        int n = points.Length / 4;
        if (n == 0) return outv;

        Span<lp_coord> coords = n <= 64 ? stackalloc lp_coord[n] : new lp_coord[n];
        fixed (double* p = points)
        fixed (lp_coord* pc = coords)
        {
            for (int i = 0; i < n; i++)
                LaplaceNative.lp_coord_trunc(p + i * 4, pc + i);
            lp_coord mean;
            LaplaceNative.lp_coord_centroid(pc, (nuint)n, &mean);
            for (int d = 0; d < 4; d++)
                outv[d] = Math.ScaleB(mean.m[d], -53);
        }
        return outv;
    }

    public static double[] KarcherMean(ReadOnlySpan<double> points, double tol = 1e-12, int maxIters = 64)
    {
        _ = tol;
        _ = maxIters;
        var outv = new double[4];
        nuint n = (nuint)(points.Length / 4);
        if (n == 0) return outv;
        fixed (double* p = points)
        fixed (double* o = outv)
            LaplaceNative.lp_karcher_mean4(p, n, o);
        return outv;
    }

    public static double Frechet(ReadOnlySpan<double> p, ReadOnlySpan<double> q)
    {
        nuint np = (nuint)(p.Length / 4);
        nuint nq = (nuint)(q.Length / 4);
        if (np == 0 || nq == 0) return double.PositiveInfinity;
        fixed (double* pp = p)
        fixed (double* pq = q)
            return LaplaceNative.lp_frechet4(pp, np, pq, nq);
    }

    public static double Hausdorff(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        nuint na = (nuint)(a.Length / 4);
        nuint nb = (nuint)(b.Length / 4);
        if (na == 0 || nb == 0) return double.PositiveInfinity;
        fixed (double* pa = a)
        fixed (double* pb = b)
            return LaplaceNative.lp_hausdorff4(pa, na, pb, nb);
    }

    public static double Dtw(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        nuint na = (nuint)(a.Length / 4);
        nuint nb = (nuint)(b.Length / 4);
        if (na == 0 || nb == 0) return double.PositiveInfinity;
        nuint steps = 0;
        fixed (double* pa = a)
        fixed (double* pb = b)
            return LaplaceNative.lp_dtw4(pa, na, pb, nb, &steps);
    }

    public static nuint Edr(ReadOnlySpan<double> a, ReadOnlySpan<double> b, double eps)
    {
        nuint na = (nuint)(a.Length / 4);
        nuint nb = (nuint)(b.Length / 4);
        fixed (double* pa = a)
        fixed (double* pb = b)
            return LaplaceNative.lp_edr4(pa, na, pb, nb, eps);
    }

    public static double Distance(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        if (a.Length < 4 || b.Length < 4) return double.PositiveInfinity;
        fixed (double* pa = a)
        fixed (double* pb = b)
            return LaplaceNative.lp_distance4(pa, pb);
    }

    public static void Slerp(ReadOnlySpan<double> p0, ReadOnlySpan<double> p1, double t, Span<double> out4)
    {
        if (p0.Length < 4 || p1.Length < 4 || out4.Length < 4)
            throw new ArgumentException("4D point spans must hold at least 4 doubles.");
        fixed (double* a = p0)
        fixed (double* b = p1)
        fixed (double* o = out4)
            LaplaceNative.lp_slerp4(a, b, t, o);
    }

    public static double UnitCentroidDistance(
        double x1, double y1, double z1, double m1,
        double x2, double y2, double z2, double m2)
    {
        Span<double> p1 = [x1, y1, z1, m1];
        Span<double> p2 = [x2, y2, z2, m2];
        double r1 = Math.Sqrt(x1 * x1 + y1 * y1 + z1 * z1 + m1 * m1);
        double r2 = Math.Sqrt(x2 * x2 + y2 * y2 + z2 * z2 + m2 * m2);
        if (r1 > 1e-12) for (int d = 0; d < 4; d++) p1[d] /= r1;
        if (r2 > 1e-12) for (int d = 0; d < 4; d++) p2[d] /= r2;
        return Distance(p1, p2);
    }

    public static double SubTrajectoryFrechet(ReadOnlySpan<double> query, ReadOnlySpan<double> target)
    {
        int m = query.Length / 4;
        int n = target.Length / 4;
        if (m == 0 || n == 0) return double.PositiveInfinity;

        double fullDist = DiscreteFrechetManaged(query, m, target, n, freeTargetEndpoints: false);
        if (fullDist <= 1e-9 || (m == 1 && n == 1))
            return fullDist;

        double best = fullDist;

        if (n >= m)
        {
            double subDist = DiscreteFrechetManaged(query, m, target, n, freeTargetEndpoints: true);
            if (subDist < best)
                best = subDist;
        }
        else
        {
            double revSubDist = DiscreteFrechetManaged(target, n, query, m, freeTargetEndpoints: true)
                + 0.55 * (1.0 - (double)n / m);
            if (revSubDist < best)
                best = revSubDist;
        }

        if (m >= 2 && n >= 2)
        {
            const double MatchThreshold = 0.25;
            Span<double> pCore = m <= 128 ? stackalloc double[m * 4] : new double[m * 4];
            Span<double> qCore = n <= 128 ? stackalloc double[n * 4] : new double[n * 4];
            int mc = 0;
            for (int i = 0; i < m; i++)
            {
                var pSlice = query.Slice(i * 4, 4);
                for (int j = 0; j < n; j++)
                {
                    if (Dist4d(pSlice, target.Slice(j * 4, 4)) <= MatchThreshold)
                    {
                        pSlice.CopyTo(pCore.Slice(mc * 4, 4));
                        mc++;
                        break;
                    }
                }
            }

            if (mc >= 2)
            {
                int nc = 0;
                for (int j = 0; j < n; j++)
                {
                    var qSlice = target.Slice(j * 4, 4);
                    for (int i = 0; i < m; i++)
                    {
                        if (Dist4d(query.Slice(i * 4, 4), qSlice) <= MatchThreshold)
                        {
                            qSlice.CopyTo(qCore.Slice(nc * 4, 4));
                            nc++;
                            break;
                        }
                    }
                }

                if (nc >= 2)
                {
                    var pCoreSpan = pCore[..(mc * 4)];
                    var qCoreSpan = qCore[..(nc * 4)];
                    double coreFrechet = nc >= mc
                        ? DiscreteFrechetManaged(pCoreSpan, mc, qCoreSpan, nc, freeTargetEndpoints: true)
                        : DiscreteFrechetManaged(pCoreSpan, mc, qCoreSpan, nc, freeTargetEndpoints: false);
                    double coreDirectedHausdorff = DirectedHausdorffManaged(pCoreSpan, mc, qCoreSpan, nc);
                    double coreShape = Math.Min(coreFrechet, coreDirectedHausdorff + 0.12);
                    double coveragePenalty = 0.55 * (1.0 - (double)mc / m);
                    double coreDist = coreShape + coveragePenalty;
                    if (coreDist < best)
                        best = coreDist;
                }
            }
        }

        return best;
    }

    private static double DirectedHausdorffManaged(
        ReadOnlySpan<double> p, int m,
        ReadOnlySpan<double> q, int n)
    {
        double maxMin = 0.0;
        for (int i = 0; i < m; i++)
        {
            var pi = p.Slice(i * 4, 4);
            double minD = double.PositiveInfinity;
            for (int j = 0; j < n; j++)
            {
                double d = Dist4d(pi, q.Slice(j * 4, 4));
                if (d < minD) minD = d;
            }
            if (minD > maxMin) maxMin = minD;
        }
        return maxMin;
    }

    private static double DiscreteFrechetManaged(
        ReadOnlySpan<double> p, int m,
        ReadOnlySpan<double> q, int n,
        bool freeTargetEndpoints)
    {
        if (m == 1)
        {
            var p0 = p[..4];
            if (freeTargetEndpoints)
            {
                double minD = double.PositiveInfinity;
                for (int j = 0; j < n; j++)
                {
                    double d = Dist4d(p0, q.Slice(j * 4, 4));
                    if (d < minD) minD = d;
                }
                return minD;
            }
            else
            {
                double maxD = 0.0;
                for (int j = 0; j < n; j++)
                {
                    double d = Dist4d(p0, q.Slice(j * 4, 4));
                    if (d > maxD) maxD = d;
                }
                return maxD;
            }
        }

        Span<double> prev = n <= 256 ? stackalloc double[n] : new double[n];
        Span<double> curr = n <= 256 ? stackalloc double[n] : new double[n];

        var pFirst = p[..4];
        prev[0] = Dist4d(pFirst, q[..4]);
        for (int j = 1; j < n; j++)
        {
            double d = Dist4d(pFirst, q.Slice(j * 4, 4));
            prev[j] = freeTargetEndpoints ? d : Math.Max(d, prev[j - 1]);
        }

        for (int i = 1; i < m; i++)
        {
            var pi = p.Slice(i * 4, 4);
            double d0 = Dist4d(pi, q[..4]);
            curr[0] = Math.Max(d0, prev[0]);

            for (int j = 1; j < n; j++)
            {
                double d = Dist4d(pi, q.Slice(j * 4, 4));
                double step = prev[j - 1];
                if (prev[j] < step) step = prev[j];
                if (curr[j - 1] < step) step = curr[j - 1];
                curr[j] = d > step ? d : step;
            }

            curr.CopyTo(prev);
        }

        if (!freeTargetEndpoints)
            return prev[n - 1];

        double bestEnd = prev[0];
        for (int j = 1; j < n; j++)
        {
            if (prev[j] < bestEnd)
                bestEnd = prev[j];
        }
        return bestEnd;
    }

    private static double Dist4d(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double dx = a[0] - b[0];
        double dy = a[1] - b[1];
        double dz = a[2] - b[2];
        double dm = a[3] - b[3];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz + dm * dm);
    }

    public static int ComputeSparseEigenmap(
        ReadOnlySpan<int> rows,
        ReadOnlySpan<int> cols,
        ReadOnlySpan<double> weights,
        int nodeCount,
        int dimensions,
        Span<double> outputCoords)
    {
        if (nodeCount <= 0 || dimensions <= 0 || dimensions >= nodeCount)
            return -1;
        if (rows.Length != cols.Length || rows.Length != weights.Length)
            return -1;
        if (outputCoords.Length < checked(nodeCount * dimensions))
            return -1;

        var matrix = new double[checked(nodeCount * nodeCount)];
        for (int i = 0; i < rows.Length; i++)
        {
            int r = rows[i], c = cols[i];
            if ((uint)r >= (uint)nodeCount || (uint)c >= (uint)nodeCount || r == c) continue;
            double w = weights[i];
            if (!double.IsFinite(w) || w <= 0.0) continue;
            matrix[r * nodeCount + c] += w;
            matrix[c * nodeCount + r] += w;
        }

        Span<double> lambda = dimensions <= 32 ? stackalloc double[dimensions] : new double[dimensions];
        fixed (double* pW = matrix)
        fixed (double* pY = outputCoords)
        fixed (double* pL = lambda)
            return LaplaceNative.lp_eigenmap(pW, (nuint)nodeCount, (nuint)dimensions, pY, pL);
    }
}
