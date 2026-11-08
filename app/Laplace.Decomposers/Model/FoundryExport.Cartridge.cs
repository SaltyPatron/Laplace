using Laplace.Decomposers.Model;
using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

internal static partial class FoundryExport
{
    private static PlaneCoo MapLayerEdges(
        Dictionary<Hash128, List<int>> tokenSlots,
        IReadOnlyList<NpgsqlFoundryReads.LayerEdgeRow> rows,
        int degreeCap,
        Func<NpgsqlFoundryReads.LayerEdgeRow, double> weight)
    {
        var adj = new Dictionary<int, List<(int Col, double W)>>();
        foreach (var edge in rows)
        {
            if (!tokenSlots.TryGetValue(FromBytes(edge.SubjectId), out var subj)) continue;
            if (!tokenSlots.TryGetValue(FromBytes(edge.ObjectId), out var obj)) continue;
            double w = weight(edge);
            if (w == 0.0) continue;
            foreach (int s in subj)
            {
                if (!adj.TryGetValue(s, out var row)) adj[s] = row = new List<(int, double)>(8);
                foreach (int o in obj) row.Add((o, w));
            }
        }
        return CooFromAdj(adj, degreeCap);
    }

    // Trim a row to its top-degreeCap edges by |weight| desc, Col asc as the
    // deterministic tie-break; every plane reader applies this ordering. Top-k is
    // mergeable, so trimming during accumulation leaves the final set unchanged.
    internal static void TrimRowToTopK(List<(int Col, double W)> row, int degreeCap)
    {
        row.Sort((a, b) =>
        {
            int c = Math.Abs(b.W).CompareTo(Math.Abs(a.W));
            return c != 0 ? c : a.Col.CompareTo(b.Col);
        });
        if (row.Count > degreeCap) row.RemoveRange(degreeCap, row.Count - degreeCap);
    }

    internal static PlaneCoo CooFromAdj(Dictionary<int, List<(int Col, double W)>> adj, int degreeCap)
    {
        long kept = 0;
        foreach (var row in adj.Values)
        {
            TrimRowToTopK(row, degreeCap);
            kept += row.Count;
        }
        var rows = new int[kept]; var cols = new int[kept]; var vals = new double[kept];
        long at = 0;
        foreach (var r in adj.Keys.OrderBy(k => k))
            foreach (var (c, w) in adj[r]) { rows[at] = r; cols[at] = c; vals[at] = w; at++; }
        return new PlaneCoo(rows, cols, vals);
    }




    internal static PlaneCoo Normalize(PlaneCoo p)
    {
        double max = 0;
        foreach (var v in p.Vals) max = Math.Max(max, Math.Abs(v));
        if (max <= 0 || max == 1.0) return p;
        var vals = new double[p.Nnz];
        for (int i = 0; i < vals.Length; i++) vals[i] = p.Vals[i] / max;
        return p with { Vals = vals };
    }

    /// Nonnegative part of a signed plane, for the normalized-Laplacian eigenmap
    /// affinity. Non-positive edges are dropped, not mirrored: a refuted edge is not
    /// affinity. Operator planes stay signed so refutation reaches attention negatively.
    internal static PlaneCoo PositivePart(PlaneCoo p)
    {
        int kept = 0;
        for (int i = 0; i < p.Nnz; i++) if (p.Vals[i] > 0) kept++;
        if (kept == p.Nnz) return p;
        var rows = new int[kept]; var cols = new int[kept]; var vals = new double[kept];
        int at = 0;
        for (int i = 0; i < p.Nnz; i++)
        {
            if (p.Vals[i] <= 0) continue;
            rows[at] = p.Rows[i]; cols[at] = p.Cols[i]; vals[at] = p.Vals[i]; at++;
        }
        return new PlaneCoo(rows, cols, vals);
    }

    internal static PlaneCoo Union(params PlaneCoo[] planes)
    {
        long total = planes.Sum(p => (long)p.Nnz);
        var rows = new int[total]; var cols = new int[total]; var vals = new double[total];
        long at = 0;
        foreach (var p in planes)
        {
            Array.Copy(p.Rows, 0, rows, at, p.Nnz);
            Array.Copy(p.Cols, 0, cols, at, p.Nnz);
            Array.Copy(p.Vals, 0, vals, at, p.Nnz);
            at += p.Nnz;
        }
        return new PlaneCoo(rows, cols, vals);
    }

    internal sealed record BasisStats(int SpectralRank, int ZeroSpectralTokens, double ProcrustesResidual);









    internal static double[] BuildBasisAffinity(
        int vocab, int dModel, PlaneCoo aff, double[]?[] anchors, Hash128 seed,
        out BasisStats stats)
    {
        int k = Math.Min(dModel - 1, Math.Min(FoundryDefaults.BasisRank, vocab));
        var A = new float[(long)vocab * vocab];
        for (long e = 0; e < aff.Nnz; e++)
        {
            int x = aff.Rows[e], y = aff.Cols[e];
            if (x < 0 || x >= vocab || y < 0 || y >= vocab) continue;
            float w = (float)aff.Vals[e];
            A[(long)x * vocab + y] += w;
            A[(long)y * vocab + x] += w;
        }
        var U = new float[(long)vocab * vocab];
        var S = new float[vocab];
        var Vt = new float[(long)vocab * vocab];
        nuint outRank = 0; int rc;
        unsafe
        {
            fixed (float* pa = A, pu = U, ps = S, pvt = Vt)
                rc = SynInterop.TensorSvdTruncate(pa, (nuint)vocab, (nuint)vocab, 0.0,
                                                  &outRank, pu, ps, pvt, (nuint)vocab);
        }
        if (rc != 0) throw new InvalidOperationException($"tensor_svd_truncate (affinity) rc={rc} (vocab={vocab})");
        int kk = Math.Min(k, (int)outRank);
        var e2 = new double[(long)vocab * dModel];
        int zero = 0;
        for (int i = 0; i < vocab; i++)
        {
            long off = (long)i * dModel; double n2 = 0;
            for (int c = 0; c < kk; c++)
            {
                double v = (double)U[(long)i * vocab + c] * Math.Sqrt(Math.Max(0f, S[c]));
                e2[off + c] = v; n2 += v * v;
            }
            double inv = n2 > 1e-24 ? 1.0 / Math.Sqrt(n2) : 0.0;
            if (inv == 0.0) { e2[off + Math.Max(0, dModel - 2)] = 1.0; inv = 1.0; zero++; }
            for (int c = 0; c < dModel - 1; c++) e2[off + c] *= inv;
            e2[off + dModel - 1] = BiasValue;
        }
        stats = new BasisStats(kk, zero, double.NaN);
        return e2;
    }













    internal static void FactorAdjacency(
        PlaneCoo adj, int vocab, int dim, out double[] embed, out double[] lmHead, out int usedRank,
        bool conditional = false, bool suppressSelf = false, double dehub = 0.0)
    {
        var rowSum = new double[vocab];
        var colSum = new double[vocab];
        double total = 0;

        var ex = new int[adj.Nnz]; var ey = new int[adj.Nnz]; var ew = new double[adj.Nnz]; int en = 0;
        for (long e = 0; e < adj.Nnz; e++)
        {
            int x = adj.Rows[e], y = adj.Cols[e];
            if (x < 0 || x >= vocab || y < 0 || y >= vocab) continue;
            if (suppressSelf && x == y) continue;
            double w = adj.Vals[e];
            ex[en] = x; ey[en] = y; ew[en] = w; en++;
            rowSum[x] += w; colSum[y] += w; total += w;
        }











        var sx = new int[en]; var sy = new int[en]; var sv = new double[en]; int sn = 0;
        for (int i = 0; i < en; i++)
        {
            double val;
            if (conditional)
            {
                double rs = rowSum[ex[i]];
                if (rs <= 0) continue;
                val = Math.Log(ew[i] / rs * vocab);




                if (dehub != 0.0 && colSum[ey[i]] > 0 && total > 0)
                    val -= dehub * Math.Log(colSum[ey[i]] / total);
            }
            else
            {
                double denom = rowSum[ex[i]] * colSum[ey[i]];
                if (denom <= 0) continue;
                val = Math.Log(ew[i] * total / denom);
                if (val <= 0) continue;
            }
            if (val == 0) continue;
            sx[sn] = ex[i]; sy[sn] = ey[i]; sv[sn] = val; sn++;
        }

        embed = new double[(long)vocab * dim];
        lmHead = new double[(long)vocab * dim];





        if (vocab <= FoundryDefaults.DenseSvdMax)
        {
            var As = new float[(long)vocab * vocab];
            for (int i = 0; i < sn; i++) As[(long)sx[i] * vocab + sy[i]] = (float)sv[i];
            var U = new float[(long)vocab * vocab];
            var S = new float[vocab];
            var Vt = new float[(long)vocab * vocab];
            nuint outRank = 0; int rc;
            unsafe
            {
                fixed (float* pa = As, pu = U, ps = S, pvt = Vt)
                    rc = SynInterop.TensorSvdTruncate(pa, (nuint)vocab, (nuint)vocab, 0.0,
                                                      &outRank, pu, ps, pvt, (nuint)vocab);
            }
            if (rc != 0) throw new InvalidOperationException($"tensor_svd_truncate (adjacency) rc={rc} (vocab={vocab})");
            int kk = Math.Min(dim, (int)outRank);
            usedRank = kk;

            for (int c = 0; c < kk; c++)
            {
                double s = Math.Max(0f, S[c]);
                for (int i = 0; i < vocab; i++)
                {
                    embed[(long)i * dim + c] = (double)U[(long)i * vocab + c];
                    lmHead[(long)i * dim + c] = (double)Vt[(long)c * vocab + i] * s;
                }
            }
        }
        else
        {
            usedRank = FactorSparseRandomized(sx, sy, sv, sn, vocab, dim, embed, lmHead);
        }
    }






    internal static int FactorSparseRandomized(
        int[] sx, int[] sy, double[] sv, int sn, int vocab, int dim, double[] embed, double[] lmHead)
    {
        int L = Math.Min(vocab, dim + FoundryDefaults.RsvdOversample);
        int q = FoundryDefaults.RsvdPower;

        var Y = new double[(long)L * vocab];
        var Om = new double[(long)L * vocab];
        ulong seed = SplitMix(0x9E3779B97F4A7C15UL ^ (ulong)vocab ^ ((ulong)dim << 32));
        for (long t = 0; t < (long)L * vocab; t++) Om[t] = Gaussian(ref seed);
        SpMatVec(sx, sy, sv, sn, Om, Y, L, vocab, false);
        var Z = new double[(long)L * vocab];
        for (int it = 0; it < q; it++)
        {
            Array.Clear(Z); SpMatVec(sx, sy, sv, sn, Y, Z, L, vocab, true);
            Array.Clear(Y); SpMatVec(sx, sy, sv, sn, Z, Y, L, vocab, false);
        }





        var G = new double[(long)L * L];
        System.Threading.Tasks.Parallel.For(0, L, i =>
        {
            long bi = (long)i * vocab;
            for (int j = 0; j <= i; j++)
            {
                long bj = (long)j * vocab;
                double d = 0; for (int t = 0; t < vocab; t++) d += Y[bi + t] * Y[bj + t];
                G[(long)i * L + j] = d; G[(long)j * L + i] = d;
            }
        });
        var Gf = new float[(long)L * L];
        for (long t = 0; t < (long)L * L; t++) Gf[t] = (float)G[t];
        var Wg = new float[(long)L * L]; var Sg = new float[L]; var Vg = new float[(long)L * L];
        nuint gRank = 0; int grc;
        unsafe { fixed (float* pg = Gf, pu = Wg, ps = Sg, pv = Vg) grc = SynInterop.TensorSvdTruncate(pg, (nuint)L, (nuint)L, 0.0, &gRank, pu, ps, pv, (nuint)L); }
        if (grc != 0) throw new InvalidOperationException($"tensor_svd_truncate (rsvd gram) rc={grc} (L={L})");
        double s0g = Sg.Length > 0 ? Sg[0] : 0;
        int rkQ = 0; while (rkQ < L && Sg[rkQ] > 1e-10 * s0g && Sg[rkQ] > 0) rkQ++;
        rkQ = Math.Max(1, rkQ);
        var Q = new double[(long)rkQ * vocab];
        System.Threading.Tasks.Parallel.For(0, rkQ, k =>
        {
            double invsq = 1.0 / Math.Sqrt(Sg[k]);
            long bk = (long)k * vocab;
            for (int i = 0; i < L; i++)
            {
                double w = (double)Wg[(long)i * L + k] * invsq;
                long bi = (long)i * vocab;
                for (int t = 0; t < vocab; t++) Q[bk + t] += w * Y[bi + t];
            }
        });
        var B = new double[(long)rkQ * vocab];
        SpMatVecQ(sx, sy, sv, sn, Q, B, rkQ, vocab);
        var Bf = new float[(long)rkQ * vocab];
        for (long t = 0; t < (long)rkQ * vocab; t++) Bf[t] = (float)B[t];
        var Ub = new float[(long)rkQ * rkQ]; var Sb = new float[rkQ]; var Vtb = new float[(long)rkQ * vocab];
        nuint outRank = 0; int rc;
        unsafe
        {
            fixed (float* pb = Bf, pu = Ub, ps = Sb, pvt = Vtb)
                rc = SynInterop.TensorSvdTruncate(pb, (nuint)rkQ, (nuint)vocab, 0.0, &outRank, pu, ps, pvt, (nuint)rkQ);
        }
        if (rc != 0) throw new InvalidOperationException($"tensor_svd_truncate (rsvd band) rc={rc} (rkQ={rkQ}, vocab={vocab})");
        int kk = Math.Min(dim, (int)outRank);

        System.Threading.Tasks.Parallel.For(0, vocab, x =>
        {
            for (int c = 0; c < kk; c++)
            {
                double acc = 0;
                for (int j = 0; j < rkQ; j++) acc += Q[(long)j * vocab + x] * Ub[(long)j * rkQ + c];
                embed[(long)x * dim + c] = acc;
                lmHead[(long)x * dim + c] = (double)Vtb[(long)c * vocab + x] * Math.Max(0f, Sb[c]);
            }
        });
        return kk;
    }



    static void SpMatVec(int[] sx, int[] sy, double[] sv, int sn, double[] M, double[] Outp, int L, int vocab, bool transpose)
    {
        System.Threading.Tasks.Parallel.For(0, L, c =>
        {
            long baseC = (long)c * vocab;
            for (int i = 0; i < sn; i++)
            {
                int a = transpose ? sy[i] : sx[i];
                int b = transpose ? sx[i] : sy[i];
                Outp[baseC + a] += sv[i] * M[baseC + b];
            }
        });
    }

    static void SpMatVecQ(int[] sx, int[] sy, double[] sv, int sn, double[] Q, double[] B, int L, int vocab)
    {
        System.Threading.Tasks.Parallel.For(0, L, c =>
        {
            long baseC = (long)c * vocab;
            for (int i = 0; i < sn; i++) B[baseC + sy[i]] += Q[baseC + sx[i]] * sv[i];
        });
    }




    internal static double[] BuildBasis(
        int vocab, int dModel, PlaneCoo leGraph, double[]?[] anchors, Hash128 seed,
        out BasisStats stats, bool coordDirect = false, double? coordScale = null,
        Hilbert128?[]? hilbertKeys = null)
    {
        bool coordOnly = FoundryDefaults.CoordOnly;
        // Hilbert content-PE reserves its trailing dims before choosing k, so a
        // full-rank spectrum cannot consume them.
        int peReserve = (hilbertKeys is not null && dModel > 24) ? 8 : 0;
        int k = coordOnly
            ? Math.Min(4, dModel - 1)
            : Math.Min(Math.Min(dModel - 1 - peReserve, FoundryDefaults.BasisRank),
                       Math.Max(2, vocab - 2));
        var y = GC.AllocateUninitializedArray<double>(checked(vocab * k), pinned: true);
        if (coordOnly)
        {






            Array.Clear(y, 0, y.Length);
            for (int i = 0; i < vocab; i++)
            {
                var a = anchors[i];
                if (a is null) continue;
                for (int d = 0; d < 4 && d < k; d++) y[(long)i * k + d] = a[d];
            }
        }
        else
        {
            int rc; nuint basisRank=(nuint)k;
            unsafe
            {
                fixed (int* pr = leGraph.Rows) fixed (int* pc = leGraph.Cols)
                fixed (double* pv = leGraph.Vals) fixed (double* py = y)
                    rc = SynInterop.GraphBasis(pr,pc,pv,(nuint)leGraph.Nnz,(nuint)vocab,(nuint)k,py,&basisRank);
            }
            if (rc != 0)
                throw new InvalidOperationException(
                    $"laplacian_eigenmaps_from_sparse_graph rc={rc} (vocab={vocab}, K={k}, nnz={leGraph.Nnz})");


            if((int)basisRank!=k) {
                int effective=(int)basisRank;var trimmed=new double[(long)vocab*effective];
                for(int x=0;x<vocab;x++)Array.Copy(y,(long)x*k,trimmed,(long)x*effective,effective);
                y=trimmed;k=effective;
            }
            var yt = new double[(long)k * vocab];
            for (int i = 0; i < vocab; i++)
                for (int d = 0; d < k; d++) yt[(long)d * vocab + i] = y[(long)i * k + d];
            int gsRc;
            unsafe { fixed (double* p = yt) gsRc = DynInterop.GramSchmidtOrthonormalize(p, (nuint)k, (nuint)vocab); }
            // A rank-deficient spectrum is an error: downstream steps assume an
            // orthonormal basis.
            if (gsRc != 0)
                throw new InvalidOperationException(
                    $"gram_schmidt_orthonormalize rc={gsRc} (vocab={vocab}, K={k}) — the spectral "
                    + "basis is rank-deficient; a non-orthonormal basis must not be used silently");
            for (int i = 0; i < vocab; i++)
                for (int d = 0; d < k; d++) y[(long)i * k + d] = yt[(long)d * vocab + i];
        }

        int zeroSpectral = 0;
        for (int i = 0; i < vocab; i++)
        {
            double n2 = 0;
            for (int d = 0; d < k; d++) { double v = y[(long)i * k + d]; n2 += v * v; }
            if (n2 < 1e-24) zeroSpectral++;
        }



        double resid = double.NaN;
        var fitIdx = new List<int>();
        for (int i = 0; i < vocab; i++) if (anchors[i] is not null) fitIdx.Add(i);
        var e = new double[(long)vocab * dModel];
        bool coordDirectMode = coordDirect || FoundryDefaults.CoordDirect;
        if (coordOnly)
        {

        }
        else if (coordDirectMode && fitIdx.Count > 0)
        {
            double cs = coordScale ?? FoundryDefaults.CoordScale;
            for (int i = 0; i < vocab; i++)
            {
                var a = anchors[i];
                if (a is null) continue;
                for (int d = 0; d < 4 && d < k; d++) y[(long)i * k + d] = a[d] * cs;
            }
        }
        else if (fitIdx.Count >= 6 && k >= 4)
        {
            var yFit = new double[(long)fitIdx.Count * k];
            var b = new double[(long)fitIdx.Count * 4];
            for (int f = 0; f < fitIdx.Count; f++)
            {
                Array.Copy(y, (long)fitIdx[f] * k, yFit, (long)f * k, k);
                var a = anchors[fitIdx[f]]!;
                b[f * 4] = a[0]; b[f * 4 + 1] = a[1]; b[f * 4 + 2] = a[2]; b[f * 4 + 3] = a[3];
            }
            IntPtr t;
            unsafe
            {
                fixed (double* py = yFit) fixed (double* pb = b)
                    t = DynInterop.ProcrustesFit(py, (nuint)fitIdx.Count, (nuint)k, pb);
            }
            if (t != IntPtr.Zero)
            {
                try
                {
                    resid = DynInterop.ProcrustesResidual(t);
                    double specSq = 0, anchSq = 0;
                    var a4 = new double[(long)vocab * 4];
                    var outv = new double[4];
                    for (int i = 0; i < vocab; i++)
                    {
                        unsafe
                        {
                            fixed (double* py = &y[(long)i * k]) fixed (double* po = outv)
                                DynInterop.ProcrustesApply(t, py, (nuint)k, po);
                        }
                        for (int d = 0; d < 4; d++)
                        {
                            a4[(long)i * 4 + d] = outv[d];
                            anchSq += outv[d] * outv[d];
                            double s = y[(long)i * k + d];
                            specSq += s * s;
                        }
                    }




                    double scale = anchSq > 0 ? Math.Sqrt(specSq / anchSq) : 1.0;
                    if (FoundryDefaults.Procrustes)
                        for (int i = 0; i < vocab; i++)
                            for (int d = 0; d < 4; d++)
                                y[(long)i * k + d] = a4[(long)i * 4 + d] * scale;
                }
                finally { DynInterop.ProcrustesFree(t); }
            }
        }

        for (int i = 0; i < vocab; i++)
            Array.Copy(y, (long)i * k, e, (long)i * dModel, k);







        double capFrac = FoundryDefaults.CapFrac;
        int capDims = Math.Max(1, dModel - 1 - k);
        double capScale = Math.Sqrt(capFrac * ((double)k / vocab) / capDims);
        for (int d = k; d < dModel - 1; d++)
        {
            ulong s = SplitMix(unchecked((ulong)seed.Hi) ^ (ulong)d);
            for (int i = 0; i < vocab; i++)
                e[(long)i * dModel + d] = Gaussian(ref s) * capScale;
        }

        // Dims between the spectral basis and the trailing PE/bias are seeded Gaussian
        // capacity. This block reports the ResidualStrata layout of d_model (S surface/PE,
        // W word spectral rank, C sense, F frames, G relation bands) and the fraction not
        // carried by S+W. On this path C is noise-filled and F has width zero. Tensor values
        // are not changed here.
        try
        {
            int peDimsPlanned = (hilbertKeys is not null && dModel > 24) ? Math.Min(8, dModel - 1 - k) : 0;
            var strata = ResidualStrata.Allocate(new ResidualStrata.Census(
                DModel: dModel,
                PeDims: peDimsPlanned + 1,              // +1: the trailing bias dim is surface-side
                WordSpectralRank: k,
                SenseSpectralRank: Math.Max(2, dModel - 1 - k - peDimsPlanned - FoundryDefaults.HighwayBandCount),
                FramesWithWitnessedLus: 0,
                BandCount: FoundryDefaults.HighwayBandCount));

            int named = strata.S.Width + strata.W.Width;
            double anon = dModel <= 0 ? 0.0 : 1.0 - (double)named / dModel;
            Console.WriteLine($"  {strata.Describe()}");
            Console.WriteLine($"  residual: {named:N0}/{dModel:N0} dims carry substrate content "
                            + $"(S+W); {anon:P1} is unallocated capacity noise — C awaits a sense-graph "
                            + "eigenmap, F awaits a witnessed-LU frame census (doc 18 §2 / GH #521)");
        }
        catch (InvalidOperationException ex)
        {
            // The layout is reported, not applied, so a d_model too small for the census
            // is printed and synthesis continues.
            Console.WriteLine($"  strata: NOT ALLOCATABLE — {ex.Message}");
        }


        for (int i = 0; i < vocab; i++)
        {
            long off = (long)i * dModel;
            double n2 = 0;
            for (int d = 0; d < dModel - 1; d++) { double v = e[off + d]; n2 += v * v; }
            double inv = n2 > 1e-24 ? 1.0 / Math.Sqrt(n2) : 0.0;
            if (inv == 0.0) { e[off + Math.Max(0, dModel - 2)] = 1.0; inv = 1.0; }
            for (int d = 0; d < dModel - 1; d++) e[off + d] *= inv;
            e[off + dModel - 1] = BiasValue;
        }

        // Hilbert content-PE in the trailing capacity dims, written after row
        // normalization so every token carries it at the same fixed HilbertPeScale.
        if (hilbertKeys is not null)
        {
            int peCapDims = dModel - 1 - k;
            int peDims = Math.Min(8, peCapDims);
            if (peDims > 0)
            {
                double peScale = FoundryDefaults.HilbertPeScale;
                int peBase = dModel - 1 - peDims;
                int placedPe = 0;
                for (int i = 0; i < vocab; i++)
                {
                    if (i >= hilbertKeys.Length || hilbertKeys[i] is not { } hb) continue;
                    long off = (long)i * dModel;
                    // Leading bytes of the packed 128-bit index = coarsest S³ locality.
                    unsafe
                    {
                        for (int d = 0; d < peDims; d++)
                            e[off + peBase + d] = (2.0 * hb.Bytes[d] / 255.0 - 1.0) * peScale;
                    }
                    placedPe++;
                }
                Console.WriteLine($"  hilbert content-PE: {placedPe:N0} tokens in {peDims} trailing dims (scale {peScale})");
            }
        }

        stats = new BasisStats(k, zeroSpectral, resid);
        return e;
    }

}
