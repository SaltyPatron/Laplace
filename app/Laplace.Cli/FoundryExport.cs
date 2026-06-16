using global::Npgsql;
using NpgsqlTypes;
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
    // SQL surface (laplace.relation_plane) is the single definition both this reader
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
    internal static async Task<PlaneCoo> ReadRelationPlaneAsync(
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
                "SELECT subject_id, object_id, w FROM laplace.relation_plane($1, $2, $3)";
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

    // Vocab-bounded consensus plane: the native read returns ONLY the vocab×vocab
    // edges of the given relation types, degree-capped per subject server-side via
    // the (subject_id, type_id) index — no full-type scan, no millions of rows
    // streamed to the client. The foundry passes its vocab entity set + the relation
    // names for one operator role and only maps the returned edges to ordinals. ONE
    // bounded query per role replaces N full-type reads + in-client filtering.
    internal static async Task<PlaneCoo> ReadConsensusPlaneAsync(
        NpgsqlDataSource ds, string[] relNames,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        var adj = new Dictionary<int, List<(int Col, double W)>>();
        await using var conn = await ds.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText =
                "SELECT subject_id, object_id, w FROM laplace.entity_relation_plane($1, $2, $3)";
            cmd.Parameters.Add(new NpgsqlParameter
                { Value = vocab,    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter
                { Value = relNames, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            cmd.Parameters.AddWithValue(degreeCap);
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
            Console.WriteLine($"  (consensus plane [{string.Join(",", relNames)}] unavailable: {ex.SqlState} — skipped)");
            return PlaneCoo.Empty;
        }

        long kept = 0;
        foreach (var row in adj.Values)
        {
            row.Sort((a, b) =>
            {
                int c = Math.Abs(b.W).CompareTo(Math.Abs(a.W));
                return c != 0 ? c : a.Col.CompareTo(b.Col);
            });
            if (row.Count > degreeCap) row.RemoveRange(degreeCap, row.Count - degreeCap);
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

    // WITHIN-LAYER plane read: consensus_layer_plane keeps the read inside one rank band
    // (the ranks ARE the layers), known relations only, and CONTENT objects only (object in
    // vocab) — so app/structural annotations (HAS_POS etc.) and unnamed types never enter the
    // tensor. weight w = eff_mu (the adjudicated rating). This is the export's correct source,
    // replacing the flat entity_relation_plane (which mixed metadata/structural with meaning).
    internal static async Task<PlaneCoo> ReadLayerPlaneAsync(
        NpgsqlDataSource ds, double rankLo, double rankHi,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        var adj = new Dictionary<int, List<(int Col, double W)>>();
        await using var conn = await ds.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText =
                "SELECT subject_id, object_id, w, layer_rank FROM laplace.consensus_layer_plane($1, $2, $3, $4)";
            cmd.Parameters.Add(new NpgsqlParameter
                { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.AddWithValue(rankLo);
            cmd.Parameters.AddWithValue(rankHi);
            cmd.Parameters.AddWithValue(degreeCap);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var subj)) continue;
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[1]), out var obj)) continue;
                // WEIGHT = relation_rank × eff_mu (the layering law: rank is the relation's AUTHORITY,
                // eff_mu its consensus significance). Weighting by eff_mu alone let a generic low-rank
                // high-witness edge (king→person/food) outrank the specific high-rank one (king IS_A
                // monarch, taxonomic 0.82). rank×eff_mu lifts the authoritative relation.
                double w = rdr.GetDouble(2) * rdr.GetDouble(3);
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
            Console.WriteLine($"  (layer plane [{rankLo:F2}-{rankHi:F2}] unavailable: {ex.SqlState} — skipped)");
            return PlaneCoo.Empty;
        }

        long kept = 0;
        foreach (var row in adj.Values)
        {
            row.Sort((a, b) =>
            {
                int c = Math.Abs(b.W).CompareTo(Math.Abs(a.W));
                return c != 0 ? c : a.Col.CompareTo(b.Col);
            });
            if (row.Count > degreeCap) row.RemoveRange(degreeCap, row.Count - degreeCap);
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

    // PER-TYPE planes — the proper attestation-tensor cast (ranks=layers, types-in-rank=heads,
    // eff_mu=weights). laplace.consensus_type_plane returns the vocab×vocab rated adjacency
    // grouped BY relation TYPE (one row group per type, the type's rank = its layer). Each type
    // becomes its OWN attention head; ranks group heads into layers. This is the separate
    // per-head/per-layer transcription, NOT 4 coarse rank bands collapsed into one operator.
    internal sealed record TypePlane(Hash128 TypeId, double Rank, PlaneCoo Plane);

    internal static async Task<List<TypePlane>> ReadTypePlanesAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        // type_id -> (rank, subject ordinal -> [(object ordinal, w)])
        var byType = new Dictionary<Hash128, (double Rank, Dictionary<int, List<(int Col, double W)>> Adj)>();
        await using var conn = await ds.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText =
                "SELECT subject_id, object_id, w, type_id, layer_rank FROM laplace.consensus_type_plane($1, $2)";
            cmd.Parameters.Add(new NpgsqlParameter
                { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.AddWithValue(degreeCap);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var subj)) continue;
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[1]), out var obj)) continue;
                double w = rdr.GetDouble(2);
                if (w == 0.0) continue;
                var tid = FromBytes((byte[])rdr[3]);
                double rank = rdr.GetDouble(4);
                if (!byType.TryGetValue(tid, out var entry))
                    byType[tid] = entry = (rank, new Dictionary<int, List<(int, double)>>());
                foreach (int s in subj)
                {
                    if (!entry.Adj.TryGetValue(s, out var row)) entry.Adj[s] = row = new List<(int, double)>(8);
                    foreach (int o in obj) row.Add((o, w));
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            Console.WriteLine($"  (consensus_type_plane unavailable: {ex.SqlState} — skipped)");
            return new List<TypePlane>();
        }

        var result = new List<TypePlane>(byType.Count);
        foreach (var (tid, entry) in byType)
            result.Add(new TypePlane(tid, entry.Rank, CooFromAdj(entry.Adj, degreeCap)));
        // strongest rank first → deterministic head/layer assignment in the cast.
        result.Sort((a, b) => b.Rank.CompareTo(a.Rank));
        return result;
    }

    // FAITHFUL adjacency read (the generative mold's source): ONE set-based call to
    // laplace.consensus_adjacency over the whole vocab. The weight is already the
    // rank-weighted rating Σ relation_rank·eff_mu — the rank looked up PER EDGE from the
    // banked law server-side, so the caller carries NO band edges and NO hand-typed rank
    // weights. Maps entity ids → token ordinals via tokenSlots; an entity that resolves to
    // several token ids fans the same weight to each pairing (the content addressing is the
    // identity). Degree already capped server-side; we re-cap in canonical order so the cast
    // is byte-stable regardless of DB scan order.
    internal static async Task<PlaneCoo> ReadAdjacencyAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        var adj = new Dictionary<int, List<(int Col, double W)>>();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 600;
        cmd.CommandText = "SELECT subject_id, object_id, w FROM laplace.consensus_adjacency($1, $2)";
        cmd.Parameters.Add(new NpgsqlParameter
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.AddWithValue(degreeCap);
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
        return CooFromAdj(adj, degreeCap);
    }

    // METRIC PLANE: a transformer head is a bilinear score between two token entities, and
    // a head TRANSCRIBES a NAMED substrate metric between their trajectories rather than
    // learning a pattern. laplace.metric_edges computes, per token, its k nearest neighbours
    // under the chosen metric (laplace_frechet_4d / hausdorff_4d / angular_distance_4d) over
    // the realized trajectory — a DISTANCE. Map distance→affinity (exp(−d): near = high) so
    // the operator scores near pairs up in the attention softmax, then project+factor it into
    // q/k exactly like a consensus plane: q·k reproduces the metric. metric ∈ frechet|hausdorff|angular.
    internal static async Task<PlaneCoo> ReadMetricEdgesAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots,
        string metric, int k, int probe, int degreeCap)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var key in tokenSlots.Keys) vocab[vi++] = key.ToBytes();

        var adj = new Dictionary<int, List<(int Col, double W)>>();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;   // pays the bounded angular-KNN + metric refine once
        cmd.CommandText = "SELECT subject_id, object_id, w FROM laplace.metric_edges($1, $2, $3, $4)";
        cmd.Parameters.Add(new NpgsqlParameter
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.AddWithValue(metric);
        cmd.Parameters.AddWithValue(k);
        cmd.Parameters.AddWithValue(probe);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var subj)) continue;
            if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[1]), out var obj)) continue;
            double dist = rdr.GetDouble(2);
            double w = Math.Exp(-dist);   // distance → affinity (near = high score)
            if (w == 0.0) continue;
            foreach (int s in subj)
            {
                if (!adj.TryGetValue(s, out var row)) adj[s] = row = new List<(int, double)>(8);
                foreach (int o in obj) row.Add((o, w));
            }
        }
        return CooFromAdj(adj, degreeCap);
    }

    // Verify the metric HEAD: after project+factor, q·k must reproduce the metric's
    // neighbour structure. For sampled subjects, rank all tokens by q·k (q_i = Left·E_i,
    // k_j = Right·E_j over the basis E) and measure recall of the metric's own top-k
    // neighbours. Proves "layer/head = metric(A,B)" with a number — a transcription
    // tripwire, not a tuned metric. K = E·Rightᵀ is precomputed once over the vocab.
    internal static void ReportMetricHeadFidelity(
        double[] e, int vocab, int dModel, PlaneCoo plane, Factors f, string metric)
    {
        var nbr = new Dictionary<int, List<int>>();
        for (long t = 0; t < plane.Nnz; t++)
        {
            int s = plane.Rows[t], o = plane.Cols[t];
            if (!nbr.TryGetValue(s, out var l)) nbr[s] = l = new List<int>();
            l.Add(o);
        }
        if (nbr.Count == 0 || f.Rank == 0) { Console.WriteLine("  metric-head fidelity: no edges to check"); return; }
        int rank = f.Rank;
        // K[o,r] = Right_r · E_o for every token, once.
        var K = new double[(long)vocab * rank];
        for (int o = 0; o < vocab; o++)
            for (int r = 0; r < rank; r++)
            {
                double a = 0;
                for (int j = 0; j < dModel && j < f.Dim; j++) a += f.Right[(long)r * f.Dim + j] * e[(long)o * dModel + j];
                K[(long)o * rank + r] = a;
            }
        var samples = nbr.Keys.OrderBy(x => x).Take(8).ToList();
        double recallSum = 0, directSum = 0, noiseSum = 0; int cnt = 0;
        foreach (int s in samples)
        {
            var want = nbr[s]; int kk = want.Count; if (kk == 0) continue;
            var qs = new double[rank];
            for (int r = 0; r < rank; r++)
            {
                double a = 0;
                for (int j = 0; j < dModel && j < f.Dim; j++) a += f.Left[(long)r * f.Dim + j] * e[(long)s * dModel + j];
                qs[r] = a;
            }
            var score = new double[vocab];
            for (int o = 0; o < vocab; o++)
            {
                double a = 0;
                for (int r = 0; r < rank; r++) a += qs[r] * K[(long)o * rank + r];
                score[o] = a;
            }
            var top = Enumerable.Range(0, vocab).Where(o => o != s)
                                .OrderByDescending(o => score[o]).Take(kk).ToHashSet();
            recallSum += (double)want.Count(w => top.Contains(w)) / kk;

            // DIRECT S³-frame: q=k= the 4 coordinate dims of E (cos on the sphere), bypassing the
            // factored operator — isolates whether the basis CARRIES the metric (rigid frame)
            // from whether the SVD factorization preserves it. For angular this is cos(coord) by
            // construction, so it is the ceiling the factored head should approach.
            var dsc = new double[vocab];
            for (int o = 0; o < vocab; o++)
            { double a = 0; for (int d = 0; d < 4 && d < dModel; d++) a += e[(long)s * dModel + d] * e[(long)o * dModel + d]; dsc[o] = a; }
            var dtop = Enumerable.Range(0, vocab).Where(o => o != s).OrderByDescending(o => dsc[o]).Take(kk).ToHashSet();
            directSum += (double)want.Count(w => dtop.Contains(w)) / kk;

            // noise floor: random top-k overlap with the true k neighbours ≈ k/vocab.
            noiseSum += (double)kk / vocab;
            cnt++;
        }
        int n = Math.Max(1, cnt);
        Console.WriteLine($"  metric-head fidelity ({metric}) over {cnt} tokens: "
            + $"NOISE FLOOR {noiseSum / n * 100:F1}%  |  factored q·k {recallSum / n * 100:F0}%  |  "
            + $"S³-frame direct (q=k=coord) {directSum / n * 100:F0}%  "
            + $"[direct = does the rigid frame carry it; factored = does the SVD keep it]");
    }

    // Pull every vocab token's NATIVE 4D super-Fibonacci S³ coordinate (physicalities.coord)
    // straight from the substrate — the mantissa/Hilbert placement the metrics are computed
    // over, NOT a derived LE eigenmap. Fills the anchor array (one coord per token, lowest
    // source_id). This is the geometry a metric head reads; LE/Procrustes cannot recover it.
    internal static async Task<int> FillCoordAnchorsAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, double[]?[] anchors)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var key in tokenSlots.Keys) vocab[vi++] = key.ToBytes();

        int filled = 0;
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = @"SELECT DISTINCT ON (p.entity_id) p.entity_id,
                ST_X(p.coord), ST_Y(p.coord), ST_Z(p.coord), ST_M(p.coord)
            FROM laplace.physicalities p
            JOIN unnest($1::bytea[]) AS u(id) ON u.id = p.entity_id
            WHERE p.type = 1 AND p.coord IS NOT NULL
            ORDER BY p.entity_id, p.source_id";
        cmd.Parameters.Add(new NpgsqlParameter
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var slots)) continue;
            var a = new[] { rdr.GetDouble(1), rdr.GetDouble(2), rdr.GetDouble(3), rdr.GetDouble(4) };
            // OVERWRITE: use the substrate's physicalities.coord verbatim — the exact coordinate
            // the metric is computed over — not whatever the tokenizer parser pre-seeded.
            foreach (int s in slots) { anchors[s] = a; filled++; }
        }
        return filled;
    }

    // Vocab-bounded trajectory ORDER LADDER in ONE walk: entity_trajectory_plane
    // masks both endpoints to the vocab inside the native scan and emits forward
    // co-occurrence at every gap 1..maxGap, degree-capped per (subject, gap). We
    // stream the single result and split it into per-gap adjacency — no per-gap
    // re-walk, no all-pairs materialization. Returns planes[0..maxGap-1] (gap g → [g-1]).
    internal static async Task<PlaneCoo[]> ReadTrajectoryLadderAsync(
        NpgsqlDataSource ds, int maxGap,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        // adj[g] : subject ordinal -> (object ordinal, w)
        var adj = new Dictionary<int, List<(int Col, double W)>>[maxGap];
        for (int g = 0; g < maxGap; g++) adj[g] = new Dictionary<int, List<(int, double)>>();

        await using var conn = await ds.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;   // the per-backend corpus build walks the whole stream once
            cmd.CommandText =
                "SELECT gap, subject_id, object_id, w FROM laplace.entity_trajectory_plane($1, $2, $3)";
            cmd.Parameters.Add(new NpgsqlParameter
                { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.AddWithValue(maxGap);
            cmd.Parameters.AddWithValue(degreeCap);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                int g = rdr.GetInt32(0);
                if (g < 1 || g > maxGap) continue;
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[1]), out var subj)) continue;
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[2]), out var obj)) continue;
                double w = rdr.GetDouble(3);
                if (w == 0.0) continue;
                var a = adj[g - 1];
                foreach (int s in subj)
                {
                    if (!a.TryGetValue(s, out var row)) a[s] = row = new List<(int, double)>(8);
                    foreach (int o in obj) row.Add((o, w));
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            Console.WriteLine($"  (trajectory ladder unavailable: {ex.SqlState} — skipped)");
            return Enumerable.Range(0, maxGap).Select(_ => PlaneCoo.Empty).ToArray();
        }

        var planes = new PlaneCoo[maxGap];
        for (int g = 0; g < maxGap; g++) planes[g] = CooFromAdj(adj[g], degreeCap);
        return planes;
    }

    // GRAPHEME-FLOOR order: P(next grapheme | current) from word constituencies
    // (laplace.grapheme_order). The grapheme-floor model factors THIS into embed/lm_head,
    // so the cast generates char-by-char following real letter statistics — and the vocab
    // (single graphemes) tokenizes any prompt in-engine with no merge path.
    internal static async Task<PlaneCoo> ReadGraphemeOrderAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int gap = 1)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        var adj = new Dictionary<int, List<(int Col, double W)>>();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;   // pays the bounded constituency walk once
        cmd.CommandText = "SELECT subject_id, object_id, w FROM laplace.grapheme_order($1, 50000, $2)";
        cmd.Parameters.Add(new NpgsqlParameter
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.AddWithValue(gap);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var subj)) continue;
            if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[1]), out var obj)) continue;
            double w = rdr.GetDouble(2);
            if (w <= 0) continue;
            foreach (int s in subj)
            {
                if (!adj.TryGetValue(s, out var row)) adj[s] = row = new List<(int, double)>(8);
                foreach (int o in obj) row.Add((o, w));
            }
        }
        return CooFromAdj(adj, 256);   // graphemes have few followers; cap generously
    }

    // WORD ORDER off the CONTENT TRAJECTORY GEOMETRY (laplace.word_order): P(next word | cur word)
    // from the witnessed sentence/phrase trajectories (tier>2), masked to the vocab — the word-tier
    // analog of ReadGraphemeOrderAsync. gap=1 bigram, gap=2 skip-gram. NOT trajectory_pairs, NOT
    // folded PRECEDES — the sequence read straight from the trajectory LineStrings.
    internal static async Task<PlaneCoo> ReadWordOrderAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots,
        int gap = 1, int trajs = 200000, int cap = 64)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        var adj = new Dictionary<int, List<(int Col, double W)>>();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;   // pays the bounded trajectory walk once
        cmd.CommandText = "SELECT subject_id, object_id, w FROM laplace.word_order($1, $2, $3)";
        cmd.Parameters.Add(new NpgsqlParameter
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.AddWithValue(trajs);
        cmd.Parameters.AddWithValue(gap);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var subj)) continue;
            if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[1]), out var obj)) continue;
            double w = rdr.GetDouble(2);
            if (w <= 0) continue;
            foreach (int s in subj)
            {
                if (!adj.TryGetValue(s, out var row)) adj[s] = row = new List<(int, double)>(8);
                foreach (int o in obj) row.Add((o, w));
            }
        }
        return CooFromAdj(adj, cap);
    }

    // LIVE trajectory order ladder: trajectory_cooccurrence_by_stride is the native
    // word-stride scan (cooccurrence_scan in C) — entity_trajectory_plane was retired.
    // Returns per-(subject,object,gap) witnessed forward counts + the per-(subject,gap)
    // total, so cnt/total is the conditional P(object follows subject at gap g). We
    // collapse gaps into one DIRECTED next-token plane, discounting by gap (closer =
    // stronger continuation), filtered to the mold's vocab. This is the ORDER signal the
    // folded causal consensus band (147 edges over the probe vocab) was missing.
    internal static async Task<PlaneCoo> ReadTrajectoryStrideAsync(
        NpgsqlDataSource ds, int maxGap,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var adj = new Dictionary<int, List<(int Col, double W)>>();
        await using var conn = await ds.OpenConnectionAsync();

        // Force _PG_init so the corpus GUC is registered BEFORE the SET. The prefix-reserve
        // reorder makes a pre-load SET adoptable, but loading the extension first makes the
        // bound apply unconditionally.
        await using (var warm = conn.CreateCommand())
        {
            warm.CommandText = "SELECT laplace.relation_type_id('IS_A')";
            await warm.ExecuteScalarAsync();
        }

        // Bound the ONE-TIME corpus build (the GUC the _PG_init reorder made reachable).
        int corpusMax = EnvInt("LAPLACE_FOUNDRY_CORPUS_MAX", 200_000);
        if (corpusMax > 0)
        {
            await using var setCmd = conn.CreateCommand();
            setCmd.CommandText = $"SET laplace_substrate.corpus_max_rows = {corpusMax}";
            await setCmd.ExecuteNonQueryAsync();
        }
        try
        {
            // Materialize the order ladder ONCE (rebuilds only when the trajectory probe
            // moves); the expensive stream build is paid here, globally, not per read.
            await using (var ensure = conn.CreateCommand())
            {
                ensure.CommandTimeout = 0;
                ensure.CommandText = "SELECT laplace.trajectory_pairs_ensure($1)";
                ensure.Parameters.AddWithValue(maxGap);
                await ensure.ExecuteScalarAsync();
            }

            // Vocab-bounded INDEX-SCAN read of the materialized ladder: w is already the
            // gap-discounted conditional continuation Σ_gap (cnt/subject_total)/gap.
            var vocab = new byte[tokenSlots.Count][];
            int vi = 0;
            foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText =
                "SELECT subject_id, object_id, w FROM laplace.trajectory_pairs_plane($1, $2)";
            cmd.Parameters.Add(new NpgsqlParameter
                { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.AddWithValue(maxGap);
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
            Console.WriteLine($"  (trajectory pairs unavailable: {ex.SqlState} — skipped)");
            return PlaneCoo.Empty;
        }

        return CooFromAdj(adj, degreeCap);
    }

    // Canonical-order COO from a subject->(object,w) adjacency, degree-capped by |w|.
    // Shared by the bounded readers so the cast law (same consensus + same mold =>
    // same bytes) holds regardless of DB scan order.
    private static PlaneCoo CooFromAdj(Dictionary<int, List<(int Col, double W)>> adj, int degreeCap)
    {
        long kept = 0;
        foreach (var row in adj.Values)
        {
            row.Sort((a, b) =>
            {
                int c = Math.Abs(b.W).CompareTo(Math.Abs(a.W));
                return c != 0 ? c : a.Col.CompareTo(b.Col);
            });
            if (row.Count > degreeCap) row.RemoveRange(degreeCap, row.Count - degreeCap);
            kept += row.Count;
        }
        var rows = new int[kept]; var cols = new int[kept]; var vals = new double[kept];
        long at = 0;
        foreach (var r in adj.Keys.OrderBy(k => k))
            foreach (var (c, w) in adj[r]) { rows[at] = r; cols[at] = c; vals[at] = w; at++; }
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

    // AFFINITY-SVD embedding: a token's vector = the SVD reduction of its full relational
    // affinity ROW (its rank-weighted edges to every other token). Two tokens with similar
    // relational fingerprints (dog/cat both IS_A mammal, both PRECEDES verbs, …) get similar
    // rows → similar embeddings. This is the direct factorization of the consensus the user
    // describes, vs Laplacian-eigenmaps over the capped graph (which measured 0.58σ). The
    // affinity is symmetrized; the top-k left singular vectors (scaled by √S) are the basis.
    // NOTE: tensor_svd_truncate needs kmax ≥ min(m,n) = vocab, so this is dense vocab×vocab —
    // use it at modest vocab (≤~3k); larger vocab needs a sparse solver.
    internal static double[] BuildBasisAffinity(
        int vocab, int dModel, PlaneCoo aff, double[]?[] anchors, Hash128 seed,
        out BasisStats stats)
    {
        int k = Math.Min(dModel - 1, Math.Min(EnvInt("LAPLACE_FOUNDRY_BASIS_RANK", 256), vocab));
        var A = new float[(long)vocab * vocab];
        for (long e = 0; e < aff.Nnz; e++)
        {
            int x = aff.Rows[e], y = aff.Cols[e];
            if (x < 0 || x >= vocab || y < 0 || y >= vocab) continue;
            float w = (float)aff.Vals[e];
            A[(long)x * vocab + y] += w;
            A[(long)y * vocab + x] += w;   // symmetrize (similarity is undirected)
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

    // FAITHFUL low-rank factorization of the rated adjacency for the generative cast.
    // A[X,Y] = rank-weighted rating of the continuation X→Y (DIRECTED — not symmetrized).
    // The truncated SVD A ≈ U S Vᵀ to rank = dim is the EXACT optimal rank-dim approximation
    // (Eckart–Young). embed[X] = U[X]·√S, lm_head[Y] = V[Y]·√S, so in the cast
    //   logits[Y|X] = lm_head[Y]·embed[X] = Σ_k U[X,k]·S_k·V[Y,k] = A_dim[X,Y]
    // — the rank-weighted rating LOOKED UP, factored to the hidden width. dim is a NORMAL
    // embedding size (e.g. 512), NEVER vocab: a 32k-token model is 32000×512, not 32000².
    // The only loss is the truncation tail (singular values past dim); no learning, no gains.
    // A is scaled to max|w|→1 first so reconstructed logits keep sane magnitude (the cast's
    // RMSNorm contributes a per-token positive factor = temperature, not an argmax change).
    // embed/lmHead come back row-major [vocab × dim], zero-padded past the spectral rank.
    // NOTE: dense vocab×vocab SVD — modest vocab (≤~4k). Larger needs a sparse/randomized solver.
    internal static void FactorAdjacency(
        PlaneCoo adj, int vocab, int dim, out double[] embed, out double[] lmHead, out int usedRank,
        bool conditional = false, bool suppressSelf = false, double dehub = 0.0)
    {
        var rowSum = new double[vocab];
        var colSum = new double[vocab];
        double total = 0;
        // accumulate the directed rated adjacency as a SPARSE list (row=subject X, col=object Y)
        var ex = new int[adj.Nnz]; var ey = new int[adj.Nnz]; var ew = new double[adj.Nnz]; int en = 0;
        for (long e = 0; e < adj.Nnz; e++)
        {
            int x = adj.Rows[e], y = adj.Cols[e];
            if (x < 0 || x >= vocab || y < 0 || y >= vocab) continue;
            if (suppressSelf && x == y) continue;   // no X→X echo (king→king); grapheme keeps doubles
            double w = adj.Vals[e];
            ex[en] = x; ey[en] = y; ew[en] = w; en++;
            rowSum[x] += w; colSum[y] += w; total += w;
        }
        // PPMI: As[X,Y] = max(0, ln( A[X,Y]·T / (rowSum[X]·colSum[Y]) )). This is THE proven
        // co-occurrence→embedding transform (positive pointwise mutual information; SVD-of-PPMI
        // equals skip-gram, Levy–Goldberg 2014). It conditions each X→Y rating on the base rates,
        // so the global hub (high in-degree function words the/I/and) is divided out and X's
        // SPECIFIC continuation surfaces. It is NON-NEGATIVE and ZERO where there is no edge, so
        // the SVD spends its rank on real structure, not the hub, and the empty byte/special rows
        // stay ~0 (no spurious byte continuations). Not a tuned scalar — the marginal conditioning
        // the invention demands ("whitespace must not weigh as heavily as content words").
        // Build As as a SPARSE edge list (non-edges = implicit 0 = uniform baseline). conditional
        // → signed log-odds log(P(Y|X)·V) (the GENERATIVE readout; PPMI's −ln P(Y) inflates rare
        // next-tokens into a hub, a SIMILARITY transform wrong for generation); else → PPMI (≥0).
        var sx = new int[en]; var sy = new int[en]; var sv = new double[en]; int sn = 0;
        for (int i = 0; i < en; i++)
        {
            double val;
            if (conditional)
            {
                double rs = rowSum[ex[i]];
                if (rs <= 0) continue;
                val = Math.Log(ew[i] / rs * vocab);     // log(P(Y|X)·V); non-edges stay 0 = uniform
                // DE-HUB: subtract λ·log P(Y) so high-frequency function-word continuations (the/of/and)
                // lose weight and the "the only one of the" loop weakens. λ=0 = pure conditional flow;
                // λ=1 ≈ PMI. P(Y)=colSum[Y]/total (≤1 so log<0; the subtraction boosts rare Y more than
                // frequent Y → frequent hubs relatively suppressed).
                if (dehub != 0.0 && colSum[ey[i]] > 0 && total > 0)
                    val -= dehub * Math.Log(colSum[ey[i]] / total);
            }
            else
            {
                double denom = rowSum[ex[i]] * colSum[ey[i]];
                if (denom <= 0) continue;
                val = Math.Log(ew[i] * total / denom);
                if (val <= 0) continue;                 // PPMI clamps negatives to 0
            }
            if (val == 0) continue;
            sx[sn] = ex[i]; sy[sn] = ey[i]; sv[sn] = val; sn++;
        }

        embed  = new double[(long)vocab * dim];
        lmHead = new double[(long)vocab * dim];

        // DENSE full SVD for modest vocab; RANDOMIZED sparse-sketch SVD above it. A dense
        // vocab×vocab matrix is 32k²·4B = 4GB with an O(vocab³) SVD — the SELF-INFLICTED "≤4k"
        // ceiling. The randomized SVD (Halko–Martinsson–Tropp) NEVER forms it: it sketches the
        // SPARSE As with a Gaussian, projects, and SVDs only the small L×vocab band. Same factors.
        if (vocab <= EnvInt("LAPLACE_FOUNDRY_DENSE_SVD_MAX", 6000))
        {
            var As = new float[(long)vocab * vocab];
            for (int i = 0; i < sn; i++) As[(long)sx[i] * vocab + sy[i]] = (float)sv[i];
            var U  = new float[(long)vocab * vocab];
            var S  = new float[vocab];
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
            // S goes ENTIRELY on lm_head (embed=U keeps unit columns so RMSNorm preserves direction).
            for (int c = 0; c < kk; c++)
            {
                double s = Math.Max(0f, S[c]);
                for (int i = 0; i < vocab; i++)
                {
                    embed[(long)i * dim + c]  = (double)U[(long)i * vocab + c];
                    lmHead[(long)i * dim + c] = (double)Vt[(long)c * vocab + i] * s;
                }
            }
        }
        else
        {
            usedRank = FactorSparseRandomized(sx, sy, sv, sn, vocab, dim, embed, lmHead);
        }
    }

    // Randomized truncated SVD of the SPARSE signed matrix As (edge sx[i]→sy[i] = sv[i], else 0):
    // embed = U[:,0:dim], lm_head = (V·S)[:,0:dim] — the SAME factors the dense path returns, with
    // NO vocab×vocab dense matrix ever formed. Halko–Martinsson–Tropp with q power iterations; the
    // only dense SVD is on the L×vocab projected band (L = dim + oversample). Matvecs parallelize
    // over the L sketch rows (each row independent → race-free), so 32k words factor in seconds.
    internal static int FactorSparseRandomized(
        int[] sx, int[] sy, double[] sv, int sn, int vocab, int dim, double[] embed, double[] lmHead)
    {
        int L = Math.Min(vocab, dim + EnvInt("LAPLACE_FOUNDRY_RSVD_OVERSAMPLE", 16));
        int q = EnvInt("LAPLACE_FOUNDRY_RSVD_POWER", 1);
        // Ω and the running sketch are L×vocab (L vectors as rows).
        var Y  = new double[(long)L * vocab];
        var Om = new double[(long)L * vocab];
        ulong seed = SplitMix(0x9E3779B97F4A7C15UL ^ (ulong)vocab ^ ((ulong)dim << 32));
        for (long t = 0; t < (long)L * vocab; t++) Om[t] = Gaussian(ref seed);
        SpMatVec(sx, sy, sv, sn, Om, Y, L, vocab, false);                 // Y = As·Ωᵀ
        var Z = new double[(long)L * vocab];
        for (int it = 0; it < q; it++)
        {
            Array.Clear(Z); SpMatVec(sx, sy, sv, sn, Y, Z, L, vocab, true);    // Z = Asᵀ·Y
            Array.Clear(Y); SpMatVec(sx, sy, sv, sn, Z, Y, L, vocab, false);   // Y = As·Z
        }
        // Orthonormalize the range of Y via its Gram matrix G = Y·Yᵀ (L×L). This is RANK-REVEALING:
        // a sparse log-odds matrix with a dominant direction makes the sketch rank-deficient, which
        // Gram-Schmidt rejects (rc=-4). The Gram eigendecomposition instead DROPS the deficient
        // directions (σ_k ≤ eps·σ_0). Q[k] = (1/√σ_k)·Σ_i W[i,k]·Y[i] is then orthonormal by W's
        // orthonormality, with no division by ~0.
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
        var Q = new double[(long)rkQ * vocab];                            // rkQ×vocab orthonormal rows
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
        var B = new double[(long)rkQ * vocab];                            // B = Q·As  (rkQ×vocab)
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
        // U_As = Qᵀ·Ũ (vocab×kk); embed = U_As, lm_head = V·S (S on lm_head, see dense note).
        System.Threading.Tasks.Parallel.For(0, vocab, x =>
        {
            for (int c = 0; c < kk; c++)
            {
                double acc = 0;
                for (int j = 0; j < rkQ; j++) acc += Q[(long)j * vocab + x] * Ub[(long)j * rkQ + c];
                embed[(long)x * dim + c]  = acc;
                lmHead[(long)x * dim + c] = (double)Vtb[(long)c * vocab + x] * Math.Max(0f, Sb[c]);
            }
        });
        return kk;
    }

    // Out[c,a] += Σ_edge sv·M[c,b], (a,b)=(x,y) for As·M (transpose=false) or (y,x) for Asᵀ·M.
    // Parallel over the L sketch rows c — each row writes only its own band, so no races.
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
    // B[c,y] += Q[c,x]·sv  (B = Q·As). Parallel over c.
    static void SpMatVecQ(int[] sx, int[] sy, double[] sv, int sn, double[] Q, double[] B, int L, int vocab)
    {
        System.Threading.Tasks.Parallel.For(0, L, c =>
        {
            long baseC = (long)c * vocab;
            for (int i = 0; i < sn; i++) B[baseC + sy[i]] += Q[baseC + sx[i]] * sv[i];
        });
    }

    // Generates E [vocab × dModel] row-major. anchors[i] is null or a 4D content
    // coordinate for vocab ordinal i. The seed must derive from the recipe (never
    // the clock) so identical consensus + identical mold ⇒ identical cast.
    internal static double[] BuildBasis(
        int vocab, int dModel, PlaneCoo leGraph, double[]?[] anchors, Hash128 seed,
        out BasisStats stats)
    {
        bool coordOnly = EnvInt("LAPLACE_FOUNDRY_COORD_ONLY", 0) != 0;
        int k = coordOnly
            ? Math.Min(4, dModel - 1)
            : Math.Min(Math.Min(dModel - 1, EnvInt("LAPLACE_FOUNDRY_BASIS_RANK", 256)),
                       Math.Max(2, vocab - 2));
        var y = GC.AllocateUninitializedArray<double>(checked(vocab * k), pinned: true);
        if (coordOnly)
        {
            // PURE S³ RIGID FRAME — NO Lanczos eigensolve, NO GSO, NO Procrustes. The substrate's
            // own 4D super-Fibonacci coord IS the embedding (FillCoordAnchors filled `anchors`);
            // q·k is then cos on S³ = the angular metric EXACTLY. O(vocab), well-conditioned by
            // construction. This removes the 392s eigensolve and its residual-68 Procrustes — that
            // ill-conditioned basis collapsed the 32k cast to a single 'or' attractor. Off-graph
            // tokens stay zero (the row-norm fallback parks them on the bias channel).
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
        }

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
        bool coordDirect = EnvInt("LAPLACE_FOUNDRY_COORD_DIRECT", 0) != 0;
        if (coordOnly)
        {
            // coords are already placed in dims 0..3; no eigenmap to align, nothing to Procrustes.
        }
        else if (coordDirect && fitIdx.Count > 0)
        {
            // S³ IS THE RIGID FRAME — do not FIT one. The substrate's own 4D super-Fibonacci
            // coordinate is the entity's position in that fixed frame; place it in dims 0..3
            // VERBATIM (no LE, no GSO, no Procrustes rotation of an eigenmap that residual~11
            // proves can't align). q·k is then cos on S³ = angular distance EXACTLY, so a
            // geometric head reproduces its metric. COORD_SCALE lets the frame dominate the
            // (optional) LE relation structure left in dims 4..k for the Glicko-rated heads.
            double cs = EnvDouble("LAPLACE_FOUNDRY_COORD_SCALE", 1.0);
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
                    // The anchoring OVERWRITES the top-4 (most significant) spectral dims with
                    // the content-coordinate fit. When the fit is poor (high residual) this
                    // corrupts the strongest geometry rather than aligning it — gate it so the
                    // pure Laplacian-eigenmap geometry can be used instead.
                    double scale = anchSq > 0 ? Math.Sqrt(specSq / anchSq) : 1.0;
                    if (EnvInt("LAPLACE_FOUNDRY_PROCRUSTES", 1) != 0)
                        for (int i = 0; i < vocab; i++)
                            for (int d = 0; d < 4; d++)
                                y[(long)i * k + d] = a4[(long)i * 4 + d] * scale;
                }
                finally { DynInterop.ProcrustesFree(t); }
            }
        }

        for (int i = 0; i < vocab; i++)
            Array.Copy(y, (long)i * k, e, (long)i * dModel, k);

        // Deterministic capacity dims: seeded Gaussian columns that give the embedding
        // full rank WITHOUT drowning the spectral geometry. The GSO'd spectral block has
        // per-row energy ≈ k/vocab; size the capacity block to carry only capFrac of that
        // total, so ≥(1-capFrac) of each normalized row is consensus structure (otherwise
        // 1792 random dims at spectral magnitude out-energize the 256 real dims and the
        // similarity cosine washes to noise — measured: +0.05σ → the geometry vanishes).
        double capFrac = EnvDouble("LAPLACE_FOUNDRY_CAP_FRAC", 0.05);
        int capDims = Math.Max(1, dModel - 1 - k);
        double capScale = Math.Sqrt(capFrac * ((double)k / vocab) / capDims);
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

    internal sealed record Factors(float[] Left, float[] Right, int Rank, int Dim, double SampleResidual, double SpectralNorm);

    // Factor M ≈ Leftᵀ·Right with Left/Right [rankCap × d] rows = √Sᵣ·uᵣᵀ / √Sᵣ·vᵣᵀ.
    // transpose=true factors Mᵀ instead (for operators whose composed orientation
    // is Wouter·Winner, e.g. Wo·Wv and Wdown·Wup). The native kernel computes the
    // FULL SVD (its kmax is buffer capacity, required ≥ min(m,n)) and truncates by
    // rel_err_tol; the mold's rank cap is applied here, keeping the strongest modes.
    //
    // Factors are SPECTRALLY NORMALIZED (divided by √s₀ each, so the composed
    // operator is M/s₀ with spectral norm 1). Plane normalization bounds edge
    // weights, not ‖EᵀAE‖₂ — unnormalized, one layer's residual add is s₀ (~10²)
    // times the stream and the forward pass power-iterates onto the dominant
    // eigendirection, erasing the prompt (measured: paris rank 267→18,559 after
    // one layer). The layer scales in the fill are the entire depth budget.
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

        double s0 = k > 0 && s[0] > 0f ? s[0] : 1.0;
        var left = new float[(long)k * d];
        var right = new float[(long)k * d];
        for (int r = 0; r < k; r++)
        {
            float sq = MathF.Sqrt((float)(Math.Max(0f, s[r]) / s0));
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
            double exact = a[(long)i * d + j] / s0;
            num += (exact - approx) * (exact - approx);
            den += exact * exact;
        }
        double resid = den > 0 ? Math.Sqrt(num / den) : 0.0;
        return new Factors(left, right, k, d, resid, s0);
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

    // DIRECT RIGID-FRAME attention head: q/k SELECT the basis coordinate dims (0..coordDims) so
    // q·k = coord·coord = cos on S³ = the angular metric EXACTLY (RMSNorm supplies the per-token
    // unit normalization). No factored operator — the head IS the S³ frame, transcribed (measured
    // 100% neighbour recall vs 8% for the SVD-factored path, 2.1% noise floor). Every head in the
    // layer reads the same coordinate block.
    internal static void FillCoordHead(float[] vals, int rows, int cols, int headDim, int coordDims, double scale)
    {
        if (headDim <= 0) return;
        int nh = rows / headDim;
        for (int h = 0; h < nh; h++)
            for (int d = 0; d < coordDims && d < headDim && d < cols; d++)
                vals[(long)(h * headDim + d) * cols + d] = (float)scale;
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
