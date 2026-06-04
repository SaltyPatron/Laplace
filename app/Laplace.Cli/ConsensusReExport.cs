using global::Npgsql;
using Laplace.Engine.Core;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;
using DynamicsInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Cli;

/// <summary>
/// Re-export = the ingestion run backward (ARCHITECTURE.md §8): take the target
/// recipe (the MOLD) and factor each CONSENSUS circuit back into the mold's
/// weight tensors at the recipe's rank, via SVD through the spectral token
/// basis — QK→q/k, OV→v/o, FFN→up/down. The output is the consensus of all
/// ingested witnesses in the chosen shape — never a reconstruction of any one
/// model, never bit-perfect.
///
/// Mechanism per arena (kind = ATTENDS / OV_RELATES / COMPLETES_TO):
///  1. Read the arena's CONSENSUS rows (source AND layer/head out of identity —
///     the frame-invariant relation web) and invert the signed score map:
///     the consensus rating's expected win-rate E against the neutral 1500
///     line inverts ½(1+tanh(m/M)) to a SIGNED strength m̂ = atanh(2E−1) in
///     per-arena M units (absolute scale is the mold's, not the substrate's).
///  2. Project the sparse token×token consensus operator into the spectral
///     token basis (eigenmaps over the |m̂| union graph — substrate-derived):
///     B = Eᵀ·M·E  [basisDim × basisDim]  (compute_substrate_gram).
///  3. Thin-SVD B (tensor_svd_truncate — the EXPORT-ONLY factoring kernel) and
///     hand each mold slot its singular-component block: per (layer, kv-group)
///     the q/k (v/o, up/down) factor rows are √S·U / √S·V columns cycled into
///     d_model — so E_mold·Wqᵀ·Wk·E_moldᵀ reproduces that block's share of the
///     consensus operator. Component-block assignment per mold slot is the
///     recipe-rank policy (the mold's choice — the consensus itself is
///     frame-invariant and carries no layers).
///
/// Nonlinearities are runtime: norms fill at 1.0 (recipe scaling), the SwiGLU
/// gate fills with the up factor (gating is data-dependent, never attested —
/// a recipe policy, §10 calibration).
/// </summary>
internal static class ConsensusReExport
{
    internal sealed record ArenaEdges(int[] Rows, int[] Cols, double[] Vals)
    {
        public int Count => Rows.Length;
    }

    internal sealed record ArenaFactor(float[] U, float[] S, float[] Vt, int Rank, int BasisDim);

    /// <summary>Invert the signed score map through the accumulated rating:
    /// expected win-rate E of the consensus rating vs the neutral 1500 line,
    /// then m̂ = atanh(2E−1) — the signed strength in per-arena M units.
    /// Monotone, signed (refuted ⇒ negative), exact inverse of
    /// score = ½(1+tanh(m/M)) read back through the neutral-baseline match.</summary>
    internal static double SignedStrength(long ratingFp1e9)
    {
        double r = ratingFp1e9 / 1e9;                       // Glicko scale, neutral 1500
        double e = 1.0 / (1.0 + Math.Pow(10.0, -(r - 1500.0) / 400.0));
        double x = Math.Clamp(2.0 * e - 1.0, -1.0 + 1e-12, 1.0 - 1e-12);
        return Math.Atanh(x);
    }

    /// <summary>Read one arena's CONSENSUS (one signed row per relation; source +
    /// layer/head already out of identity) as a sparse token×token operator.</summary>
    internal static async Task<ArenaEdges> ReadArenaAsync(
        NpgsqlDataSource ds, Hash128 kindId,
        IReadOnlyDictionary<Hash128, int> entityToToken, int vocab)
    {
        var rows = new List<int>();
        var cols = new List<int>();
        var vals = new List<double>();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT subject_id, object_id, rating FROM laplace.consensus
            WHERE kind_id = $1 AND object_id IS NOT NULL
            """;
        cmd.Parameters.AddWithValue(kindId.ToBytes());
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var subj = FromBytes((byte[])rdr[0]);
            var obj  = FromBytes((byte[])rdr[1]);
            if (!entityToToken.TryGetValue(subj, out int i) || i >= vocab) continue;
            if (!entityToToken.TryGetValue(obj,  out int j) || j >= vocab) continue;
            double m = SignedStrength(rdr.GetInt64(2));
            if (m == 0.0) continue;                         // zero = the only non-event
            rows.Add(i); cols.Add(j); vals.Add(m);
        }
        return new ArenaEdges(rows.ToArray(), cols.ToArray(), vals.ToArray());
    }

    /// <summary>Spectral token basis: Laplacian eigenmaps over the UNION of all
    /// arenas' |m̂| edges (signed consensus folds in as |m̂| — dissent is
    /// structure too, never discarded). Returns [vocab × basisDim] row-major,
    /// or null when the graph is empty/degenerate.</summary>
    internal static double[]? BuildBasis(int vocab, int basisDim, params ArenaEdges[] arenas)
    {
        long nnz = arenas.Sum(a => (long)a.Count);
        if (nnz == 0) return null;
        var rows = new int[nnz]; var cols = new int[nnz]; var vals = new double[nnz];
        long w = 0;
        foreach (var a in arenas)
            for (int e = 0; e < a.Count; e++)
            { rows[w] = a.Rows[e]; cols[w] = a.Cols[e]; vals[w] = a.Vals[e]; w++; }

        var basis = new double[(long)vocab * basisDim];
        int rc;
        unsafe
        {
            fixed (int* rp = rows) fixed (int* cp = cols) fixed (double* vp = vals)
            fixed (double* bp = basis)
                rc = DynamicsInterop.LaplacianEigenmapsFromSparseGraph(
                    rp, cp, vp, (nuint)nnz, (nuint)vocab, (nuint)basisDim, bp);
        }
        return rc == 0 ? basis : null;
    }

    /// <summary>Factor one arena: B = Eᵀ·M·E (basis gram of the signed sparse
    /// operator), then full thin SVD via the export-only kernel.</summary>
    internal static ArenaFactor? FactorArena(double[] basis, int vocab, int basisDim, ArenaEdges arena)
    {
        if (arena.Count == 0) return null;
        var zero = new double[vocab];                       // unary gram unused here
        var ugram = new double[(long)basisDim * basisDim];
        var bgram = new double[(long)basisDim * basisDim];
        int rc;
        unsafe
        {
            fixed (double* bp = basis) fixed (double* zp = zero)
            fixed (int* rp = arena.Rows) fixed (int* cp = arena.Cols) fixed (double* vp = arena.Vals)
            fixed (double* ug = ugram) fixed (double* bg = bgram)
                rc = SynthInterop.ComputeSubstrateGram(
                    bp, zp, (nuint)vocab, (nuint)basisDim,
                    rp, cp, vp, (nuint)arena.Count, ug, bg);
        }
        if (rc != 0) return null;

        var A  = new float[(long)basisDim * basisDim];
        for (long i = 0; i < A.LongLength; i++) A[i] = (float)bgram[i];
        var U  = new float[(long)basisDim * basisDim];
        var S  = new float[basisDim];
        var Vt = new float[(long)basisDim * basisDim];
        nuint rank;
        unsafe
        {
            fixed (float* ap = A) fixed (float* up = U) fixed (float* sp = S) fixed (float* vtp = Vt)
                rc = SynthInterop.TensorSvdTruncate(
                    ap, (nuint)basisDim, (nuint)basisDim, 0.0, &rank, up, sp, vtp, (nuint)basisDim);
        }
        if (rc != 0 || rank == 0) return null;
        return new ArenaFactor(U, S, Vt, (int)rank, basisDim);
    }

    /// <summary>Fill <paramref name="rowsOut"/> factor rows of a mold tensor
    /// [rowsOut × dModel] from √S-scaled singular columns, components assigned
    /// from <paramref name="compOffset"/> (cycled mod rank — the recipe-rank
    /// block policy), each column cycled into d_model (mod basisDim, normalized
    /// by the cycle count so the cycled inner product averages).
    /// <paramref name="useVt"/> selects the V side (dec) instead of U (enc).</summary>
    internal static void FillFactorRows(
        float[] dst, long dstOffset, int rowsOut, int dModel,
        ArenaFactor f, int compOffset, bool useVt)
    {
        int b = f.BasisDim;
        int cycles = (dModel + b - 1) / b;
        float norm = 1.0f / cycles;
        for (int j = 0; j < rowsOut; j++)
        {
            int c = (compOffset + j) % f.Rank;
            float s = MathF.Sqrt(MathF.Abs(f.S[c]));
            long ro = dstOffset + (long)j * dModel;
            for (int d = 0; d < dModel; d++)
            {
                int dp = d % b;
                // U: [b × b] row-major, column c → U[dp*b + c]. Vt: row c → Vt[c*b + dp].
                float comp = useVt ? f.Vt[(long)c * b + dp] : f.U[(long)dp * b + c];
                dst[ro + d] = s * comp * norm;
            }
        }
    }

    /// <summary>The mold's embedding: the spectral token basis cycled into
    /// d_model — the substrate-derived token placement, NOT any source model's
    /// embedding (the input dissolved; this is the consensus frame).</summary>
    internal static void FillEmbedding(float[] dst, double[] basis, int vocab, int basisDim, int dModel)
    {
        int cycles = (dModel + basisDim - 1) / basisDim;
        float norm = 1.0f / cycles;
        for (int t = 0; t < vocab; t++)
        {
            long bo = (long)t * basisDim, ro = (long)t * dModel;
            for (int d = 0; d < dModel; d++)
                dst[ro + d] = (float)basis[bo + (d % basisDim)] * norm;
        }
    }

    /// <summary>f32 → BF16 truncation bytes (upper 16 bits of each f32).</summary>
    internal static byte[] ToBf16Bytes(float[] data)
    {
        var o = new byte[(long)data.Length * 2];
        for (long i = 0; i < data.LongLength; i++)
        {
            uint bits = BitConverter.SingleToUInt32Bits(data[i]);
            ushort bf = (ushort)(bits >> 16);
            o[i * 2]     = (byte)bf;
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

    private static unsafe Hash128 FromBytes(byte[] bts)
    {
        if (bts.Length < 16) return Hash128.Zero;
        fixed (byte* p = bts) return *(Hash128*)p;
    }
}
