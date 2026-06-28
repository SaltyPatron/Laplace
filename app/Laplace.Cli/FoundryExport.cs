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

    internal static int EnvInt(string name, int dflt) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;

    internal static double EnvDouble(string name, double dflt) =>
        double.TryParse(Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : dflt;

    internal static double EnvDoubleOr(string name, double ifUnset) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } raw
        && double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : ifUnset;

    
    
    
    
    
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

    // word→category relations (HAS_POS, HAS_SENSE, …) compile to a vocab×vocab plane: tokens that share
    // the same category object attend to each other. The category entity is not in the recipe vocab, so
    // entity_relation_plane / consensus_type_plane cannot surface these edges as word↔word heads.
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
        cmd.CommandText = @"SELECT DISTINCT ON (p.entity_id) p.entity_id,
                ST_X(p.coord), ST_Y(p.coord), ST_Z(p.coord), ST_M(p.coord)
            FROM laplace.physicalities p
            JOIN unnest($1::bytea[]) AS u(id) ON u.id = p.entity_id
            WHERE p.type = 1 AND p.coord IS NOT NULL
            ORDER BY p.entity_id, p.id";
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

        
        int corpusMax = EnvInt("LAPLACE_FOUNDRY_CORPUS_MAX", 200_000);
        if (corpusMax > 0)
        {
            await using var setCmd = conn.CreateCommand();
            setCmd.CommandText = $"SET laplace_substrate.corpus_max_rows = {corpusMax}";
            await setCmd.ExecuteNonQueryAsync();
        }
        try
        {
            
            
            // Direct, vocab-scoped continuation read from content trajectories — NO global
            // trajectory_pairs cache, NO lazy rebuild / staleness probe / maintenance. word_order
            // scans a bounded slice of trajectories and emits gap-ordered intra-vocab pairs.
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

        // The content trajectories ARE the knowledge (real observed usage). Raw counts preserve the
        // actual usage flow (incl. the grammatical glue that genuinely follows words); PPMI instead
        // surfaces context-specific associations by normalizing out each target's global frequency.
        // Toggle: LAPLACE_FOUNDRY_PPMI=0 keeps the raw usage flow (fluent continuation), =1 (default) PPMI.
        if (EnvInt("LAPLACE_FOUNDRY_PPMI", 1) != 0) ApplyPpmi(adj);
        return CooFromAdj(adj, degreeCap);
    }

    // Positive pointwise mutual information over a continuation adjacency.
    // PMI(s,o) = log( count(s,o)·N / (rowSum[s]·colSum[o]) ); keep only positive (real) associations.
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
        int k = Math.Min(dModel - 1, Math.Min(EnvInt("LAPLACE_FOUNDRY_BASIS_RANK", 256), vocab));
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

        embed  = new double[(long)vocab * dim];
        lmHead = new double[(long)vocab * dim];

        
        
        
        
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

    
    
    
    
    
    internal static int FactorSparseRandomized(
        int[] sx, int[] sy, double[] sv, int sn, int vocab, int dim, double[] embed, double[] lmHead)
    {
        int L = Math.Min(vocab, dim + EnvInt("LAPLACE_FOUNDRY_RSVD_OVERSAMPLE", 16));
        int q = EnvInt("LAPLACE_FOUNDRY_RSVD_POWER", 1);
        
        var Y  = new double[(long)L * vocab];
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
                embed[(long)x * dim + c]  = acc;
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
        bool coordDirect = EnvInt("LAPLACE_FOUNDRY_COORD_DIRECT", 0) != 0;
        if (coordOnly)
        {
            
        }
        else if (coordDirect && fitIdx.Count > 0)
        {
            
            
            
            
            
            
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

        
        
        
        
        
        
        double capFrac = EnvDouble("LAPLACE_FOUNDRY_CAP_FRAC", 0.05);
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

    // Per-head fill: head `headIdx` occupies rows [headIdx*headDim, (headIdx+1)*headDim) and is
    // filled from ITS OWN operator factor — one distinct attestation-type/metric operator per head
    // (Mold-A-Model), not top-k of one mashed operator tiled across every head.
    internal static void FillHead(float[] vals, int rows, int cols, int headIdx, int headDim, Factors f, double scale)
    {
        int baseRow = headIdx * headDim;
        if (baseRow >= rows) return;
        int k = Math.Min(f.Rank, headDim);
        for (int r = 0; r < k && (baseRow + r) < rows; r++)
        {
            long dst = (long)(baseRow + r) * cols;
            for (int j = 0; j < cols && j < f.Dim; j++)
                vals[dst + j] = (float)(scale * f.Left[(long)r * f.Dim + j]);
        }
    }

    internal static void FillHeadRight(float[] vals, int rows, int cols, int headIdx, int headDim, Factors f, double scale)
    {
        int baseRow = headIdx * headDim;
        if (baseRow >= rows) return;
        int k = Math.Min(f.Rank, headDim);
        for (int r = 0; r < k && (baseRow + r) < rows; r++)
        {
            long dst = (long)(baseRow + r) * cols;
            for (int j = 0; j < cols && j < f.Dim; j++)
                vals[dst + j] = (float)(scale * f.Right[(long)r * f.Dim + j]);
        }
    }

    // Per-head column fill for o_proj [dModel, nHeads*headDim]: head h's output occupies columns
    // [h*headDim, (h+1)*headDim) and is projected back by ITS OWN operator (the OV factor).
    internal static void FillColsHead(float[] vals, int rows, int cols, int headIdx, int headDim, Factors f, double scale)
    {
        int baseCol = headIdx * headDim;
        if (baseCol >= cols) return;
        int k = Math.Min(f.Rank, headDim);
        for (int r = 0; r < k && (baseCol + r) < cols; r++)
            for (int i = 0; i < rows && i < f.Dim; i++)
                vals[(long)i * cols + (baseCol + r)] = (float)(scale * f.Left[(long)r * f.Dim + i]);
    }

    // ---- Context/sequence head (op:"context") ----
    // q=k=identity (scaled) on the head slice → RoPE + causal mask peak attention on the CURRENT
    // token (recency), not a uniform prefix mean. v=o=identity passes that token's slice back into
    // the residual so h[last] ≈ E[last] at lm_head readout — the prefix-conditioning bridge for
    // source-normalized trajectory bigram. (Uniform q=k=0 was the prefix-mean bug → global attractor.)
    internal static bool IsContinuationOperator(string opKey) =>
        opKey is "context" or "trajectory" or "relation:PRECEDES";

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
            if (baseRow + r < cols) vals[dst + (baseRow + r)] = 1f;   // V = the head's slice of the input
        }
    }
    internal static void FillColsHeadIdentity(float[] vals, int rows, int cols, int headIdx, int headDim)
    {
        int baseCol = headIdx * headDim;
        for (int r = 0; r < headDim && (baseCol + r) < cols; r++)
            for (int i = 0; i < rows; i++)
                vals[(long)i * cols + (baseCol + r)] = (i == baseCol + r) ? 1f : 0f;   // O maps the slice back
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
