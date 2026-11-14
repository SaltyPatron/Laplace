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
    internal const double BiasValue = 1.0;

    private static InvalidOperationException RequiredQueryUnavailable(string operation, PostgresException cause)
        => new($"Model export refused: required query '{operation}' is unavailable [{cause.SqlState}].", cause);

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
        // The consensus family pushes the vocab down to the (type_id, subject_id) index
        // server-side; the traj family reads unfiltered. MapWeightedEdges drops any row
        // outside tokenSlots either way.
        var vocab = VocabArray(tokenSlots);
        try
        {
            var rows = await NpgsqlFoundryReads.RelationPlaneAsync(
                ds, spec.Family, spec.Name, spec.Arg,
                spec.Family == "consensus" ? vocab : null);
            return MapWeightedEdges(tokenSlots, rows, degreeCap);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            throw RequiredQueryUnavailable($"relation_plane/{spec}", ex);
        }
    }








    internal static async Task<PlaneCoo> ReadConsensusPlaneAsync(
        NpgsqlDataSource ds, string[] relNames,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var vocab = VocabArray(tokenSlots);
        try
        {
            var rows = await NpgsqlFoundryReads.EntityRelationPlaneAsync(ds, vocab, relNames, degreeCap);
            return MapWeightedEdges(tokenSlots, rows, degreeCap);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            throw RequiredQueryUnavailable("entity_relation_plane", ex);
        }
    }






    internal static async Task<PlaneCoo> ReadLayerPlaneAsync(
        NpgsqlDataSource ds, double rankLo, double rankHi,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var vocab = VocabArray(tokenSlots);
        try
        {
            var rows = await NpgsqlFoundryReads.ConsensusLayerPlaneAsync(
                ds, vocab, rankLo, rankHi, degreeCap);
            // the layer family weights the edge by its layer rank, not by w alone
            return MapLayerEdges(tokenSlots, rows, degreeCap, static r => r.W * r.LayerRank);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            throw RequiredQueryUnavailable("consensus_layer_plane", ex);
        }
    }






    internal static async Task<PlaneCoo> ReadLayerPlaneMaskedAsync(
    NpgsqlDataSource ds, Mask256 bandMask,
    Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var vocab = VocabArray(tokenSlots);
        try
        {
            var rows = await NpgsqlFoundryReads.ConsensusLayerPlaneMaskedAsync(
                ds, vocab, bandMask.ToByteArray(), degreeCap);
            return MapLayerEdges(tokenSlots, rows, degreeCap, static r => r.W * r.LayerRank);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            throw RequiredQueryUnavailable("consensus_layer_plane_masked", ex);
        }
    }

    internal sealed record TypePlane(Hash128 TypeId, double Rank, PlaneCoo Plane);

    internal static async Task<List<TypePlane>> ReadTypePlanesAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int degreeCap,
        IReadOnlyCollection<Hash128>? typeIds = null)
    {
        var vocab = VocabArray(tokenSlots);

        // Rows carry type_id + layer_rank and fan out into one adjacency per relation type.
        var byType = new Dictionary<Hash128, (double Rank, Dictionary<int, List<(int Col, double W)>> Adj)>();
        try
        {
            byte[][]? typeFilter = typeIds is { Count: > 0 }
                ? typeIds.Select(t => t.ToBytes()).ToArray()
                : null;
            var planeRows = await NpgsqlFoundryReads.ConsensusTypePlaneAsync(
                ds, vocab, degreeCap, typeFilter);
            foreach (var row in planeRows)
            {
                if (!tokenSlots.TryGetValue(FromBytes(row.SubjectId), out var subj)) continue;
                if (!tokenSlots.TryGetValue(FromBytes(row.ObjectId), out var obj)) continue;
                double w = row.W;
                if (w == 0.0) continue;
                var tid = FromBytes(row.TypeId);
                double rank = row.LayerRank;
                if (!byType.TryGetValue(tid, out var entry))
                    byType[tid] = entry = (rank, new Dictionary<int, List<(int, double)>>());
                foreach (int s in subj)
                {
                    if (!entry.Adj.TryGetValue(s, out var rowAdj)) entry.Adj[s] = rowAdj = new List<(int, double)>(8);
                    foreach (int o in obj) rowAdj.Add((o, w));
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            throw RequiredQueryUnavailable("consensus_type_plane", ex);
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
        var vocab = VocabArray(tokenSlots);

        // The object of relationType is a category, usually outside the token vocab, so
        // the outbound read restricts only the subject. Tokens sharing a category form a
        // co-category clique built below, weighted by √(w_a·w_b) of their walk_edge_weight.
        var wordCat = new Dictionary<int, Hash128>();
        var wordMu = new Dictionary<int, double>();
        var attrRows = await NpgsqlFoundryReads.AttributeOutboundAsync(
            ds, relationType, vocab, degreeCap);
        foreach (var row in attrRows)
        {
            if (!tokenSlots.TryGetValue(FromBytes(row.SubjectId), out var subj)) continue;
            var cat = FromBytes(row.NeighbourId);
            double w = row.W;
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
                    // Trim during accumulation so a dense category never holds its full
                    // N·(N−1) clique; top-k merges, so the final row is identical.
                    if (degreeCap > 0 && row.Count >= degreeCap * 2)
                        TrimRowToTopK(row, degreeCap);
                }
            }
        }
        return CooFromAdj(adj, degreeCap);
    }

    internal static async Task<PlaneCoo> ReadAdjacencyAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var vocab = VocabArray(tokenSlots);
        var rows = await NpgsqlFoundryReads.ConsensusAdjacencyAsync(ds, vocab, degreeCap);
        return MapWeightedEdges(tokenSlots, rows, degreeCap);
    }








    internal static async Task<PlaneCoo> ReadMetricEdgesAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots,
        string metric, int k, int probe, int degreeCap)
    {
        var vocab = VocabArray(tokenSlots);
        var rows = await NpgsqlFoundryReads.MetricEdgesAsync(ds, vocab, metric, k, probe);
        // column 3 is a DISTANCE here, not a weight — decay it into an affinity
        var decayed = rows.Select(static r => r with { W = Math.Exp(-r.W) }).ToList();
        return MapWeightedEdges(tokenSlots, decayed, degreeCap);
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
        var vocab = VocabArray(tokenSlots);
        int filled = 0;
        var rows = await NpgsqlFoundryReads.EntityPhysicalityCoordsAsync(ds, vocab);
        foreach (var row in rows)
        {
            if (!tokenSlots.TryGetValue(FromBytes(row.EntityId), out var slots)) continue;
            var a = new[] { row.X, row.Y, row.Z, row.M };
            foreach (int s in slots) { anchors[s] = a; filled++; }
        }
        return filled;
    }







    /// Per-token Native Hilbert key of the entity's coordinate (most significant
    /// octet = coarsest locality), feeding the content-PE dims.
    internal static async Task<ulong?[]> FillHilbertKeysAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int vocabSize)
    {
        var vocab = VocabArray(tokenSlots);
        var keys = new ulong?[vocabSize];
        try
        {
            var rows = await NpgsqlFoundryReads.EntityHilbertKeysAsync(ds, vocab);
            foreach (var row in rows)
            {
                if (!tokenSlots.TryGetValue(FromBytes(row.EntityId), out var slots)) continue;
                foreach (int s in slots)
                    if (s >= 0 && s < vocabSize) keys[s] = row.Hilbert;
            }
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            throw RequiredQueryUnavailable("entity_hilbert_keys", ex);
        }
        return keys;
    }



    internal static async Task<PlaneCoo[]> ReadTrajectoryLadderAsync(
        NpgsqlDataSource ds, int maxGap,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        var vocab = VocabArray(tokenSlots);
        var adj = new Dictionary<int, List<(int Col, double W)>>[maxGap];
        for (int g = 0; g < maxGap; g++) adj[g] = new Dictionary<int, List<(int, double)>>();
        try
        {
            var rows = await NpgsqlFoundryReads.TrajectoryPlaneAsync(ds, vocab, maxGap, degreeCap);
            foreach (var row in rows)
            {
                int g = row.Gap;
                if (g < 1 || g > maxGap) continue;
                if (!tokenSlots.TryGetValue(FromBytes(row.SubjectId), out var subj)) continue;
                if (!tokenSlots.TryGetValue(FromBytes(row.ObjectId), out var obj)) continue;
                double w = row.W;
                if (w == 0.0) continue;
                var a = adj[g - 1];
                foreach (int s in subj)
                {
                    if (!a.TryGetValue(s, out var rowAdj)) a[s] = rowAdj = new List<(int, double)>(8);
                    foreach (int o in obj) rowAdj.Add((o, w));
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            throw RequiredQueryUnavailable("trajectory_plane", ex);
        }
        var planes = new PlaneCoo[maxGap];
        for (int g = 0; g < maxGap; g++) planes[g] = CooFromAdj(adj[g], degreeCap);
        return planes;
    }







    internal static async Task<PlaneCoo> ReadGraphemeOrderAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int gap = 1)
    {
        var vocab = VocabArray(tokenSlots);
        var rows = await NpgsqlFoundryReads.GraphemeOrderAsync(ds, vocab, gap);
        return MapWeightedEdges(tokenSlots, rows, degreeCap: 256, dropNonPositive: true);
    }







    internal static async Task<PlaneCoo> ReadWordOrderAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots,
        int gap = 1, int trajs = 200000, int cap = 64)
    {
        var vocab = VocabArray(tokenSlots);
        var rows = await NpgsqlFoundryReads.WordOrderAsync(ds, vocab, trajs, gap);
        return MapWeightedEdges(tokenSlots, rows, degreeCap: cap, dropNonPositive: true);
    }









    /// Smoothed log-conditional continuation table log P̂(y|x). Returns the top-cap
    /// entries per row plus each row's unseen default (the NULL-object rows); absent
    /// pairs densify to that default, not zero, since zero is log 1.

    internal static async Task<(PlaneCoo Plane, double[] RowDefault)> ReadConditionalPlaneAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int vocabSize,
        int trajs = 200000, double smoothK = 0.5, int cap = 512)
    {
        var vocab = VocabArray(tokenSlots);
        var adj = new Dictionary<int, List<(int Col, double W)>>();
        var rowDefault = new double[vocabSize];
        Array.Fill(rowDefault, double.NaN);
        var rows = await NpgsqlFoundryReads.ContinuationConditionalPlaneAsync(
            ds, vocab, trajs, smoothK, cap);
        foreach (var row in rows)
        {
            if (!tokenSlots.TryGetValue(FromBytes(row.SubjectId), out var subj)) continue;
            double w = row.W;
            if (row.ObjectId is null)
            {
                foreach (int s in subj) if (s >= 0 && s < vocabSize) rowDefault[s] = w;
                continue;
            }
            if (!tokenSlots.TryGetValue(FromBytes(row.ObjectId), out var obj)) continue;
            foreach (int s in subj)
            {
                if (!adj.TryGetValue(s, out var rowAdj)) adj[s] = rowAdj = new List<(int, double)>(16);
                foreach (int o in obj) rowAdj.Add((o, w));
            }
        }
        return (CooFromAdj(adj, cap), rowDefault);
    }


    /// Dominant POS class per vocab token plus the class-transition log-conditional
    /// table T; the conditional_pos floor adds gain·T[class(x),class(y)] to unseen cells.
    /// Missing class pairs take the table's minimum.

    internal static async Task<(int[] TokenClass, double[,] T, int NClasses)> ReadPosCorrectionAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int vocabSize)
    {
        var vocab = VocabArray(tokenSlots);
        var tokenClass = new int[vocabSize];
        Array.Fill(tokenClass, -1);
        var classIndex = new Dictionary<Hash128, int>();
        var posRows = await NpgsqlFoundryReads.VocabDominantPosAsync(ds, vocab);
        foreach (var row in posRows)
        {
            if (!tokenSlots.TryGetValue(FromBytes(row.WordId), out var slots)) continue;
            var pos = FromBytes(row.PosId);
            if (!classIndex.TryGetValue(pos, out int ci)) classIndex[pos] = ci = classIndex.Count;
            foreach (int s in slots)
                if (s >= 0 && s < vocabSize) tokenClass[s] = ci;
        }
        var entries = await NpgsqlFoundryReads.PosClassTransitionsAsync(ds, vocab);
        int nc = classIndex.Count;
        double floor = entries.Count > 0 ? entries.Min(e => e.W) : 0.0;
        var table = new double[nc, nc];
        for (int i = 0; i < nc; i++)
            for (int j = 0; j < nc; j++) table[i, j] = floor;
        foreach (var e in entries)
        {
            var px = FromBytes(e.SubjectId);
            var py = FromBytes(e.ObjectId);
            if (classIndex.TryGetValue(px, out int ci) && classIndex.TryGetValue(py, out int cj))
                table[ci, cj] = e.W;
        }
        return (tokenClass, table, nc);
    }


    /// Sentence-boundary word bridge: last word of sentence i → first word of
    /// sentence i+1, read from document trajectories. Non-positive weights are dropped.

    internal static async Task<PlaneCoo> ReadSentenceOrderAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots,
        int docs = 100000, int cap = 64)
    {
        var vocab = VocabArray(tokenSlots);
        try
        {
            var rows = await NpgsqlFoundryReads.SentenceOrderWordBridgeAsync(ds, vocab, docs);
            return MapWeightedEdges(tokenSlots, rows, degreeCap: cap, dropNonPositive: true);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            throw RequiredQueryUnavailable("sentence_order_word_bridge", ex);
        }
    }


    internal static async Task<PlaneCoo> ReadTrajectoryStrideAsync(
        NpgsqlDataSource ds, int maxGap,
        Dictionary<Hash128, List<int>> tokenSlots, int degreeCap)
    {
        int corpusMax = FoundryDefaults.CorpusMax;
        int trajCap = corpusMax > 0 ? corpusMax : 200_000;
        var vocab = VocabArray(tokenSlots);

        // Same read as ReadWordOrderAsync, but this path warms the relation-type lookup,
        // bounds the corpus, uses a 600 s timeout,
        // and PPMI-reweights when FoundryDefaults.Ppmi is set.
        try
        {
            await using var conn = await ds.OpenConnectionAsync();
            await NpgsqlFoundryReads.WarmRelationTypeIdAsync(conn, "IS_A");

            var rows = await NpgsqlFoundryReads.WordOrderAsync(
                ds, vocab, trajCap, maxGap, timeoutSeconds: 600);
            return MapWeightedEdges(
                tokenSlots, rows, degreeCap,
                transform: FoundryDefaults.Ppmi ? ApplyPpmi : null);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            throw RequiredQueryUnavailable("word_order", ex);
        }
    }



    internal static void ApplyPpmi(Dictionary<int, List<(int Col, double W)>> adj)
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




    /// The vocab pushdown array: tokenSlots' keys as a bytea[], in Keys order.
    /// Every reader that probes a server-side index by vocab builds this same array.
    private static byte[][] VocabArray(Dictionary<Hash128, List<int>> tokenSlots)
    {
        var vocab = new byte[tokenSlots.Count][];
        int vi = 0;
        foreach (var k in tokenSlots.Keys) vocab[vi++] = k.ToBytes();
        return vocab;
    }

    /// A bytea[] bind. Value is `object` because several readers pass DBNull to mean
    /// "no vocab pushdown" and Npgsql needs the declared type either way.
    private static NpgsqlParameter ByteaArrayParam(object value) =>
        new() { Value = value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea };

    private static PlaneCoo MapWeightedEdges(
        Dictionary<Hash128, List<int>> tokenSlots,
        IReadOnlyList<NpgsqlFoundryReads.WeightedEdgeRow> rows,
        int degreeCap,
        bool dropNonPositive = false,
        Action<Dictionary<int, List<(int Col, double W)>>>? transform = null)
    {
        var adj = new Dictionary<int, List<(int Col, double W)>>();
        foreach (var edge in rows)
        {
            if (!tokenSlots.TryGetValue(FromBytes(edge.SubjectId), out var subj)) continue;
            if (!tokenSlots.TryGetValue(FromBytes(edge.ObjectId), out var obj)) continue;
            double w = edge.W;
            if (dropNonPositive ? w <= 0 : w == 0.0) continue;
            foreach (int s in subj)
            {
                if (!adj.TryGetValue(s, out var row)) adj[s] = row = new List<(int, double)>(8);
                foreach (int o in obj) row.Add((o, w));
            }
        }
        transform?.Invoke(adj);
        return CooFromAdj(adj, degreeCap);
    }

}
