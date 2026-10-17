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

    /// <summary>
    /// Discrete 4-D Fréchet distance between two trajectories P and Q (each packed as 4 doubles per vertex).
    /// </summary>
    public static double Frechet(ReadOnlySpan<double> p, ReadOnlySpan<double> q)
    {
        nuint np = (nuint)(p.Length / 4);
        nuint nq = (nuint)(q.Length / 4);
        if (np == 0 || nq == 0) return double.PositiveInfinity;
        fixed (double* pp = p)
        fixed (double* pq = q)
            return NativeInterop.Math4dFrechet(pp, np, pq, nq);
    }

    /// <summary>
    /// Symmetric 4-D Hausdorff distance between two point sets A and B (each packed as 4 doubles per point).
    /// </summary>
    public static double Hausdorff(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        nuint na = (nuint)(a.Length / 4);
        nuint nb = (nuint)(b.Length / 4);
        if (na == 0 || nb == 0) return double.PositiveInfinity;
        fixed (double* pa = a)
        fixed (double* pb = b)
            return NativeInterop.Math4dHausdorff(pa, na, pb, nb);
    }

    /// <summary>
    /// Spherical linear interpolation (SLERP) along the shortest geodesic on unit S^3
    /// from <paramref name="p0"/> (t = 0) to <paramref name="p1"/> (t = 1).
    /// </summary>
    public static void Slerp(ReadOnlySpan<double> p0, ReadOnlySpan<double> p1, double t, Span<double> out4)
    {
        double x0 = p0[0], y0 = p0[1], z0 = p0[2], m0 = p0[3];
        double x1 = p1[0], y1 = p1[1], z1 = p1[2], m1 = p1[3];

        double r0 = Math.Sqrt(x0 * x0 + y0 * y0 + z0 * z0 + m0 * m0);
        double r1 = Math.Sqrt(x1 * x1 + y1 * y1 + z1 * z1 + m1 * m1);
        if (r0 > 1e-12) { x0 /= r0; y0 /= r0; z0 /= r0; m0 /= r0; }
        if (r1 > 1e-12) { x1 /= r1; y1 /= r1; z1 /= r1; m1 /= r1; }

        double cosTheta = Math.Clamp(x0 * x1 + y0 * y1 + z0 * z1 + m0 * m1, -1.0, 1.0);
        double theta = Math.Acos(cosTheta);
        double sinTheta = Math.Sin(theta);

        double rx, ry, rz, rm;
        if (sinTheta > 1e-7)
        {
            double s0 = Math.Sin((1.0 - t) * theta) / sinTheta;
            double s1 = Math.Sin(t * theta) / sinTheta;
            rx = s0 * x0 + s1 * x1;
            ry = s0 * y0 + s1 * y1;
            rz = s0 * z0 + s1 * z1;
            rm = s0 * m0 + s1 * m1;
        }
        else
        {
            double s0 = 1.0 - t;
            rx = s0 * x0 + t * x1;
            ry = s0 * y0 + t * y1;
            rz = s0 * z0 + t * z1;
            rm = s0 * m0 + t * m1;
        }

        double nr = Math.Sqrt(rx * rx + ry * ry + rz * rz + rm * rm);
        if (nr > 1e-12)
        {
            out4[0] = rx / nr;
            out4[1] = ry / nr;
            out4[2] = rz / nr;
            out4[3] = rm / nr;
        }
        else
        {
            out4[0] = x1;
            out4[1] = y1;
            out4[2] = z1;
            out4[3] = m1;
        }
    }

    /// <summary>
    /// Euclidean chord distance on unit S^3 between two 4-D centroid vectors after projecting both onto unit S^3.
    /// </summary>
    public static double UnitCentroidDistance(
        double x1, double y1, double z1, double m1,
        double x2, double y2, double z2, double m2)
    {
        var r1 = Math.Sqrt(x1 * x1 + y1 * y1 + z1 * z1 + m1 * m1);
        var r2 = Math.Sqrt(x2 * x2 + y2 * y2 + z2 * z2 + m2 * m2);
        if (r1 > 1e-12) { x1 /= r1; y1 /= r1; z1 /= r1; m1 /= r1; }
        if (r2 > 1e-12) { x2 /= r2; y2 /= r2; z2 /= r2; m2 /= r2; }
        var dx = x1 - x2;
        var dy = y1 - y2;
        var dz = z1 - z2;
        var dm = m1 - m2;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz + dm * dm);
    }

    /// <summary>
    /// Sub-trajectory aware 4-D discrete Fréchet distance from <paramref name="query"/> (m vertices)
    /// into <paramref name="target"/> (n vertices). Evaluates full-curve Fréchet, contiguous
    /// free-endpoint sub-trajectory Fréchet (when n &gt;= m), and monotonic core sub-trajectory
    /// Fréchet (penalized by unmatched query fraction).
    /// </summary>
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
}
