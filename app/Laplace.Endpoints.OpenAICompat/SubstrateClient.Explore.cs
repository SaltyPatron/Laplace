using System.Globalization;
using Npgsql;
using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.Engine.Dynamics;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{

    public Task<string?> PerfcacheReceiptHexAsync(CancellationToken ct) =>
        NpgsqlSubstrateReads.PerfcacheReceiptHexAsync(
            _dataSource, ct, TranslateReadError);

    private static readonly WitnessCatalog WitnessCatalog = WitnessCatalog.Load();

    // The catalog counts the whole admitted world and does not vary by tenant. One
    // load fills it under _catalogGate and every caller reads it for the TTL.
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private ExploreCatalogResponse? _catalogCache;
    private DateTimeOffset _catalogCachedAt;

    public async Task<ExploreCatalogResponse> ExploreCatalogAsync(CancellationToken ct)
    {
        var cached = _catalogCache;
        if (cached is not null && DateTimeOffset.UtcNow - _catalogCachedAt < CatalogTtl)
            return cached;

        if (cached is not null)
        {
            // Past the TTL the stale catalog is returned at once and a single
            // background load refreshes it.
            if (_catalogGate.Wait(0))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _catalogCache = await LoadCatalogAsync(CancellationToken.None);
                        _catalogCachedAt = DateTimeOffset.UtcNow;
                    }
                    catch
                    {
                        // The stale catalog stays; the next expiry retries.
                    }
                    finally
                    {
                        _catalogGate.Release();
                    }
                });
            }

            return cached;
        }

        await _catalogGate.WaitAsync(ct);
        try
        {
            if (_catalogCache is { } refilled && DateTimeOffset.UtcNow - _catalogCachedAt < CatalogTtl)
                return refilled;

            var response = await LoadCatalogAsync(ct);
            _catalogCache = response;
            _catalogCachedAt = DateTimeOffset.UtcNow;
            return response;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private async Task<ExploreCatalogResponse> LoadCatalogAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            var counts = (await NpgsqlSubstrateReads.SubstrateCountsAsync(conn, ct))
                .Select(r => new SubstrateCount(r.Metric.TrimEnd(' ', '~'), r.Value))
                .ToList();

            // Planner-estimate consensus stats only; AuditReportAsync is the read that
            // attempts the exact full aggregate.
            ConsensusHealth? consensus = null;
            var approx = await NpgsqlSubstrateReads.ConsensusStatsApproxAsync(conn, ct);
            if (approx is { } s)
            {
                consensus = new ConsensusHealth(
                    EvidenceRows: s.EvidenceRows,
                    ConsensusRows: s.ConsensusRows,
                    DedupRatio: s.DedupRatio,
                    AvgWitnesses: s.AvgWitnesses,
                    MaxWitnesses: s.MaxWitnesses);
            }

            var multiSource = await TryReadMultiSourceCountAsync(conn, budgetSeconds: 5, ct);
            var topRelations = await ReadTopRelationsAsync(conn, 20, ct);

            // Per-source counts come from the bounded estimate form only. The exact
            // ops.source_counts() is an unbounded GROUP BY over attestations, and a
            // client CommandTimeout does not stop the backend from running it and
            // holding its lock. The estimate has no content count, so Content is null.
            var sources = new List<ExploreSourceRow>();
            var liveByKey = new Dictionary<string, ExploreSourceRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in await NpgsqlSubstrateReads.SourceCountsApproxAsync(
                conn, ct, timeoutSeconds: 10))
            {
                var mapped = new ExploreSourceRow(
                    Key: row.Source,
                    Evidence: row.Evidence,
                    Content: null,
                    Stage: WitnessCatalog.StageForSource(WitnessCatalog.Root, WitnessCatalog.CliForSourceKey(row.Source)),
                    Layer: null,
                    Role: null,
                    IdHex: row.IdHex);
                sources.Add(mapped);
                liveByKey[row.Source] = mapped;
            }

            var stages = WitnessCatalog.BuildStages(liveByKey);

            return new ExploreCatalogResponse(
                Counts: counts,
                Consensus: consensus,
                MultiSourceEntityCount: multiSource,
                TopRelations: topRelations,
                Sources: sources,
                Stages: stages,
                FeaturedRefs: WitnessCatalog.FeaturedRefsList());
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore catalog query failed.", ex);
        }
    }

    public async Task<ExploreResolveResponse?> ExploreResolveAsync(string reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        var requested = reference.Trim();

        // A FEN resolves as the id of its composed position.
        if (ChessPositionRef.TryComposeHex(reference) is { } fenHex)
            reference = fenHex;

        try
        {
            NpgsqlSubstrateReads.ExploreResolveRow? resolved;
            await using (var conn = await _dataSource.OpenConnectionAsync(ct))
            {
                resolved = await NpgsqlSubstrateReads.ExploreResolveAsync(conn, reference, ct);
                if (resolved is { Exists: true } value)
                {
                    var facts = await ReadSalientFactsAsync(conn, value.Id, 3, ct);
                    var display = await NpgsqlDisplayLabels.ReadOneAsync(conn, value.Id, ct);
                    var resolvedHex = Convert.ToHexStringLower(value.Id);
                    return new ExploreResolveResponse(
                        IdHex: resolvedHex,
                        Label: PreferredDisplayLabel(display?.Label, resolvedHex),
                        RefKind: value.RefKind,
                        Exists: true,
                        PreviewFacts: facts);
                }
            }

            // A non-hex reference the lexical resolver did not realize may name a player
            // handle. It is ranked through the same indexed name-trajectory candidates as
            // ChessPlayersAsync. Hex and FEN references stay exact.
            if (!LooksLikeEntityHex(reference))
            {
                var players = await ChessPlayersAsync(
                    1, 0, requested, null, "relevance", "desc", ct);
                if (players.Players.FirstOrDefault() is { } player)
                {
                    var playerId = Convert.FromHexString(player.IdHex);
                    await using var conn = await _dataSource.OpenConnectionAsync(ct);
                    var facts = await ReadSalientFactsAsync(conn, playerId, 3, ct);
                    return new ExploreResolveResponse(
                        player.IdHex, player.Name, "chess_player", true, facts);
                }
            }

            if (resolved is not { } unresolved) return null;

            var unresolvedLabel = unresolved.Label;
            if (string.IsNullOrWhiteSpace(unresolvedLabel) || LooksLikeEntityHex(unresolvedLabel))
            {
                await using var conn = await _dataSource.OpenConnectionAsync(ct);
                var unresolvedHex = Convert.ToHexStringLower(unresolved.Id);
                unresolvedLabel = PreferredDisplayLabel(
                    (await NpgsqlDisplayLabels.ReadOneAsync(conn, unresolved.Id, ct))?.Label,
                    unresolvedHex);
            }

            return new ExploreResolveResponse(
                IdHex: Convert.ToHexStringLower(unresolved.Id),
                Label: unresolvedLabel,
                RefKind: unresolved.RefKind,
                Exists: false,
                PreviewFacts: []);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore resolve query failed.", ex);
        }
    }

    private static bool LooksLikeEntityHex(string value) =>
        value.Length == 32 && value.All(static ch =>
            ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    public async Task<ExploreEntityPreviewResponse?> ExploreEntityPreviewAsync(string idHex, CancellationToken ct)
    {
        var id = TryParseIdHex(idHex);
        if (id is null) return null;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var (label, tier, type, exists) = await ReadEntityFacetsAsync(conn, id, ct);
            if (label is null) return null;

            var evidenceCount = await ReadEvidenceCountAsync(conn, id, ct);
            // An id that resolves but was never witnessed has no facts to read.
            var facts = exists
                ? await ReadSalientFactsAsync(conn, id, 3, ct)
                : (IReadOnlyList<SalientFactRow>)Array.Empty<SalientFactRow>();

            return new ExploreEntityPreviewResponse(
                IdHex: idHex.ToLowerInvariant(),
                Label: label,
                Tier: tier,
                Type: type,
                Exists: exists,
                EvidenceCount: evidenceCount,
                PreviewFacts: facts);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore entity preview query failed.", ex);
        }
    }

    // Neighbour search from a computed anchor (ExploreDecomposeService): the id has no
    // stored physicality, so its coordinate and trajectory are bound as parameters to
    // explore_anchor_neighbors.
    public async Task<IReadOnlyList<ExploreAnchorNeighborRow>> ExploreAnchorNeighborsAsync(
        ExploreAnchor anchor, int geodesicK, int frechetK, double frechetMax, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            // The Fréchet arm over the prefiltered candidates dominates cost; it runs
            // under at least a 20 s command budget.
            var rows = await NpgsqlSubstrateReads.ExploreAnchorNeighborsAsync(
                conn, anchor.Cx, anchor.Cy, anchor.Cz, anchor.Cm, anchor.TrajectoryWkt,
                geodesicK, frechetK, frechetMax, Math.Max(DefaultCommandTimeoutSeconds, 20), ct);
            var labels = await ReadDisplayLabelsAsync(
                conn, rows.Select(r => r.IdHex).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);
            return [.. rows.Select(r => new ExploreAnchorNeighborRow(
                r.Axis, r.IdHex, DisplayLabel(labels, r.IdHex, r.Label),
                r.Tier, r.Geodesic, r.Frechet))];
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore anchor neighbours query failed.", ex);
        }
    }

    // Of a batch of candidate surfaces, keeps those whose content id (word_id) exists.
    // One round trip of exact index probes; no fuzzy match or surface scan.
    public async Task<IReadOnlyList<WitnessedWord>> WitnessedWordsAsync(
        IReadOnlyList<string> surfaces, CancellationToken ct)
    {
        if (surfaces.Count == 0) return Array.Empty<WitnessedWord>();

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var rows = await NpgsqlSubstrateReads.WitnessedWordsAsync(conn, surfaces.ToArray(), ct);
            return [.. rows.Select(r => new WitnessedWord(r.Surface, r.IdHex, r.Witnesses))];
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore witnessed-words query failed.", ex);
        }
    }

    public async Task<ExploreEntityResponse?> ExploreEntityAsync(
        string idHex, int consensusLimit, int evidenceLimit, CancellationToken ct)
    {
        var id = TryParseIdHex(idHex);
        if (id is null) return null;

        consensusLimit = Math.Max(0, consensusLimit);
        evidenceLimit = Math.Max(0, evidenceLimit);

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var (label, tier, type, exists) = await ReadEntityFacetsAsync(conn, id, ct);
            if (label is null) return null;

            var evidenceCount = await ReadEvidenceCountAsync(conn, id, ct);

            async Task<T> OnConn<T>(Func<NpgsqlConnection, Task<T>> fn)
            {
                await using var c = await _dataSource.OpenConnectionAsync(ct);
                return await fn(c);
            }

            var physicalitiesTask = OnConn(c => ReadPhysicalitiesAsync(c, id, ct));
            var consensusOutTask = OnConn(c => ReadConsensusAsync(c, id, "out", consensusLimit, ct));
            var consensusInTask = OnConn(c => ReadConsensusAsync(c, id, "in", consensusLimit, ct));
            var sensesTask = OnConn(c => ReadSensesAsync(c, id, ct));
            var constituentsTask = OnConn(c => ReadConstituentsAsync(c, id, ct));
            var packedTask = OnConn(c => ReadPackedVerticesAsync(c, id, ct));
            var realizedTask = OnConn(c => ReadRealizedVerticesAsync(c, id, ct));
            var evidenceTask = OnConn(c => ReadEvidenceItemsAsync(c, id, evidenceLimit, ct));

            await Task.WhenAll(
                physicalitiesTask, consensusOutTask, consensusInTask,
                sensesTask, constituentsTask, packedTask, realizedTask, evidenceTask);

            var consensusOut = await consensusOutTask;
            IReadOnlyList<SalientFactRow> exactFacts =
            [
                .. consensusOut.Take(24).Select(c =>
                    new SalientFactRow(c.Type, c.EntityLabel, c.EffMu, c.Witnesses))
            ];

            return new ExploreEntityResponse(
                IdHex: idHex.ToLowerInvariant(),
                Label: label,
                Tier: tier,
                Type: type,
                Exists: exists,
                EvidenceCount: evidenceCount,
                Physicalities: await physicalitiesTask,
                SalientFacts: exactFacts,
                ConsensusOut: consensusOut,
                ConsensusIn: await consensusInTask,
                Senses: await sensesTask,
                Constituents: await constituentsTask,
                PackedVertices: await packedTask,
                RealizedVertices: await realizedTask,
                Evidence: await evidenceTask);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore entity query failed.", ex);
        }
    }

    public async Task<ExploreTrainingExportResponse?> ExploreTrainingExportAsync(
        string idHex, int consensusLimit, int evidenceLimit, bool includeMembers, bool includePeers, CancellationToken ct)
    {
        var entity = await ExploreEntityAsync(idHex, consensusLimit, evidenceLimit, ct);
        if (entity is null) return null;

        IReadOnlyList<ExploreMemberRow> members = Array.Empty<ExploreMemberRow>();
        IReadOnlyList<ExplorePeerRow> peers = Array.Empty<ExplorePeerRow>();

        if (includeMembers)
        {
            var m = await ExploreMembersAsync(idHex, 100, ct);
            members = m?.Members ?? Array.Empty<ExploreMemberRow>();
        }

        if (includePeers)
        {
            var p = await ExplorePeersAsync(idHex, 48, ct);
            peers = p?.Peers ?? Array.Empty<ExplorePeerRow>();
        }

        // EvidenceCount is the exact number of attestation rows for this entity.
        // ObservationCount is multiplicity carried by those rows, not additional rows.
        var witnessRows = entity.EvidenceCount;
        var consensusRows = entity.ConsensusOut.Count + entity.ConsensusIn.Count;

        return new ExploreTrainingExportResponse(
            IdHex: entity.IdHex,
            Label: entity.Label,
            GeneratedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            WitnessRows: witnessRows,
            ConsensusRows: consensusRows,
            Entity: entity,
            Members: members,
            Peers: peers);
    }

    public async Task<ExploreNeighborsResponse?> ExploreNeighborsAsync(string idHex, int k, CancellationToken ct)
    {
        var id = TryParseIdHex(idHex);
        if (id is null) return null;
        k = Math.Max(0, k);

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var label = await ReadLabelAsync(conn, id, ct);
            if (label is null) return null;

            // KNN over the entity's own stored physicality coordinate.
            var structuralRows = await NpgsqlSubstrateReads.StructuralNeighborsAsync(conn, id, k, ct);
            var structuralLabels = await ReadDisplayLabelsAsync(
                conn,
                structuralRows.Where(r => r.IdHex is not null)
                    .Select(r => r.IdHex!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ct);
            var structural = structuralRows
                .Select(r => new ExploreNeighborRow(
                    Neighbor: DisplayLabel(structuralLabels, r.IdHex, r.Label),
                    Geodesic: r.Geodesic, Frechet: r.Frechet,
                    Axis: "structural", NeighborIdHex: r.IdHex?.ToLowerInvariant(),
                    X: r.X, Y: r.Y, Z: r.Z, M: r.M, Radius: r.Radius))
                .ToList();

            var semantic = await ReadSalientFactsAsync(conn, id, k, ct);

            return new ExploreNeighborsResponse(
                IdHex: idHex.ToLowerInvariant(),
                Structural: structural,
                Semantic: semantic);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore neighbors query failed.", ex);
        }
    }

    public async Task<ExploreMembersResponse?> ExploreMembersAsync(string idHex, int limit, CancellationToken ct)
    {
        var id = TryParseIdHex(idHex);
        if (id is null) return null;
        limit = Math.Max(0, limit);

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            if (await ReadLabelAsync(conn, id, ct) is null) return null;

            var memberRows = await NpgsqlSubstrateReads.ConceptMembersAsync(conn, id, limit, ct);
            var labels = await ReadDisplayLabelsAsync(
                conn, memberRows.Select(r => r.IdHex).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);
            var members = memberRows
                .Select(r => new ExploreMemberRow(
                    r.IdHex, DisplayLabel(labels, r.IdHex, r.Label), r.Kind, r.EffMu, r.Witnesses))
                .ToList();

            return new ExploreMembersResponse(idHex.ToLowerInvariant(), members);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore members query failed.", ex);
        }
    }

    public async Task<ExplorePeersResponse?> ExplorePeersAsync(string idHex, int limit, CancellationToken ct)
    {
        var id = TryParseIdHex(idHex);
        if (id is null) return null;
        limit = Math.Max(0, limit);

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            if (await ReadLabelAsync(conn, id, ct) is null) return null;

            var peers = (await NpgsqlSubstrateReads.ConceptPeersAsync(conn, id, limit, ct))
                .Select(r => new ExplorePeerRow(r.Peer, r.Kind, r.Strength))
                .ToList();

            return new ExplorePeersResponse(idHex.ToLowerInvariant(), peers);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore peers query failed.", ex);
        }
    }

    public async Task<ExploreContainersResponse?> ExploreContainersAsync(
        string idHex, int maxHops, int limit, CancellationToken ct)
    {
        var id = TryParseIdHex(idHex);
        if (id is null) return null;
        maxHops = Math.Max(0, maxHops);
        limit = Math.Max(0, limit);

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            var containerRows = await NpgsqlSubstrateReads.ContainersAsync(conn, id, maxHops, limit, ct);
            var labels = await ReadDisplayLabelsAsync(
                conn, containerRows.Select(r => r.IdHex).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);
            var containers = containerRows
                .Select(r => new ExploreContainerRow(
                    r.IdHex, DisplayLabel(labels, r.IdHex, r.Label), r.Tier, r.Type, r.Hops))
                .ToList();

            return new ExploreContainersResponse(idHex.ToLowerInvariant(), containers);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore containers query failed.", ex);
        }
    }

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
        return rows
            .GroupBy(label, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        var labels = await ReadDisplayLabelsAsync(
            conn, rows.Select(r => r.EntityIdHex).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);
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

    private static async Task<IReadOnlyList<ExploreSenseRow>> ReadSensesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.SensesAsync(conn, id, ct);
        var labels = await ReadDisplayLabelsAsync(
            conn, rows.Select(r => r.SynsetIdHex).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);
        var result = rows.Select(s => new ExploreSenseRow(
            SenseIdHex: s.SenseIdHex,
            SynsetIdHex: s.SynsetIdHex,
            SynsetLabel: DisplayLabel(labels, s.SynsetIdHex, s.SynsetLabel),
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
        var rows = await NpgsqlSubstrateReads.EvidenceReceiptAsync(conn, id, limit, ct);
        var labels = await ReadDisplayLabelsAsync(
            conn, rows.Select(r => r.ObjectIdHex).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);
        var result = rows.Select(e => new LabeledEvidenceItem(
            TypeId: e.TypeIdHex,
            TypeLabel: e.TypeLabel,
            ObjectId: e.ObjectIdHex,
            ObjectLabel: DisplayLabel(labels, e.ObjectIdHex, e.ObjectLabel),
            SourceId: "",
            SourceLabel: e.SourceLabels ?? "",
            ContextId: null,
            Outcome: 2,
            ObservationCount: e.WitnessCount,
            EffMu: e.EffMu)).ToList();
        var ambiguous = AmbiguousDisplayLabels(
            result, r => r.ObjectLabel, r => r.ObjectId);
        return [.. result.Select(r => ambiguous.Count == 0 ? r : r with
        {
            ObjectLabel = DisambiguateDisplayLabel(
                r.ObjectLabel, r.ObjectId, ambiguous)
        })];
    }
}
