using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Cli;


















internal static class FoundryExport
{
    internal const double BiasValue = 1.0;

    internal sealed record PlaneCoo(int[] Rows, int[] Cols, double[] Vals)
    {
        public int Nnz => Rows.Length;
        public static readonly PlaneCoo Empty = new([], [], []);
    }






    internal readonly record struct PlaneSpec(string Family, string Name, int? Arg)
    {
        public static PlaneSpec Consensus(string name) => new("consensus", name, null);
        public static PlaneSpec TrajNext() => new("traj", "next", null);
        public static PlaneSpec TrajGap(int g) => new("traj", "gap", g);
        public static PlaneSpec TrajWindow(int w) => new("traj", "window", w);
        public override string ToString() => Arg is null ? $"{Family}:{Name}" : $"{Family}:{Name}:{Arg}";
    }




    internal static async Task<PlaneCoo> ReadRelationPlaneAsync(
        NpgsqlDataSource ds, PlaneSpec spec,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var adj = new Dictionary<int, List<(int Col, double W)>>();
        // Vocab pushdown (2026-07-09): unfiltered consensus-family reads streamed
        // 27.5M rows per synthesis so this loop could keep ~1%; the vocab probes the
        // (type_id, subject_id) index server-side instead. Client filter retained
        // as the correctness net (traj family still returns unfiltered).
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();
        await using var conn = await ds.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText =
                "SELECT subject_id, object_id, w FROM laplace.relation_plane($1, $2, $3, $4)";
            cmd.Parameters.AddWithValue(spec.Family);
            cmd.Parameters.AddWithValue(spec.Name);
            cmd.Parameters.AddWithValue((object?)spec.Arg ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = spec.Family == "consensus" ? vocab : (object)DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea
            });
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
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
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






    internal static async Task<PlaneCoo> ReadLayerPlaneMaskedAsync(
    NpgsqlDataSource ds, Mask256 bandMask,
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
                "SELECT subject_id, object_id, w, layer_rank FROM laplace.consensus_layer_plane_masked($1, $2, $3)";
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = bandMask.ToByteArray(), NpgsqlDbType = NpgsqlDbType.Bytea });
            cmd.Parameters.AddWithValue(degreeCap);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var subj)) continue;
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[1]), out var obj)) continue;
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
            Console.WriteLine($"  (layer plane [masked band] unavailable: {ex.SqlState} — skipped)");
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

    internal sealed record TypePlane(Hash128 TypeId, double Rank, PlaneCoo Plane);

    internal static async Task<List<TypePlane>> ReadTypePlanesAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int degreeCap,
        IReadOnlyCollection<Hash128>? typeIds = null)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();


        var byType = new Dictionary<Hash128, (double Rank, Dictionary<int, List<(int Col, double W)>> Adj)>();
        await using var conn = await ds.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText =
                "SELECT subject_id, object_id, w, type_id, layer_rank FROM laplace.consensus_type_plane($1, $2, $3)";
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.AddWithValue(degreeCap);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = (typeIds is { Count: > 0 })
                    ? typeIds.Select(t => t.ToBytes()).ToArray()
                    : (object)DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            });
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

        result.Sort((a, b) => b.Rank.CompareTo(a.Rank));
        return result;
    }




    internal static async Task<PlaneCoo> ReadAttributePlaneAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, string relationType, int degreeCap)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        var wordCat = new Dictionary<int, Hash128>();
        var wordMu = new Dictionary<int, double>();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 180;
        cmd.CommandText =
            "SELECT subject_id, object_id, " +
            "GREATEST((eff_mu(rating, rd) - glicko2_neutral_mu())::double precision / 1e9, 0) AS w " +
            "FROM laplace.consensus " +
            "WHERE type_id = laplace.relation_type_id($1) AND subject_id = ANY($2)";
        cmd.Parameters.AddWithValue(relationType);
        cmd.Parameters.Add(new NpgsqlParameter
        { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var subj)) continue;
            var cat = FromBytes((byte[])rdr[1]);
            double w = rdr.GetDouble(2);
            if (w <= 0) w = 1e-6;
            foreach (int t in subj)
            {
                wordCat[t] = cat;
                wordMu[t] = Math.Max(wordMu.GetValueOrDefault(t), w);
            }
        }

        var byCat = new Dictionary<Hash128, List<int>>();
        foreach (var (tok, cat) in wordCat)
        {
            if (!byCat.TryGetValue(cat, out var list)) byCat[cat] = list = new List<int>(8);
            list.Add(tok);
        }

        var adj = new Dictionary<int, List<(int Col, double W)>>();
        foreach (var group in byCat.Values)
        {
            if (group.Count < 2) continue;
            for (int i = 0; i < group.Count; i++)
            {
                int a = group[i];
                double muA = wordMu.GetValueOrDefault(a, 1.0);
                if (!adj.TryGetValue(a, out var row)) adj[a] = row = new List<(int, double)>(8);
                for (int j = 0; j < group.Count; j++)
                {
                    if (i == j) continue;
                    int b = group[j];
                    double muB = wordMu.GetValueOrDefault(b, 1.0);
                    double w = Math.Sqrt(muA * muB);
                    row.Add((b, w));
                }
            }
        }
        return CooFromAdj(adj, degreeCap);
    }

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
        cmd.CommandTimeout = 0;
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
            double w = Math.Exp(-dist);
            if (w == 0.0) continue;
            foreach (int s in subj)
            {
                if (!adj.TryGetValue(s, out var row)) adj[s] = row = new List<(int, double)>(8);
                foreach (int o in obj) row.Add((o, w));
            }
        }
        return CooFromAdj(adj, degreeCap);
    }






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





            var dsc = new double[vocab];
            for (int o = 0; o < vocab; o++)
            { double a = 0; for (int d = 0; d < 4 && d < dModel; d++) a += e[(long)s * dModel + d] * e[(long)o * dModel + d]; dsc[o] = a; }
            var dtop = Enumerable.Range(0, vocab).Where(o => o != s).OrderByDescending(o => dsc[o]).Take(kk).ToHashSet();
            directSum += (double)want.Count(w => dtop.Contains(w)) / kk;


            noiseSum += (double)kk / vocab;
            cnt++;
        }
        int n = Math.Max(1, cnt);
        Console.WriteLine($"  metric-head fidelity ({metric}) over {cnt} tokens: "
            + $"NOISE FLOOR {noiseSum / n * 100:F1}%  |  factored q·k {recallSum / n * 100:F0}%  |  "
            + $"S³-frame direct (q=k=coord) {directSum / n * 100:F0}%  "
            + $"[direct = does the rigid frame carry it; factored = does the SVD keep it]");
    }





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
        cmd.CommandText =
            "SELECT entity_id, x, y, z, m FROM laplace.entity_physicality_coords($1)";
        cmd.Parameters.Add(new NpgsqlParameter
        { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var slots)) continue;
            var a = new[] { rdr.GetDouble(1), rdr.GetDouble(2), rdr.GetDouble(3), rdr.GetDouble(4) };


            foreach (int s in slots) { anchors[s] = a; filled++; }
        }
        return filled;
    }






    /// Plan Phase 5 (doc 14 P7): per-token 128-bit hilbert key (big-endian; leading
    /// bytes = coarsest content locality) for the static content-PE dims.
    internal static async Task<byte[]?[]> FillHilbertKeysAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int vocabSize)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var key in tokenSlots.Keys) vocab[vi++] = key.ToBytes();

        var keys = new byte[]?[vocabSize];
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = "SELECT entity_id, hilbert_index FROM laplace.entity_hilbert_keys($1)";
        cmd.Parameters.Add(new NpgsqlParameter
        { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        try
        {
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var slots)) continue;
                var hb = (byte[])rdr[1];
                foreach (int s in slots) if (s >= 0 && s < vocabSize) keys[s] = hb;
            }
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            Console.WriteLine($"  (entity_hilbert_keys unavailable: {ex.SqlState} — content-PE skipped)");
        }
        return keys;
    }

    internal static async Task<PlaneCoo[]> ReadTrajectoryLadderAsync(
        NpgsqlDataSource ds, int maxGap,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();


        var adj = new Dictionary<int, List<(int Col, double W)>>[maxGap];
        for (int g = 0; g < maxGap; g++) adj[g] = new Dictionary<int, List<(int, double)>>();

        await using var conn = await ds.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
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





    internal static async Task<PlaneCoo> ReadGraphemeOrderAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int gap = 1)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        var adj = new Dictionary<int, List<(int Col, double W)>>();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
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
        return CooFromAdj(adj, 256);
    }





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
        cmd.CommandTimeout = 0;
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








    /// Finish-line Phase 3 (the conditional theorem): the smoothed log-conditional
    /// continuation table. Returns the sparse top-cap entries per row plus each
    /// row's UNSEEN default (NULL-object rows from the SQL) — absent pairs must
    /// densify to the default, NOT zero (zero = log 1 would rank every unseen
    /// continuation above every seen one).
    internal static async Task<(PlaneCoo Plane, double[] RowDefault)> ReadConditionalPlaneAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int vocabSize,
        int trajs = 200000, double smoothK = 0.5, int cap = 512)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        var adj = new Dictionary<int, List<(int Col, double W)>>();
        var rowDefault = new double[vocabSize];
        Array.Fill(rowDefault, double.NaN);
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText =
            "SELECT subject_id, object_id, w FROM laplace.continuation_conditional_plane($1, $2, $3, $4)";
        cmd.Parameters.Add(new NpgsqlParameter
        { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.AddWithValue(trajs);
        cmd.Parameters.AddWithValue(smoothK);
        cmd.Parameters.AddWithValue(cap);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var subj)) continue;
            double w = rdr.GetDouble(2);
            if (rdr.IsDBNull(1))
            {
                foreach (int s in subj) if (s >= 0 && s < vocabSize) rowDefault[s] = w;
                continue;
            }
            if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[1]), out var obj)) continue;
            foreach (int s in subj)
            {
                if (!adj.TryGetValue(s, out var row)) adj[s] = row = new List<(int, double)>(16);
                foreach (int o in obj) row.Add((o, w));
            }
        }
        return (CooFromAdj(adj, cap), rowDefault);
    }

    /// Finish-line Phase 4 v1b: dominant POS class per vocab token + the class
    /// transition log-conditional table, for the lm_head compose
    /// M[x,y] += gain * T[class(x),class(y)] (grammatical shaping of the
    /// unseen mass — the flat row default becomes class-differentiated).
    internal static async Task<(int[] TokenClass, double[,] T, int NClasses)> ReadPosCorrectionAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int vocabSize)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        var tokenClass = new int[vocabSize];
        Array.Fill(tokenClass, -1);
        var classIndex = new Dictionary<Hash128, int>();
        await using var conn = await ds.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 600;
            cmd.CommandText = "SELECT word_id, pos_id FROM laplace.vocab_dominant_pos($1)";
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var slots)) continue;
                var pos = FromBytes((byte[])rdr[1]);
                if (!classIndex.TryGetValue(pos, out int ci)) classIndex[pos] = ci = classIndex.Count;
                foreach (int s in slots)
                    if (s >= 0 && s < vocabSize) tokenClass[s] = ci;
            }
        }
        var entries = new List<(Hash128 Px, Hash128 Py, double W)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 600;
            cmd.CommandText = "SELECT subject_id, object_id, w FROM laplace.pos_class_transitions($1)";
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                entries.Add((FromBytes((byte[])rdr[0]), FromBytes((byte[])rdr[1]), rdr.GetDouble(2)));
        }
        int nc = classIndex.Count;
        // unattested class pairs get the table's own floor, not 0 (0 = certainty-scale
        // log-prob and would rank an unseen class pair above every seen one)
        double floor = entries.Count > 0 ? entries.Min(e => e.W) : 0.0;
        var t = new double[nc, nc];
        for (int i = 0; i < nc; i++)
            for (int j = 0; j < nc; j++) t[i, j] = floor;
        foreach (var (px, py, w) in entries)
            if (classIndex.TryGetValue(px, out int ci) && classIndex.TryGetValue(py, out int cj))
                t[ci, cj] = w;
        return (tokenClass, t, nc);
    }

    /// Plan Phase 4: sentence-boundary word bridge — last word of sentence i to
    /// first word of sentence i+1, from tier-4 document trajectories. The
    /// discourse component of the lm_head (doc 14 C3 correction).
    internal static async Task<PlaneCoo> ReadSentenceOrderAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots,
        int docs = 100000, int cap = 64)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

        var adj = new Dictionary<int, List<(int Col, double W)>>();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = "SELECT subject_id, object_id, w FROM laplace.sentence_order_word_bridge($1, $2)";
        cmd.Parameters.Add(new NpgsqlParameter
        { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.AddWithValue(docs);
        try
        {
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
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            Console.WriteLine($"  (sentence_order_word_bridge unavailable: {ex.SqlState} — skipped)");
            return PlaneCoo.Empty;
        }
        return CooFromAdj(adj, cap);
    }

    internal static async Task<PlaneCoo> ReadTrajectoryStrideAsync(
        NpgsqlDataSource ds, int maxGap,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var adj = new Dictionary<int, List<(int Col, double W)>>();
        await using var conn = await ds.OpenConnectionAsync();




        await using (var warm = conn.CreateCommand())
        {
            warm.CommandText = "SELECT laplace.relation_type_id('IS_A')";
            await warm.ExecuteScalarAsync();
        }


        int corpusMax = FoundryDefaults.CorpusMax;
        if (corpusMax > 0)
        {
            await using var setCmd = conn.CreateCommand();
            // set_config instead of interpolated SET: parameterizable (SET is not),
            // so the statement text stays stable for server-side plan reuse.
            setCmd.CommandText = "SELECT set_config('laplace_substrate.corpus_max_rows', $1, false)";
            setCmd.Parameters.AddWithValue(corpusMax.ToString());
            await setCmd.ExecuteNonQueryAsync();
        }
        try
        {





            var vocab = new byte[tokenSlots.Count][];
            int vi = 0;
            foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();

            int trajCap = corpusMax > 0 ? corpusMax : 200_000;
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText =
                "SELECT subject_id, object_id, w FROM laplace.word_order($1, $2, $3)";
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.AddWithValue(trajCap);
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





        if (FoundryDefaults.Ppmi) ApplyPpmi(adj);
        return CooFromAdj(adj, degreeCap);
    }



    private static void ApplyPpmi(Dictionary<int, List<(int Col, double W)>> adj)
    {
        var colSum = new Dictionary<int, double>();
        var rowSum = new Dictionary<int, double>(adj.Count);
        double n = 0;
        foreach (var (s, row) in adj)
        {
            double rs = 0;
            foreach (var (o, w) in row) { rs += w; colSum[o] = colSum.GetValueOrDefault(o) + w; n += w; }
            rowSum[s] = rs;
        }
        if (n <= 0) return;
        foreach (var s in adj.Keys.ToList())
        {
            double rs = rowSum[s];
            var nr = new List<(int, double)>(adj[s].Count);
            foreach (var (o, w) in adj[s])
            {
                double denom = rs * colSum[o];
                if (denom <= 0) continue;
                double pmi = Math.Log(w * n / denom);
                if (pmi > 0) nr.Add((o, pmi));
            }
            adj[s] = nr;
        }
    }




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




    internal static PlaneCoo Normalize(PlaneCoo p)
    {
        double max = 0;
        foreach (var v in p.Vals) max = Math.Max(max, Math.Abs(v));
        if (max <= 0 || max == 1.0) return p;
        var vals = new double[p.Nnz];
        for (int i = 0; i < vals.Length; i++) vals[i] = p.Vals[i] / max;
        return p with { Vals = vals };
    }

    /// P5 signed/nonneg split: operator planes carry SIGNED weights (refutation =
    /// negative attention; ProjectOperator is sign-safe), but the basis union feeds
    /// the normalized-Laplacian eigenmap, which needs a nonnegative affinity.
    /// Clamp negatives to zero (drop, not abs — a refuted edge is not affinity).
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
        byte[]?[]? hilbertKeys = null)
    {
        bool coordOnly = FoundryDefaults.CoordOnly;
        // Phase 5: when a hilbert content-PE is requested, RESERVE its trailing dims
        // up front — otherwise a full-rank spectrum (k = dModel-1) leaves peDims = 0
        // and the PE silently vanishes (observed on the first Path-A synthesis).
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

        // Plan Phase 5 (doc 14 P7): hilbert content-PE in the trailing capacity dims,
        // written AFTER row-normalization so every token carries the same fixed-scale
        // positional fraction (content dims are unit-norm; PE rides at HilbertPeScale).
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
                    var hb = hilbertKeys.Length > i ? hilbertKeys[i] : null;
                    if (hb is null || hb.Length == 0) continue;
                    long off = (long)i * dModel;
                    for (int d = 0; d < peDims; d++)
                    {
                        byte b = d < hb.Length ? hb[d] : (byte)0;
                        e[off + peBase + d] = (2.0 * b / 255.0 - 1.0) * peScale;
                    }
                    placedPe++;
                }
                Console.WriteLine($"  hilbert content-PE: {placedPe:N0} tokens in {peDims} trailing dims (scale {peScale})");
            }
        }

        stats = new BasisStats(k, zeroSpectral, resid);
        return e;
    }



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













    /// P3 (doc 14 M2): block Gram-Schmidt across per-head OV output bases so each
    /// head writes an orthogonal residual slice. Rows are orthonormalized jointly
    /// (earlier heads keep their span; later heads get the complement — native
    /// Householder QR, deterministic), then each row's ORIGINAL norm is re-applied
    /// so singular-value energy per head survives; only the overlap is removed.
    /// Fail-open: on native rc != 0 (rank deficiency) the factors are left as-is.
    internal static void BlockOrthonormalizeLeft(
        IReadOnlyList<string> headKeys, Dictionary<string, Factors> fo, int dModel)
    {
        var live = new List<(string Key, Factors F)>();
        int total = 0;
        foreach (var k in headKeys)
            if (fo.TryGetValue(k, out var f) && f.Rank > 0 && f.Dim == dModel)
            { live.Add((k, f)); total += f.Rank; }
        if (live.Count < 2 || total > dModel) return;

        var buf = new double[(long)total * dModel];
        var norms = new double[total];
        int v = 0;
        foreach (var (_, f) in live)
            for (int r = 0; r < f.Rank; r++, v++)
            {
                double n2 = 0;
                for (int j = 0; j < dModel; j++)
                {
                    double x = f.Left[(long)r * dModel + j];
                    buf[(long)v * dModel + j] = x;
                    n2 += x * x;
                }
                norms[v] = Math.Sqrt(n2);
            }

        int rc;
        unsafe { fixed (double* p = buf) rc = DynInterop.GramSchmidtOrthonormalize(p, (nuint)total, (nuint)dModel); }
        if (rc != 0)
        {
            // Rank deficiency here IS doc-14 M2 observed: the heads' output bases are
            // collinear. Managed modified Gram-Schmidt: orthogonalize sequentially,
            // ZERO directions that vanish (a later head's collinear component carries
            // no new information — removing it is the allocation working, not a loss).
            int zeroed = 0;
            for (int a = 0; a < total; a++)
            {
                long ao = (long)a * dModel;
                for (int b2 = 0; b2 < a; b2++)
                {
                    long bo = (long)b2 * dModel;
                    double dot = 0;
                    for (int j = 0; j < dModel; j++) dot += buf[ao + j] * buf[bo + j];
                    if (dot == 0) continue;
                    for (int j = 0; j < dModel; j++) buf[ao + j] -= dot * buf[bo + j];
                }
                double n2r = 0;
                for (int j = 0; j < dModel; j++) n2r += buf[ao + j] * buf[ao + j];
                if (n2r < 1e-12)
                {
                    for (int j = 0; j < dModel; j++) buf[ao + j] = 0;
                    zeroed++;
                    continue;
                }
                double invr = 1.0 / Math.Sqrt(n2r);
                for (int j = 0; j < dModel; j++) buf[ao + j] *= invr;
            }
            Console.WriteLine($"  head-allocation: native QR rc={rc} → managed MGS, {zeroed}/{total} collinear directions zeroed (M2 overlap removed)");
        }

        v = 0;
        foreach (var (key, f) in live)
        {
            var left = new float[(long)f.Rank * dModel];
            for (int r = 0; r < f.Rank; r++, v++)
                for (int j = 0; j < dModel; j++)
                    left[(long)r * dModel + j] = (float)(norms[v] * buf[(long)v * dModel + j]);
            fo[key] = f with { Left = left };
        }
    }

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
        // spectrum flattening (M3 at operator level): each side carries
        // (s_r/s0)^(alpha/2) so the reconstructed direction r scales (s_r/s0)^alpha.
        float halfAlpha = (float)(FoundryDefaults.FactorSpectrumAlpha / 2.0);
        for (int r = 0; r < k; r++)
        {
            float sq = MathF.Pow((float)(Math.Max(0f, s[r]) / s0), halfAlpha);
            for (int j = 0; j < d; j++)
            {
                left[(long)r * d + j] = sq * u[(long)j * d + r];
                right[(long)r * d + j] = sq * vt[(long)r * d + j];
            }
        }



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




    /// Phase 0 RoPE mitigation, second half: rotary pair 0 (head components 0 and
    /// hd/2) rotates at frequency 1 REGARDLESS of freq_base, and the factor's
    /// strongest row otherwise lands exactly there. When skipping, factor row r
    /// maps to component r+1 (or r+2 past the pair partner) so synthesized content
    /// operators never occupy the always-rotating plane. Q and K must use the
    /// SAME mapping (components pair in the dot product); V is not rotated.
    private static int RotarySafeComponent(int r, int headDim, bool skip)
        => !skip ? r : (r + 1 < headDim / 2 ? r + 1 : r + 2);

    internal static void FillHead(float[] vals, int rows, int cols, int headIdx, int headDim, Factors f, double scale, bool skipRotaryPair0 = false)
    {
        int baseRow = headIdx * headDim;
        if (baseRow >= rows) return;
        int k = Math.Min(f.Rank, skipRotaryPair0 ? headDim - 2 : headDim);
        for (int r = 0; r < k; r++)
        {
            int c = RotarySafeComponent(r, headDim, skipRotaryPair0);
            if (baseRow + c >= rows) break;
            long dst = (long)(baseRow + c) * cols;
            for (int j = 0; j < cols && j < f.Dim; j++)
                vals[dst + j] = (float)(scale * f.Left[(long)r * f.Dim + j]);
        }
    }

    internal static void FillHeadRight(float[] vals, int rows, int cols, int headIdx, int headDim, Factors f, double scale, bool skipRotaryPair0 = false)
    {
        int baseRow = headIdx * headDim;
        if (baseRow >= rows) return;
        int k = Math.Min(f.Rank, skipRotaryPair0 ? headDim - 2 : headDim);
        for (int r = 0; r < k; r++)
        {
            int c = RotarySafeComponent(r, headDim, skipRotaryPair0);
            if (baseRow + c >= rows) break;
            long dst = (long)(baseRow + c) * cols;
            for (int j = 0; j < cols && j < f.Dim; j++)
                vals[dst + j] = (float)(scale * f.Right[(long)r * f.Dim + j]);
        }
    }



    internal static void FillColsHead(float[] vals, int rows, int cols, int headIdx, int headDim, Factors f, double scale)
    {
        int baseCol = headIdx * headDim;
        if (baseCol >= cols) return;
        int k = Math.Min(f.Rank, headDim);
        for (int r = 0; r < k && (baseCol + r) < cols; r++)
            for (int i = 0; i < rows && i < f.Dim; i++)
                vals[(long)i * cols + (baseCol + r)] = (float)(scale * f.Left[(long)r * f.Dim + i]);
    }






    internal static bool IsContinuationOperator(string opKey) =>
        opKey is "context" or "trajectory" or "sentence_order" or "relation:PRECEDES";

    internal static void FillHeadZero(float[] vals, int rows, int cols, int headIdx, int headDim)
    {
        int baseRow = headIdx * headDim;
        for (int r = 0; r < headDim && (baseRow + r) < rows; r++)
        {
            long dst = (long)(baseRow + r) * cols;
            for (int j = 0; j < cols; j++) vals[dst + j] = 0f;
        }
    }
    internal static void FillHeadIdentity(float[] vals, int rows, int cols, int headIdx, int headDim)
    {
        int baseRow = headIdx * headDim;
        for (int r = 0; r < headDim && (baseRow + r) < rows; r++)
        {
            long dst = (long)(baseRow + r) * cols;
            for (int j = 0; j < cols; j++) vals[dst + j] = 0f;
            if (baseRow + r < cols) vals[dst + (baseRow + r)] = 1f;
        }
    }
    internal static void FillColsHeadIdentity(float[] vals, int rows, int cols, int headIdx, int headDim)
    {
        int baseCol = headIdx * headDim;
        for (int r = 0; r < headDim && (baseCol + r) < cols; r++)
            for (int i = 0; i < rows; i++)
                vals[(long)i * cols + (baseCol + r)] = (i == baseCol + r) ? 1f : 0f;
    }

    internal static void FillHeadIdentityScaled(float[] vals, int rows, int cols, int headIdx, int headDim, float scale)
    {
        int baseRow = headIdx * headDim;
        for (int r = 0; r < headDim && (baseRow + r) < rows; r++)
        {
            long dst = (long)(baseRow + r) * cols;
            for (int j = 0; j < cols; j++) vals[dst + j] = 0f;
            if (baseRow + r < cols) vals[dst + (baseRow + r)] = scale;
        }
    }

    /// Plan Phase 6 (doc 14 P2/M1): per-token highway masks for the content gate.
    internal static async Task<Mask256[]> FillHighwayMasksAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int vocabSize)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var key in tokenSlots.Keys) vocab[vi++] = key.ToBytes();

        var masks = new Mask256[vocabSize];
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = "SELECT entity_id, highway_mask FROM laplace.entity_highway_masks($1)";
        cmd.Parameters.Add(new NpgsqlParameter
        { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        try
        {
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr[0]), out var slots)) continue;
                var m = Mask256.FromByteArray((byte[])rdr[1]);
                foreach (int s in slots) if (s >= 0 && s < vocabSize) masks[s] = m;
            }
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            Console.WriteLine($"  (entity_highway_masks unavailable: {ex.SqlState} — banded gate falls back to constant)");
        }

        // Fallback for DB generations whose entities.highway_mask is unpopulated
        // (live finding 2026-07-08: 0 of 20.2M entities carry masks): DERIVE
        // membership from consensus participation — the OR of the relation-type
        // masks this entity has edges under, via the in-memory highway perfcache
        // (layout-safe: no SQL bit arithmetic).
        bool anyStored = false;
        foreach (var m in masks) if (!m.IsZero) { anyStored = true; break; }
        if (!anyStored && HighwayPerfcache.IsLoaded)
        {
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandTimeout = 300;
            cmd2.CommandText =
                "SELECT u.id, c.type_id FROM unnest($1::bytea[]) AS u(id) " +
                "JOIN laplace.consensus c ON c.subject_id = u.id GROUP BY u.id, c.type_id " +
                "UNION " +
                "SELECT u.id, c.type_id FROM unnest($1::bytea[]) AS u(id) " +
                "JOIN laplace.consensus c ON c.object_id = u.id GROUP BY u.id, c.type_id";
            cmd2.Parameters.Add(new NpgsqlParameter
            { Value = vocab, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            int derived = 0;
            await using var rdr2 = await cmd2.ExecuteReaderAsync();
            while (await rdr2.ReadAsync())
            {
                if (!tokenSlots.TryGetValue(FromBytes((byte[])rdr2[0]), out var slots)) continue;
                var tm = HighwayPerfcache.MaskForRelationType(FromBytes((byte[])rdr2[1]));
                if (tm.IsZero) continue;
                foreach (int s in slots)
                    if (s >= 0 && s < vocabSize) { masks[s] |= tm; derived++; }
            }
            Console.WriteLine($"  highway masks: derived from consensus participation ({derived:N0} entity-type memberships; stored masks empty)");
        }
        return masks;
    }

    /// Plan Phase 6 (doc 14 P2, kills M1): gate rows keyed on per-band embedding
    /// centroids — FFN block b fires for tokens aligned with salience band b's
    /// membership centroid. silu(gain·cos + floor): aligned ≈ silu(gain) (the old
    /// constant), misaligned ≈ silu(floor) (a small leak, no dead tokens). Bands
    /// with no centroid keep the old constant-open column — strictly no worse.
    internal static void FillGateBanded(
        float[] vals, int rows, int cols, double[]?[] bandCentroids, double gateZ, double floorZ)
    {
        // gateZ/floorZ are LOGIT units. For unit-content rows with bias=1, RMSNorm
        // gives h ≈ x·sqrt(d/2), so weights scale by 1/sqrt(d/2) — the same
        // calibration the constant gateCol used. Aligned token: silu(≈gateZ·cos);
        // any token: at least silu(≈floorZ) via the bias column (no dead tokens).
        double s = 1.0 / Math.Sqrt(cols / 2.0);
        int bands = bandCentroids.Length;
        for (int r = 0; r < rows; r++)
        {
            int b = (int)((long)r * bands / rows);
            var c = bandCentroids[b];
            if (c is null)
            {
                vals[(long)r * cols + (cols - 1)] = (float)(gateZ * s); // constant-open fallback
                continue;
            }
            for (int j = 0; j < cols - 1 && j < c.Length; j++)
                vals[(long)r * cols + j] = (float)(gateZ * s * c[j]);
            vals[(long)r * cols + (cols - 1)] = (float)(floorZ * s);
        }
    }

    internal static void FillGate(float[] vals, int rows, int cols, double gateCol)
    {
        for (int r = 0; r < rows; r++)
            vals[(long)r * cols + (cols - 1)] = (float)gateCol;
    }






    internal static void FillCoordHead(float[] vals, int rows, int cols, int headDim, int coordDims, double scale)
    {
        if (headDim <= 0) return;
        int nh = rows / headDim;
        for (int h = 0; h < nh; h++)
            for (int d = 0; d < coordDims && d < headDim && d < cols; d++)
                vals[(long)(h * headDim + d) * cols + d] = (float)scale;
    }

    internal static double Silu(double z) => z / (1.0 + Math.Exp(-z));



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

    internal static unsafe Hash128 FromBytes(byte[] bts)
    {
        if (bts.Length < 16) return Hash128.Zero;
        fixed (byte* p = bts) return *(Hash128*)p;
    }
}
