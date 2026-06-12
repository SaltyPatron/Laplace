using global::Npgsql;
using Laplace.Engine.Core;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Cli;

// The foundry: pours adjudicated token→token consensus into a user-declared mold.
// Inputs are the consensus planes, the recipe (shapes), and the tokenizer (which
// token entities fill the mold's vocab) — never model weights. The embedding basis
// is GENERATED (Laplacian eigenmaps over the consensus graph, Gram-Schmidt
// orthonormalized, Procrustes-anchored to token content coordinates); interior
// tensors are truncated-SVD factorizations of the consensus operators projected
// through that basis. There is no inverse score law and no per-witness scale
// calibration: export renders consensus, it does not invert an ingest.
//
// Basis layout per token row [dModel]:
//   [0..K)          spectral coordinates of the consensus graph (first 4 anchored)
//   [K..dModel-1)   deterministic capacity dims (seeded from the recipe, no clock)
//   [dModel-1]      the bias channel: constant BiasValue for every token. Attention
//                   and FFN factors never write this dim, so it survives depth; the
//                   gate tensor reads ONLY this dim, making SiLU(gate·x) a stable
//                   positive scalar — the SwiGLU mold carries a linear FFN operator.
//                   lm_head sees a uniform logit shift from it (softmax-invariant).
internal static class FoundryExport
{
    internal const double BiasValue = 1.0;

    internal sealed record PlaneCoo(int[] Rows, int[] Cols, double[] Vals)
    {
        public int Nnz => Rows.Length;
        public static readonly PlaneCoo Empty = new([], [], []);
    }

    internal static int EnvInt(string name, int dflt) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;

    internal static double EnvDouble(string name, double dflt) =>
        double.TryParse(Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : dflt;

    // A plane is named, never hand-rolled: ('consensus', TYPE) reads adjudicated
    // eff-μ RELATIVE TO NEUTRAL (signed; refuted < 0); ('traj', next|gap|window, n)
    // reads conditional frequencies straight from the witnessed trajectories. The
    // SQL surface (laplace.token_plane) is the single definition both this reader
    // and every audit/walk view share.
    internal readonly record struct PlaneSpec(string Family, string Name, int? Arg)
    {
        public static PlaneSpec Consensus(string name) => new("consensus", name, null);
        public static PlaneSpec TrajNext() => new("traj", "next", null);
        public static PlaneSpec TrajGap(int g) => new("traj", "gap", g);
        public static PlaneSpec TrajWindow(int w) => new("traj", "window", w);
        public override string ToString() => Arg is null ? $"{Family}:{Name}" : $"{Family}:{Name}:{Arg}";
    }

    // One set-based read per plane; entity→ordinal mapping is in-process (perf-cache
    // derived token entities), so the DB is touched exactly once per plane. Degree-
    // capped at top-m by |w| per subject ordinal to bound factorization fill-in.
    internal static async Task<PlaneCoo> ReadTokenPlaneAsync(
        NpgsqlDataSource ds, PlaneSpec spec,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var adj = new Dictionary<int, List<(int Col, double W)>>();
        await using var conn = await ds.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText =
                "SELECT subject_id, object_id, w FROM laplace.token_plane($1, $2, $3)";
            cmd.Parameters.AddWithValue(spec.Family);
            cmd.Parameters.AddWithValue(spec.Name);
            cmd.Parameters.AddWithValue((object?)spec.Arg ?? DBNull.Value);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var subj)) continue;
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[1]), out var obj)) continue;
                double w = rdr.GetDouble(2);
                if (w == 0.0) continue;
                foreach (int s in subj)
                {
                    if (!adj.TryGetValue(s, out var row)) adj[s] = row = new List<(int, double)>(8);
                    foreach (int o in obj) row.Add((o, w));
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            Console.WriteLine($"  (plane {spec} unavailable: {ex.SqlState} — skipped)");
            return PlaneCoo.Empty;
        }

        // Canonical order regardless of DB scan order: the cast law (identical
        // consensus + identical mold => identical cast) dies here otherwise —
        // |w| ties at the degree cap kept a scan-order subset, and dictionary
        // emission order perturbed downstream float summation.
        long kept = 0;
        foreach (var row in adj.Values)
        {
            row.Sort((a, b) =>
            {
                int c = Math.Abs(b.W).CompareTo(Math.Abs(a.W));
                return c != 0 ? c : a.Col.CompareTo(b.Col);
            });
            if (row.Count > degreeCap)
                row.RemoveRange(degreeCap, row.Count - degreeCap);
            kept += row.Count;
        }

        var rows = new int[kept]; var cols = new int[kept]; var vals = new double[kept];
        long at = 0;
        foreach (var r in adj.Keys.OrderBy(k => k))
            foreach (var (c, w) in adj[r])
            {
                rows[at] = r; cols[at] = c; vals[at] = w; at++;
            }
        return new PlaneCoo(rows, cols, vals);
    }

    // Per-plane scale normalization (max |w| → 1) so μ-weighted consensus planes
    // and frequency-weighted trajectory planes union into operators at comparable
    // magnitude. Relative structure within each plane is untouched.
    internal static PlaneCoo Normalize(PlaneCoo p)
    {
        double max = 0;
        foreach (var v in p.Vals) max = Math.Max(max, Math.Abs(v));
        if (max <= 0 || max == 1.0) return p;
        var vals = new double[p.Nnz];
        for (int i = 0; i < vals.Length; i++) vals[i] = p.Vals[i] / max;
        return p with { Vals = vals };
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

    // Generates E [vocab × dModel] row-major. anchors[i] is null or a 4D content
    // coordinate for vocab ordinal i. The seed must derive from the recipe (never
    // the clock) so identical consensus + identical mold ⇒ identical cast.
    internal static double[] BuildBasis(
        int vocab, int dModel, PlaneCoo leGraph, double[]?[] anchors, Hash128 seed,
        out BasisStats stats)
    {
        int k = Math.Min(Math.Min(dModel - 1, EnvInt("LAPLACE_FOUNDRY_BASIS_RANK", 256)),
                         Math.Max(2, vocab - 2));
        var y = GC.AllocateUninitializedArray<double>(checked(vocab * k), pinned: true);
        int rc;
        unsafe
        {
            fixed (int* pr = leGraph.Rows) fixed (int* pc = leGraph.Cols)
            fixed (double* pv = leGraph.Vals) fixed (double* py = y)
                rc = DynInterop.LaplacianEigenmapsFromSparseGraph(
                    pr, pc, pv, (nuint)leGraph.Nnz, (nuint)vocab, (nuint)k, py);
        }
        if (rc != 0)
            throw new InvalidOperationException(
                $"laplacian_eigenmaps_from_sparse_graph rc={rc} (vocab={vocab}, K={k}, nnz={leGraph.Nnz})");

        // GSO over the spectral columns (vectors-as-rows: transpose, orthonormalize, transpose back).
        var yt = new double[(long)k * vocab];
        for (int i = 0; i < vocab; i++)
            for (int d = 0; d < k; d++) yt[(long)d * vocab + i] = y[(long)i * k + d];
        int gsRc;
        unsafe { fixed (double* p = yt) gsRc = DynInterop.GramSchmidtOrthonormalize(p, (nuint)k, (nuint)vocab); }
        if (gsRc == 0)
            for (int i = 0; i < vocab; i++)
                for (int d = 0; d < k; d++) y[(long)i * k + d] = yt[(long)d * vocab + i];

        int zeroSpectral = 0;
        for (int i = 0; i < vocab; i++)
        {
            double n2 = 0;
            for (int d = 0; d < k; d++) { double v = y[(long)i * k + d]; n2 += v * v; }
            if (n2 < 1e-24) zeroSpectral++;
        }

        // Procrustes-anchor the first 4 spectral dims to token content coordinates,
        // rescaled so the anchored block keeps the spectral block's magnitude.
        double resid = double.NaN;
        var fitIdx = new List<int>();
        for (int i = 0; i < vocab; i++) if (anchors[i] is not null) fitIdx.Add(i);
        var e = new double[(long)vocab * dModel];
        if (fitIdx.Count >= 6 && k >= 4)
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
                    for (int i = 0; i < vocab; i++)
                        for (int d = 0; d < 4; d++)
                            y[(long)i * k + d] = a4[(long)i * 4 + d] * scale;
                }
                finally { DynInterop.ProcrustesFree(t); }
            }
        }

        for (int i = 0; i < vocab; i++)
            Array.Copy(y, (long)i * k, e, (long)i * dModel, k);

        // Deterministic capacity dims: seeded Gaussian columns at spectral magnitude.
        double capScale = 0.5 / Math.Sqrt(vocab);
        for (int d = k; d < dModel - 1; d++)
        {
            ulong s = SplitMix(unchecked((ulong)seed.Hi) ^ (ulong)d);
            for (int i = 0; i < vocab; i++)
                e[(long)i * dModel + d] = Gaussian(ref s) * capScale;
        }

        // Row-normalize the content dims; the bias channel sits outside the norm.
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

        stats = new BasisStats(k, zeroSpectral, resid);
        return e;
    }

    // M = Eᵀ·A·E for a sparse signed operator A (per-token weights are all ones, so
    // the kernel's "scale by √consensus" is the identity and binary gram == Eᵀ A E).
    internal static double[] ProjectOperator(double[] e, int vocab, int dModel, PlaneCoo coo)
    {
        var ones = new double[vocab];
        Array.Fill(ones, 1.0);
        var unary = new double[(long)dModel * dModel];
        var binary = new double[(long)dModel * dModel];
        int rc;
        unsafe
        {
            fixed (double* pe = e) fixed (double* po = ones)
            fixed (int* pr = coo.Rows) fixed (int* pc = coo.Cols) fixed (double* pv = coo.Vals)
            fixed (double* pu = unary) fixed (double* pb = binary)
                rc = SynInterop.ComputeSubstrateGram(
                    pe, po, (nuint)vocab, (nuint)dModel,
                    pr, pc, pv, (nuint)coo.Nnz, pu, pb);
        }
        if (rc != 0)
            throw new InvalidOperationException($"compute_substrate_gram rc={rc} (nnz={coo.Nnz})");
        return binary;
    }

    internal sealed record Factors(float[] Left, float[] Right, int Rank, int Dim, double SampleResidual);

    // Factor M ≈ Leftᵀ·Right with Left/Right [rankCap × d] rows = √Sᵣ·uᵣᵀ / √Sᵣ·vᵣᵀ.
    // transpose=true factors Mᵀ instead (for operators whose composed orientation
    // is Wouter·Winner, e.g. Wo·Wv and Wdown·Wup). The native kernel computes the
    // FULL SVD (its kmax is buffer capacity, required ≥ min(m,n)) and truncates by
    // rel_err_tol; the mold's rank cap is applied here, keeping the strongest modes.
    internal static Factors Factor(double[] m, int d, int rankCap, double relTol, bool transpose)
    {
        var a = new float[(long)d * d];
        for (int i = 0; i < d; i++)
            for (int j = 0; j < d; j++)
                a[(long)i * d + j] = (float)(transpose ? m[(long)j * d + i] : m[(long)i * d + j]);

        var u = new float[(long)d * d];
        var s = new float[d];
        var vt = new float[(long)d * d];
        nuint outRank = 0;
        int rc;
        unsafe
        {
            fixed (float* pa = a) fixed (float* pu = u) fixed (float* ps = s) fixed (float* pvt = vt)
                rc = SynInterop.TensorSvdTruncate(pa, (nuint)d, (nuint)d, relTol, &outRank, pu, ps, pvt, (nuint)d);
        }
        if (rc != 0) throw new InvalidOperationException($"tensor_svd_truncate rc={rc} (d={d})");
        int k = Math.Min((int)outRank, rankCap);

        var left = new float[(long)k * d];
        var right = new float[(long)k * d];
        for (int r = 0; r < k; r++)
        {
            float sq = MathF.Sqrt(Math.Max(0f, s[r]));
            for (int j = 0; j < d; j++)
            {
                left[(long)r * d + j] = sq * u[(long)j * d + r];
                right[(long)r * d + j] = sq * vt[(long)r * d + j];
            }
        }

        // Sampled recomposition residual — a layout/orientation tripwire, not a fidelity
        // metric. Large values mean the factor wiring is wrong, not that consensus is.
        double num = 0, den = 0;
        ulong rng = SplitMix(0x9E3779B97F4A7C15UL ^ (ulong)d);
        for (int t = 0; t < 512; t++)
        {
            int i = (int)(Next(ref rng) % (ulong)d);
            int j = (int)(Next(ref rng) % (ulong)d);
            double approx = 0;
            for (int r = 0; r < k; r++) approx += (double)left[(long)r * d + i] * right[(long)r * d + j];
            double exact = a[(long)i * d + j];
            num += (exact - approx) * (exact - approx);
            den += exact * exact;
        }
        double resid = den > 0 ? Math.Sqrt(num / den) : 0.0;
        return new Factors(left, right, k, d, resid);
    }

    // ── mold tensor fills ─────────────────────────────────────────────────────

    internal static void FillRows(float[] vals, int rows, int cols, Factors f, double scale)
    {
        int k = Math.Min(f.Rank, rows);
        for (int r = 0; r < k; r++)
            for (int j = 0; j < cols && j < f.Dim; j++)
                vals[(long)r * cols + j] = (float)(scale * f.Left[(long)r * f.Dim + j]);
    }

    internal static void FillRowsRight(float[] vals, int rows, int cols, Factors f, double scale)
    {
        int k = Math.Min(f.Rank, rows);
        for (int r = 0; r < k; r++)
            for (int j = 0; j < cols && j < f.Dim; j++)
                vals[(long)r * cols + j] = (float)(scale * f.Right[(long)r * f.Dim + j]);
    }

    internal static void FillCols(float[] vals, int rows, int cols, Factors f, double scale)
    {
        int k = Math.Min(f.Rank, cols);
        for (int r = 0; r < k; r++)
            for (int i = 0; i < rows && i < f.Dim; i++)
                vals[(long)i * cols + r] = (float)(scale * f.Left[(long)r * f.Dim + i]);
    }

    internal static void FillGate(float[] vals, int rows, int cols, double gateCol)
    {
        for (int r = 0; r < rows; r++)
            vals[(long)r * cols + (cols - 1)] = (float)gateCol;
    }

    internal static double Silu(double z) => z / (1.0 + Math.Exp(-z));

    // ── byte packers (GGUF tensor payloads) ───────────────────────────────────

    internal static byte[] ToBf16Bytes(float[] data)
    {
        var o = new byte[(long)data.Length * 2];
        for (long i = 0; i < data.LongLength; i++)
        {
            uint bits = BitConverter.SingleToUInt32Bits(data[i]);
            ushort bf = (ushort)(bits >> 16);
            o[i * 2] = (byte)bf;
            o[i * 2 + 1] = (byte)(bf >> 8);
        }
        return o;
    }

    internal static byte[] ToF32Bytes(float[] data)
    {
        var o = new byte[(long)data.Length * 4];
        Buffer.BlockCopy(data, 0, o, 0, o.Length);
        return o;
    }

    // ── deterministic PRNG (no clock, no shared Random) ───────────────────────

    private static ulong SplitMix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static ulong Next(ref ulong state)
    {
        state = SplitMix(state);
        return state;
    }

    private static double Gaussian(ref ulong state)
    {
        double u1 = (Next(ref state) >> 11) * (1.0 / 9007199254740992.0);
        double u2 = (Next(ref state) >> 11) * (1.0 / 9007199254740992.0);
        if (u1 < 1e-300) u1 = 1e-300;
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static unsafe Hash128 FromBytes(byte[] bts)
    {
        if (bts.Length < 16) return Hash128.Zero;
        fixed (byte* p = bts) return *(Hash128*)p;
    }
}
