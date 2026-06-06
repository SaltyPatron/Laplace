using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Model;

namespace Laplace.Cli;

/// <summary>
/// Re-export = the ingestion run backward (ARCHITECTURE.md §8). The substrate's
/// model arenas ARE the logical tables (the ten tensor-role kinds), so the mold
/// fills DIRECTLY from them: per tensor slot, look up the slot's role arena and
/// pour each relation's consensus strength into its cell. Positions all receive
/// the relation's one consensus value — a deeper mold receives the same
/// consensus at every layer (consensus-of-all witnesses in the chosen shape,
/// never a reconstruction of any one model, never bit-perfect).
///
/// Mechanism per arena (kind = EMBEDS … OUTPUT_PROJECTS):
///  1. Read the arena's CONSENSUS rows (source and position out of identity)
///     and invert the signed score map: the consensus rating's expected
///     win-rate E against the neutral 1500 line inverts ½(1+tanh(m/M)) to a
///     SIGNED strength m̂ = atanh(2E−1) in per-arena M units.
///  2. Resolve the relation's endpoints back to tensor indices — the TRANSFORM
///     run backward: token entities through the tokenizer's key-mapping (every
///     vocab slot sharing the content entity receives the value), axis
///     entities through <see cref="SourceEntityIdConventions.ModelAxisEntity"/>.
///  3. Scale: the absolute per-role M is the MOLD's, not the substrate's —
///     measured from the mold dir's own reference tensors when present
///     (the recipe names them), else 1.0 (relative structure only).
///
/// Nonlinearities are runtime: NORM_SCALES pours per-channel consensus into the
/// norm vectors (fallback 1.0); the SwiGLU gating itself is never attested.
/// Rank/shape retargeting (mold ≠ source schema shape) is the export-only SVD
/// path — not built; a mismatched mold fails loud, never silently mis-fills.
/// </summary>
internal static class ConsensusReExport
{
    /// <summary>One tensor-role arena poured into its tensor layout
    /// [rows × cols] (HF row-major, same orientation the ETL read).</summary>
    internal sealed record TableArena(float[] Cells, int Rows, int Cols, long Relations);

    // Witness weight for a model arena = kind_rank(TensorCalculation 0.27) ×
    // source_trust(AiModelProbe 0.50); → opponent φ via the same shape the
    // factory uses (WitnessPhi). Constant across every model cell.
    // MUST equal ModelTableETL.ModelWeight exactly (the calibrated inverse needs
    // the same φ the ETL folded with) — both derive from the registry.
    private static readonly double ModelWeight =
        Laplace.Decomposers.Abstractions.RelationTypeRegistry.Resolve("EMBEDS").Rank
        * Laplace.Decomposers.Abstractions.SourceTrust.AiModelProbe;
    private static long PhiFp() => (long)((350.0 + (30.0 - 350.0) * ModelWeight) * 1e9);

    /// <summary>
    /// CALIBRATED inverse μ → signed strength (w/M), built from the substrate's
    /// OWN kernel: the encode is score = ½(1+tanh(w/M)) → n Glicko games vs the
    /// neutral 1500 line at the model opponent φ → consensus μ. That forward map
    /// (deterministic given n, φ, prior) is sampled over a w/M grid via
    /// laplace_glicko2_accumulate_games and inverted by monotone interpolation —
    /// the EXACT inverse (verified rel-L2 0.0000 on EMBEDS by
    /// scripts/validate-arena-reconstruction.py). The earlier atanh(2E−1)
    /// approximation ignored the Glicko map and compressed magnitudes ~7×.
    /// Cached per witness-count n (n=1 global, n=layers interior, n=norm-slots).
    /// </summary>
    internal sealed class CalibratedInverse
    {
        private readonly Dictionary<long, (double[] Mu, double[] Wom)> _byN = new();
        private readonly NpgsqlConnection _conn;
        public CalibratedInverse(NpgsqlConnection conn) => _conn = conn;

        private (double[] Mu, double[] Wom) Map(long n)
        {
            if (_byN.TryGetValue(n, out var m)) return m;
            const int G = 4001; const double LO = -6.0, HI = 6.0;
            var wom = new double[G]; var sumFp = new long[G];
            for (int i = 0; i < G; i++)
            {
                double w = LO + (HI - LO) * i / (G - 1);
                wom[i] = w;
                double score = 0.5 * (1.0 + Math.Tanh(w));
                sumFp[i] = (long)Math.Round(score * 1e9) * n;   // n equal games summing here
            }
            var mu = new double[G];
            using (var cmd = _conn.CreateCommand())
            {
                // one set-based call: the grid through the real n-game kernel
                cmd.CommandText =
                    "SELECT g.i, (laplace.laplace_glicko2_accumulate_games("
                    + "1500000000000,350000000000,60000000,1500000000000,$1,$2,s.sum,500000000)).rating "
                    + "FROM unnest($3::bigint[]) WITH ORDINALITY AS s(sum,i) "
                    + "CROSS JOIN LATERAL (SELECT s.i AS i) g ORDER BY g.i";
                cmd.Parameters.AddWithValue(PhiFp());
                cmd.Parameters.AddWithValue(n);
                cmd.Parameters.AddWithValue(sumFp);
                using var rdr = cmd.ExecuteReader();
                int k = 0;
                while (rdr.Read()) mu[k++] = rdr.GetInt64(1) / 1e9;
            }
            // sort by μ for interpolation (the map is monotone increasing in w)
            var order = Enumerable.Range(0, G).OrderBy(i => mu[i]).ToArray();
            var muS = new double[G]; var womS = new double[G];
            for (int i = 0; i < G; i++) { muS[i] = mu[order[i]]; womS[i] = wom[order[i]]; }
            var pair = (muS, womS);
            _byN[n] = pair;
            return pair;
        }

        /// <summary>μ (fixed-point ×1e9) + game count n → signed strength w/M.</summary>
        public double Wom(long ratingFp1e9, long n)
        {
            var (mu, wom) = Map(n <= 0 ? 1 : n);
            double r = ratingFp1e9 / 1e9;
            // binary search + linear interp (mu ascending)
            int lo = 0, hi = mu.Length - 1;
            if (r <= mu[0]) return wom[0];
            if (r >= mu[hi]) return wom[hi];
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (mu[mid] <= r) lo = mid; else hi = mid; }
            double t = (r - mu[lo]) / (mu[hi] - mu[lo] + 1e-30);
            return wom[lo] + t * (wom[hi] - wom[lo]);
        }
    }

    /// <summary>
    /// Read one tensor-role arena's CONSENSUS into the role's tensor layout.
    /// <paramref name="inIndex"/>/<paramref name="outIndex"/> resolve an
    /// endpoint entity to its tensor indices (a token content entity may own
    /// several vocab slots — every slot receives the value; the surrogate-key
    /// resolution run backward). <paramref name="rowsAreOut"/> mirrors the
    /// ETL's HF [rows × cols] = [out × in] orientation. <paramref name="m"/>
    /// is the mold's per-role scale.
    /// </summary>
    internal static async Task<TableArena> ReadTableArenaAsync(
        NpgsqlDataSource ds, Hash128 typeId, int rows, int cols, bool rowsAreOut,
        Func<Hash128, IReadOnlyList<int>?> inIndex,
        Func<Hash128, IReadOnlyList<int>?> outIndex,
        double m)
    {
        var cells = new float[(long)rows * cols];
        long relations = 0;
        await using var conn = await ds.OpenConnectionAsync();
        var inverse = new CalibratedInverse(conn);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT subject_id, object_id, rating, witness_count FROM laplace.consensus
            WHERE kind_id = $1 AND object_id IS NOT NULL
            """;
        cmd.Parameters.AddWithValue(typeId.ToBytes());
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var subjIdx = inIndex(FromBytes((byte[])rdr[0]));
            var objIdx  = outIndex(FromBytes((byte[])rdr[1]));
            if (subjIdx is null || objIdx is null) continue;   // another source's axes/tokens
            // calibrated inverse μ→(w/M), then ×M = reconstructed weight
            float v = (float)(inverse.Wom(rdr.GetInt64(2), rdr.GetInt64(3)) * m);
            if (v == 0f) continue;                             // zero = the only non-event
            relations++;
            foreach (int i in subjIdx)
                foreach (int o in objIdx)
                {
                    int row = rowsAreOut ? o : i;
                    int col = rowsAreOut ? i : o;
                    if ((uint)row < (uint)rows && (uint)col < (uint)cols)
                        cells[(long)row * cols + col] = v;
                }
        }
        return new TableArena(cells, rows, cols, relations);
    }

    /// <summary>Per-channel NORM_SCALES consensus → a norm vector (unary arena:
    /// object NULL; subject = the channel). Fallback 1.0 for unwitnessed
    /// channels (runtime scaling is the recipe's, never invented).</summary>
    internal static async Task<float[]> ReadNormVectorAsync(
        NpgsqlDataSource ds, Hash128 typeId, int dModel,
        Func<Hash128, IReadOnlyList<int>?> channelIndex, double m)
    {
        var vec = new float[dModel];
        Array.Fill(vec, 1.0f);
        await using var conn = await ds.OpenConnectionAsync();
        var inverse = new CalibratedInverse(conn);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT subject_id, rating, witness_count FROM laplace.consensus
            WHERE kind_id = $1 AND object_id IS NULL
            """;
        cmd.Parameters.AddWithValue(typeId.ToBytes());
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var idx = channelIndex(FromBytes((byte[])rdr[0]));
            if (idx is null) continue;
            float v = (float)(inverse.Wom(rdr.GetInt64(1), rdr.GetInt64(2)) * m);
            foreach (int c in idx)
                if ((uint)c < (uint)dModel) vec[c] = v;
        }
        return vec;
    }

    /// <summary>
    /// The mold's per-role scale M: measured from the mold dir's own reference
    /// tensors (pooled RMS over the role's instances — the same measurement the
    /// ETL calibrated with), when the recipe's dir carries them. The substrate
    /// stores adjudicated strengths in M units; the absolute scale is the
    /// MOLD's choice. Returns 1.0 (relative structure only) when no reference
    /// tensors are present.
    /// </summary>
    internal static double MoldArenaScale(
        Dictionary<string, SafetensorsContainerParser.TensorReference>? refMap,
        IEnumerable<string> instanceNames)
    {
        if (refMap is null) return 1.0;
        double sumsq = 0; long n = 0;
        foreach (var name in instanceNames)
        {
            if (!refMap.TryGetValue(name, out var tref)) continue;
            long elems = (long)tref.Shape[0] * (tref.Shape.Length > 1 ? tref.Shape[1] : 1);
            var w = WeightTensorETL.LoadTensorF32(refMap, name, elems);
            for (long i = 0; i < w.LongLength; i++) { double v = w[i]; sumsq += v * v; }
            n += elems;
        }
        return n > 0 && sumsq > 0 ? Math.Sqrt(sumsq / n) : 1.0;
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
