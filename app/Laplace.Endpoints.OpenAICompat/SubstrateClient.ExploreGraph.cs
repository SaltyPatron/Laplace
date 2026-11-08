using System.Globalization;
using Npgsql;
using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.Engine.Dynamics;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{
    public async Task<ExploreGraphResponse?> ExploreConsensusGraphAsync(
        string idHex, int hops, int fanout, int maxNodes, CancellationToken ct)
    {
        var seed = TryParseIdHex(idHex);
        if (seed is null) return null;

        hops = Math.Max(0, hops);
        fanout = Math.Max(0, fanout);
        maxNodes = Math.Max(0, maxNodes);

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var (label, tier, _, _) = await ReadEntityFacetsAsync(conn, seed, ct);
            if (label is null) return null;

            var seedHex = idHex.ToLowerInvariant();
            if (maxNodes == 0)
                return new ExploreGraphResponse(seedHex, label, hops, fanout, [], [], true, 0);

            var nodes = new Dictionary<string, ExploreGraphNode>(StringComparer.OrdinalIgnoreCase)
            {
                [seedHex] = new ExploreGraphNode(seedHex, label, 0, tier),
            };

            void AdmitNode(string hex, int candidateHop)
            {
                if (!nodes.TryGetValue(hex, out var node))
                {
                    nodes[hex] = new ExploreGraphNode(hex, hex, candidateHop, null);
                    return;
                }
                if (candidateHop < node.Hop)
                    nodes[hex] = node with { Hop = candidateHop };
            }

            // Consensus-ranked crawl selects the bounded vertex set.
            var discovery = await NpgsqlSubstrateReads.ExploreWebAsync(
                conn, seed, hops, fanout, maxNodes,
                Math.Max(SubstrateClient.DefaultCommandTimeoutSeconds, 60), ct);
            foreach (var row in discovery)
            {
                AdmitNode(row.SourceIdHex.ToLowerInvariant(), row.Hop);
                AdmitNode(row.ObjectIdHex.ToLowerInvariant(), row.Hop);
            }

            // The crawl is a tree; the induced edges among the selected vertices restore
            // cross-links and cycles before projection.
            var nodeIds = nodes.Keys
                .OrderBy(static x => x, StringComparer.Ordinal)
                .Select(Convert.FromHexString)
                .ToArray();
            var induced = await NpgsqlSubstrateReads.ExploreInducedEdgesAsync(
                conn, nodeIds, Math.Max(SubstrateClient.DefaultCommandTimeoutSeconds, 60), ct);

            var edges = new List<ExploreGraphEdge>(induced.Count);
            var edgeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var typeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in induced)
            {
                var sourceHex = row.SourceIdHex.ToLowerInvariant();
                var typeHex = row.TypeIdHex.ToLowerInvariant();
                var objectHex = row.ObjectIdHex.ToLowerInvariant();
                if (!nodes.ContainsKey(sourceHex) || !nodes.ContainsKey(objectHex) || sourceHex == objectHex)
                    continue;
                if (!edgeKeys.Add($"{sourceHex}|{typeHex}|{objectHex}")) continue;

                typeIds.Add(typeHex);
                edges.Add(new ExploreGraphEdge(
                    SourceIdHex: sourceHex,
                    TargetIdHex: objectHex,
                    Type: typeHex,
                    EffMu: row.EffMu,
                    Witnesses: row.WitnessCount,
                    Hop: Math.Max(nodes[sourceHex].Hop, nodes[objectHex].Hop),
                    CompleteWeight: row.CompleteWeight,
                    Refuted: row.Refuted,
                    Rating: row.Rating,
                    Rd: row.Rd,
                    Volatility: row.Volatility));
            }

            var idsToLabel = nodes.Keys
                .Where(hex => !hex.Equals(seedHex, StringComparison.OrdinalIgnoreCase))
                .Concat(typeIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (idsToLabel.Count > 0)
            {
                var labels = await ReadDisplayLabelsAsync(conn, idsToLabel, ct);
                foreach (var (hex, entry) in labels)
                    if (nodes.TryGetValue(hex, out var node))
                        nodes[hex] = node with
                        {
                            Label = TrimGraphLabel(entry.Label, hex),
                            Tier = node.Tier ?? entry.Tier,
                        };

                for (var i = 0; i < edges.Count; i++)
                {
                    var edge = edges[i];
                    if (labels.TryGetValue(edge.Type, out var typeLabel))
                        edges[i] = edge with { Type = TrimGraphLabel(typeLabel.Label, edge.Type) };
                }
            }

            // Native normalized-Laplacian eigenmap. Only positive, unrefuted standing
            // is affinity; refuted edges remain in the response but add no weight.
            var belief = BuildBeliefProjection(nodes.Values, edges);
            if (belief is not null)
                foreach (var (hex, xyz) in belief)
                    if (nodes.TryGetValue(hex, out var node))
                        nodes[hex] = node with
                        {
                            BeliefX = xyz.X,
                            BeliefY = xyz.Y,
                            BeliefZ = xyz.Z,
                        };

            return new ExploreGraphResponse(
                IdHex: seedHex,
                Label: label,
                Hops: hops,
                Fanout: fanout,
                Nodes: nodes.Values.OrderBy(n => n.Hop).ThenBy(n => n.Label).ToList(),
                Edges: edges,
                Truncated: nodes.Count >= maxNodes,
                MaxNodes: maxNodes);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            throw new SubstrateUnavailableException(
                $"Explore consensus graph query failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    private static unsafe Dictionary<string, (double X, double Y, double Z)>? BuildBeliefProjection(
        IReadOnlyCollection<ExploreGraphNode> nodes,
        IReadOnlyList<ExploreGraphEdge> edges)
    {
        if (nodes.Count < 3) return null;

        var ordered = nodes.OrderBy(static n => n.IdHex, StringComparer.Ordinal).ToArray();
        var ordinal = new Dictionary<string, int>(ordered.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ordered.Length; i++) ordinal[ordered[i].IdHex] = i;

        var positive = edges
            .Where(e => e.CompleteWeight > 0.0 && !e.Refuted &&
                        double.IsFinite(e.CompleteWeight) &&
                        ordinal.ContainsKey(e.SourceIdHex) &&
                        ordinal.ContainsKey(e.TargetIdHex) &&
                        !e.SourceIdHex.Equals(e.TargetIdHex, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (positive.Length == 0) return null;

        var rows = new int[positive.Length];
        var cols = new int[positive.Length];
        var weights = new double[positive.Length];
        var degree = new double[ordered.Length];
        for (var i = 0; i < positive.Length; i++)
        {
            var row = ordinal[positive[i].SourceIdHex];
            var col = ordinal[positive[i].TargetIdHex];
            var weight = positive[i].CompleteWeight;
            rows[i] = row;
            cols[i] = col;
            weights[i] = weight;
            // The native eigenmap symmetrizes W, so degree counts both endpoints.
            degree[row] += weight;
            degree[col] += weight;
        }

        var dimensions = Math.Min(3, ordered.Length - 2);
        if (dimensions <= 0) return null;
        var projected = new double[checked(ordered.Length * dimensions)];

        int rc;
        fixed (int* pr = rows)
        fixed (int* pc = cols)
        fixed (double* pw = weights)
        fixed (double* po = projected)
            rc = NativeInterop.LaplacianEigenmapsFromSparseGraph(
                pr, pc, pw, (nuint)weights.Length, (nuint)ordered.Length,
                (nuint)dimensions, po);
        if (rc != 0) return null;

        var result = new Dictionary<string, (double X, double Y, double Z)>(
            ordered.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ordered.Length; i++)
        {
            // A vertex with no positive conductance gets no belief coordinate rather
            // than the spectral origin.
            if (!(degree[i] > 0.0)) continue;

            var x = projected[i * dimensions];
            var y = dimensions > 1 ? projected[i * dimensions + 1] : 0.0;
            var z = dimensions > 2 ? projected[i * dimensions + 2] : 0.0;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                return null;
            result[ordered[i].IdHex] = (x, y, z);
        }
        return result;
    }

    private static async Task<Dictionary<string, (string Label, short? Tier)>> ReadDisplayLabelsAsync(
        NpgsqlConnection conn, IReadOnlyList<string> idHexes, CancellationToken ct)
    {
        var result = new Dictionary<string, (string Label, short? Tier)>(StringComparer.OrdinalIgnoreCase);
        if (idHexes.Count == 0) return result;

        var ids = new List<byte[]>(idHexes.Count);
        foreach (var idHex in idHexes)
        {
            var parsed = TryParseIdHex(idHex);
            if (parsed is not null) ids.Add(parsed);
        }

        if (ids.Count == 0) return result;
        foreach (var row in await NpgsqlDisplayLabels.ReadAsync(conn, ids.ToArray(), ct))
        {
            var hex = row.IdHex.ToLowerInvariant();
            result[hex] = (row.Label, row.Tier);
        }

        return result;
    }

    private static bool IsGenericUnrealizedLabel(string? label) =>
        string.Equals(label?.Trim(), "Unrealized entity", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(label?.Trim(), "Unresolved entity", StringComparison.OrdinalIgnoreCase);

    private static string IdentityDisplayLabel(string? idHex, string? type = null)
    {
        var prefix = string.IsNullOrWhiteSpace(type) ? "Entity" : type.Trim();
        if (string.IsNullOrWhiteSpace(idHex)) return prefix;
        var normalized = idHex.Trim().ToLowerInvariant();
        var shortId = normalized[..Math.Min(12, normalized.Length)];
        return $"{prefix} · {shortId}";
    }

    private static string PreferredDisplayLabel(string? label, string? idHex, string? type = null)
    {
        if (!string.IsNullOrWhiteSpace(label) && !IsGenericUnrealizedLabel(label))
            return label.Trim();
        return IdentityDisplayLabel(idHex, type);
    }

    private static string DisplayLabel(
        IReadOnlyDictionary<string, (string Label, short? Tier)> labels,
        string? idHex,
        string? fallback)
    {
        if (idHex is not null && labels.TryGetValue(idHex, out var found))
            return PreferredDisplayLabel(found.Label, idHex);
        if (!string.IsNullOrWhiteSpace(fallback) &&
            !LooksLikeEntityHex(fallback) &&
            !IsGenericUnrealizedLabel(fallback))
            return fallback.Trim();
        return IdentityDisplayLabel(idHex);
    }

    private static HashSet<string> AmbiguousDisplayLabels<T>(
        IEnumerable<T> rows,
        Func<T, string> label,
        Func<T, string> id)
    {
        // Labels compare exactly: "not" and "Not" are different text and tell their entities
        // apart already; only the same text naming different entities needs an identity.
        return rows
            .GroupBy(label, StringComparer.Ordinal)
            .Where(g => g.Select(id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string DisambiguateDisplayLabel(
        string label, string idHex, IReadOnlySet<string> ambiguous)
    {
        if (!ambiguous.Contains(label)) return label;
        var normalized = idHex.Trim().ToLowerInvariant();
        var shortId = normalized[..Math.Min(12, normalized.Length)];
        return $"{label} · {shortId}";
    }

    private static string TrimGraphLabel(string label, string? idHex = null)
    {
        // Collapses whitespace and truncates at 48 text elements, on grapheme
        // boundaries so combining sequences stay whole.
        label = string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (label.Length == 0) return IdentityDisplayLabel(idHex);

        var starts = StringInfo.ParseCombiningCharacters(label);
        if (starts.Length <= 48) return label;
        return label[..starts[47]] + "…";
    }

    private static byte[]? TryParseIdHex(string idHex)
    {
        if (string.IsNullOrWhiteSpace(idHex) || idHex.Length != 32) return null;
        try { return Convert.FromHexString(idHex); }
        catch (FormatException) { return null; }
    }

    private static async Task<string?> ReadLabelAsync(NpgsqlConnection conn, byte[] id, CancellationToken ct)
        => (await NpgsqlDisplayLabels.ReadOneAsync(conn, id, ct))?.Label;

    /// <summary>
    /// Reads tier, type, and existence without realizing the entity body; the label comes
    /// from the bounded display-label read, so a high-tier composition is not reconstructed
    /// to name it.
    /// </summary>
    private static async Task<(string Label, short? Tier, string? Type, bool Exists)> ReadEntityFacetsAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        var display = await NpgsqlDisplayLabels.ReadOneAsync(conn, id, ct);
        var facet = await NpgsqlDisplayLabels.FacetAsync(conn, id, ct);
        var idHex = Convert.ToHexStringLower(id);
        if (display is null && facet is null) return (IdentityDisplayLabel(idHex), null, null, false);

        return facet is { } f
            ? (PreferredDisplayLabel(display?.Label, idHex, f.Type), f.Tier, f.Type, f.Exists)
            : (PreferredDisplayLabel(display?.Label, idHex), display?.Tier, null, false);
    }

    private static async Task<long> ReadEvidenceCountAsync(NpgsqlConnection conn, byte[] id, CancellationToken ct)
        => await NpgsqlSubstrateReads.EvidenceCountAsync(conn, id, ct) ?? 0L;

    private static async Task<IReadOnlyList<SalientFactRow>> ReadSalientFactsAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct)
    {
        var facts = await NpgsqlSubstrateReads.SalientFactsAsync(conn, id, limit, ct);
        return [.. facts.Select(f => new SalientFactRow(f.Type, f.Fact, f.EffMu, f.Witnesses))];
    }

    private static async Task<IReadOnlyList<ExplorePhysicalityRow>> ReadPhysicalitiesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.EntityPhysicalitiesAsync(conn, id, ct);
        return [.. rows.Select(p => new ExplorePhysicalityRow(
            Type: p.Type, X: p.X, Y: p.Y, Z: p.Z, M: p.M,
            Radius: p.Radius, Constituents: p.Constituents))];
    }

    private static async Task<IReadOnlyList<ExploreConsensusRow>> ReadConsensusAsync(
        NpgsqlConnection conn, byte[] id, string direction, int limit, CancellationToken ct)
    {
        var rows = new List<ExploreConsensusRow>(limit);
        if (direction == "out")
        {
            foreach (var c in await NpgsqlSubstrateReads.ConsensusOutLabeledAsync(conn, id, limit, ct))
            {
                rows.Add(new ExploreConsensusRow(
                    Direction: "out",
                    Type: c.TypeLabel,
                    EntityIdHex: c.ObjectIdHex,
                    EntityLabel: c.ObjectLabel,
                    EffMu: c.EffMu,
                    Witnesses: c.Witnesses));
            }
        }
        else
        {
            foreach (var c in await NpgsqlSubstrateReads.ConsensusInLabeledAsync(conn, id, limit, ct))
            {
                rows.Add(new ExploreConsensusRow(
                    Direction: "in",
                    Type: c.TypeLabel,
                    EntityIdHex: c.SubjectIdHex,
                    EntityLabel: c.SubjectLabel,
                    EffMu: c.EffMu,
                    Witnesses: c.Witnesses));
            }
        }

        if (rows.Count == 0)
            return rows;

        var needsLabelLookup = rows
            .Where(r => string.IsNullOrWhiteSpace(r.EntityLabel)
                     || LooksLikeEntityHex(r.EntityLabel)
                     || IsGenericUnrealizedLabel(r.EntityLabel))
            .Select(r => r.EntityIdHex)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var labels = needsLabelLookup.Count > 0
            ? await ReadDisplayLabelsAsync(conn, needsLabelLookup, ct)
            : new Dictionary<string, (string Label, short? Tier)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            rows[i] = new ExploreConsensusRow(
                r.Direction, r.Type, r.EntityIdHex,
                DisplayLabel(labels, r.EntityIdHex, r.EntityLabel),
                r.EffMu, r.Witnesses);
        }

        var ambiguous = AmbiguousDisplayLabels(
            rows, r => r.EntityLabel, r => r.EntityIdHex);
        if (ambiguous.Count > 0)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                rows[i] = r with
                {
                    EntityLabel = DisambiguateDisplayLabel(
                        r.EntityLabel, r.EntityIdHex, ambiguous)
                };
            }
        }

        return rows;
    }

    private static async Task<IReadOnlyList<SalientFactRow>> ReadPreviewFactsAsync(NpgsqlConnection conn,byte[] id,CancellationToken ct)
    {
        var senses=await ReadSensesAsync(conn,id,ct);
        var meanings=senses.Where(s=>IsReadableLexiconValue(s.SynsetLabel,null)).Take(3)
            .Select(s=>new SalientFactRow("meaning",s.SynsetLabel,s.EffMu,s.Witnesses)).ToArray();
        return meanings.Length>0?meanings:(await ReadSalientFactsAsync(conn,id,12,ct))
            .Where(f=>IsReadableLexiconValue(f.Type,null)&&IsReadableLexiconValue(f.Fact,null)).Take(3).ToArray();
    }

    private static async Task<IReadOnlyList<ExploreSenseRow>> ReadSensesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.SensesAsync(conn, id, ct);
        var result = rows.Select(s => new ExploreSenseRow(
            SenseIdHex: s.SenseIdHex,
            SynsetIdHex: s.SynsetIdHex,
            SynsetLabel: s.SynsetLabel,
            EffMu: s.EffMu,
            Witnesses: s.Witnesses)).ToList();
        var ambiguous = AmbiguousDisplayLabels(
            result, r => r.SynsetLabel, r => r.SynsetIdHex);
        return [.. result.Select(r => ambiguous.Count == 0 ? r : r with
        {
            SynsetLabel = DisambiguateDisplayLabel(
                r.SynsetLabel, r.SynsetIdHex, ambiguous)
        })];
    }

    private static async Task<IReadOnlyList<ExploreConstituentRow>> ReadConstituentsAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.ConstituentsAsync(conn, id, ct);
        return [.. rows.Select(c => new ExploreConstituentRow(
            Ordinal: c.Ordinal,
            ChildIdHex: c.ChildIdHex,
            ChildLabel: c.ChildLabel,
            RunLength: c.RunLength,
            Flags: c.Flags))];
    }

    private static async Task<IReadOnlyList<ExplorePackedVertexRow>> ReadPackedVerticesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.PackedTrajectoryVerticesAsync(conn, id, ct);
        return [.. rows.Select(v => new ExplorePackedVertexRow(
            Ordinal: v.Ordinal, X: v.X, Y: v.Y, Z: v.Z, M: v.M,
            ChildIdHex: v.ChildIdHex, RunLength: v.RunLength, Flags: v.Flags))];
    }

    private static async Task<IReadOnlyList<ExploreRealizedVertexRow>> ReadRealizedVerticesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.RealizedTrajectoryVerticesAsync(conn, id, ct);
        return [.. rows.Select(v => new ExploreRealizedVertexRow(
            Ordinal: v.Ordinal, X: v.X, Y: v.Y, Z: v.Z, M: v.M,
            ChildIdHex: v.ChildIdHex, ChildLabel: v.ChildLabel, Radius: v.Radius))];
    }

    private static async Task<IReadOnlyList<LabeledEvidenceItem>> ReadEvidenceItemsAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.EvidenceSeriesReceiptAsync(conn, id, limit, false, ct);
        var result = rows.Select(e => new LabeledEvidenceItem(
            TypeId: e.TypeId,
            TypeLabel: e.TypeLabel,
            ObjectId: e.ObjectId,
            ObjectLabel: e.ObjectLabel,
            SourceId: e.SourceId,
            SourceLabel: e.SourceLabel,
            ContextId: null,
            Outcome: null,
            ObservationCount: null,
            EffMu: e.EffMu,
            WitnessCount: 1,
            ClaimId: e.ClaimId,
            Games: e.Games, Tokens: e.Tokens, Score: e.Score, Trust: e.Trust,
            DependenceRoot: e.DependenceRoot, Position: e.Position)).ToList();
        var ambiguous = AmbiguousDisplayLabels(
            result, r => r.ObjectLabel, r => r.ObjectId);
        return [.. result.Select(r => ambiguous.Count == 0 ? r : r with
        {
            ObjectLabel = DisambiguateDisplayLabel(
                r.ObjectLabel, r.ObjectId, ambiguous)
        })];
    }
}
