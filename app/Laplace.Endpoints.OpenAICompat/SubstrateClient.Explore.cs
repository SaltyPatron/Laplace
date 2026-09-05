using System.Globalization;
using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{
    private static readonly WitnessCatalog WitnessCatalog = WitnessCatalog.Load();

    // The catalog is tenant-independent substrate accounting; recomputing it per page
    // load re-aggregated 100M+ row tables on every UI landing. One flight fills it,
    // everyone reads it for the TTL.
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
            // Stale: serve it immediately and refresh once in the background. A cold
            // load pays the doomed exact-aggregate budget attempts (~15s); no user
            // request should wait on that when yesterday's counts are on hand.
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
                        // keep serving stale; the next expiry retries
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

            // Approx variant only: the exact consensus.stats() is a minutes-long full
            // aggregate and this is the UI landing call. The audit report is the place
            // that attempts exactness (AuditReportAsync).
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

            // BOUNDED ONLY. ops.source_counts() is a full GROUP BY over attestations
            // plus a count(DISTINCT) join — unbounded, and it does not belong on a
            // request path at any budget.
            //
            // This used to attempt the exact form first with timeoutSeconds:10 and treat
            // approx as the failure case. That guard could not work: timeoutSeconds sets
            // Npgsql's CommandTimeout, which is a CLIENT wait. The client gives up at 10s,
            // sends a best-effort cancel, renders this degraded page on schedule, and
            // returns 200 — while the BACKEND keeps executing and keeps AccessShareLock.
            //
            // MEASURED 2026-08-10: a request issued this query, the endpoint answered
            // normally, and the backend ran 21+ minutes holding the lock. ALTER EXTENSION
            // laplace_substrate UPDATE queued behind it and the deploy job was cancelled
            // at timeout. The same wedge is recorded at 2h08m on 2026-08-06 in
            // NpgsqlSubstrateReads.TopRelationsAsync — a second query, same shape, and the
            // fix applied there (Issue 52, bounded candidate form, 0.5s at 124M) was never
            // generalized to this one.
            //
            // Serving reads the bounded form. The exact aggregate is an offline audit and
            // has no caller here. Content count is unknown in approx and stays null: an
            // estimate labelled, never a lying zero.
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

        // GH #575: FEN → composed position id before the lexical resolve arms.
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
                    return new ExploreResolveResponse(
                        IdHex: Convert.ToHexStringLower(value.Id),
                        Label: display?.Label ?? "Unrealized entity",
                        RefKind: value.RefKind,
                        Exists: true,
                        PreviewFacts: facts);
                }
            }

            // A provider handle, surname, forename, or FIDE name-order spelling is a
            // legitimate warehouse reference.  Player identities are governed handles,
            // not lexical word ids, so the lexical resolver cannot discover them.  Reuse
            // the indexed name-trajectory candidate path and its single human-name ranker;
            // do not add a rendered corpus scan or a second player matching law here.
            // Hex/FEN references remain exact and never fall through to name matching.
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
                unresolvedLabel = (await NpgsqlDisplayLabels.ReadOneAsync(conn, unresolved.Id, ct))?.Label
                    ?? "Unrealized entity";
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
            // A resolvable-but-unwitnessed id has no facts to fetch; skip the
            // salient-facts walk entirely for it. The not-found explorer
            // (/v1/explore/notfound) serves the navigable view for these.
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

    // Neighbour search from a COMPUTED anchor (see ExploreDecomposeService /
    // explore_anchor_neighbors). Used by the not-found explorer: the id resolved
    // but was never witnessed, so there is no stored coord -- the anchor comes
    // from HashComposer instead, and the KNN runs on bound parameters.
    public async Task<IReadOnlyList<ExploreAnchorNeighborRow>> ExploreAnchorNeighborsAsync(
        ExploreAnchor anchor, int geodesicK, int frechetK, double frechetMax, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            // Frechet over a bounded prefilter is the slow arm; keep it inside a
            // real budget so the not-found page renders instead of hanging.
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

    // Of a batch of candidate surfaces, which are witnessed words? Resolves each
    // through word_id (the content hash) and keeps the ids that entity_exists.
    // One round trip -> did-you-mean is an exact index probe over the edit-distance
    // neighbourhood, no fuzzy extension or full-surface scan.
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
            var factsTask = OnConn(c => ReadSalientFactsAsync(c, id, 24, ct));
            var consensusOutTask = OnConn(c => ReadConsensusAsync(c, id, "out", consensusLimit, ct));
            var consensusInTask = OnConn(c => ReadConsensusAsync(c, id, "in", consensusLimit, ct));
            var sensesTask = OnConn(c => ReadSensesAsync(c, id, ct));
            var constituentsTask = OnConn(c => ReadConstituentsAsync(c, id, ct));
            var packedTask = OnConn(c => ReadPackedVerticesAsync(c, id, ct));
            var realizedTask = OnConn(c => ReadRealizedVerticesAsync(c, id, ct));
            var evidenceTask = OnConn(c => ReadEvidenceItemsAsync(c, id, evidenceLimit, ct));

            await Task.WhenAll(
                physicalitiesTask, factsTask, consensusOutTask, consensusInTask,
                sensesTask, constituentsTask, packedTask, realizedTask, evidenceTask);

            return new ExploreEntityResponse(
                IdHex: idHex.ToLowerInvariant(),
                Label: label,
                Tier: tier,
                Type: type,
                Exists: exists,
                EvidenceCount: evidenceCount,
                Physicalities: await physicalitiesTask,
                SalientFacts: await factsTask,
                ConsensusOut: await consensusOutTask,
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

        var witnessRows = entity.EvidenceCount
            + entity.Evidence.Sum(e => e.ObservationCount);
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

            // Entity-id KNN with real S³ coords — not label→prompt_state re-resolve,
            // and not decorative Math.sin positions on the glome.
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
            if (await ReadLabelAsync(conn, id, ct) is null) return null;

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

        // Native SPI web expansion (pg_laplace_explore_web): one connection,
        // undirected consensus probe, ≤fanout new nodes/frontier parent, all tiers.
        // Display text is resolved after the bounded graph election: semantic names,
        // exact shallow Unicode, one-constituent document/definition previews, then
        // governed type/source fallbacks. The entity hash remains identity only.
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
                return new ExploreGraphResponse(
                    seedHex, label, hops, fanout, [], [], true, 0);

            var nodes = new Dictionary<string, ExploreGraphNode>(StringComparer.OrdinalIgnoreCase)
            {
                [seedHex] = new ExploreGraphNode(seedHex, label, 0, tier),
            };
            var edges = new List<ExploreGraphEdge>();
            var edgeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var typeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unlabeled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var edgeRows = await NpgsqlSubstrateReads.ExploreWebAsync(
                conn, seed, hops, fanout, maxNodes, Math.Max(SubstrateClient.DefaultCommandTimeoutSeconds, 60), ct);
            foreach (var w in edgeRows)
            {
                var sourceHex = w.SourceIdHex.ToLowerInvariant();
                var typeHex = w.TypeIdHex.ToLowerInvariant();
                var objectHex = w.ObjectIdHex.ToLowerInvariant();
                var hop = w.Hop;

                typeIds.Add(typeHex);
                var key = $"{sourceHex}|{typeHex}|{objectHex}";
                if (!edgeKeys.Add(key)) continue;

                edges.Add(new ExploreGraphEdge(
                    SourceIdHex: sourceHex,
                    TargetIdHex: objectHex,
                    Type: typeHex,
                    EffMu: w.EffMu,
                    Witnesses: w.WitnessCount,
                    Hop: hop));

                if (!nodes.ContainsKey(sourceHex))
                {
                    nodes[sourceHex] = new ExploreGraphNode(sourceHex, sourceHex, hop, null);
                    unlabeled.Add(sourceHex);
                }
                if (!nodes.ContainsKey(objectHex))
                {
                    nodes[objectHex] = new ExploreGraphNode(objectHex, objectHex, hop, null);
                    unlabeled.Add(objectHex);
                }
                else if (hop < nodes[objectHex].Hop)
                {
                    nodes[objectHex] = nodes[objectHex] with { Hop = hop };
                }
            }

            // Batch-label endpoints + relation types only after the graph is bounded.
            var idsToLabel = unlabeled.Concat(typeIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (idsToLabel.Count > 0)
            {
                var labels = await ReadDisplayLabelsAsync(conn, idsToLabel, ct);
                foreach (var (hex, entry) in labels)
                {
                    if (nodes.TryGetValue(hex, out var node))
                        nodes[hex] = node with
                        {
                            Label = TrimGraphLabel(entry.Label),
                            Tier = node.Tier ?? entry.Tier,
                        };
                }

                for (var i = 0; i < edges.Count; i++)
                {
                    var e = edges[i];
                    if (labels.TryGetValue(e.Type, out var tl))
                        edges[i] = e with { Type = TrimGraphLabel(tl.Label) };
                }
            }

            var truncated = nodes.Count >= maxNodes;
            return new ExploreGraphResponse(
                IdHex: seedHex,
                Label: label,
                Hops: hops,
                Fanout: fanout,
                Nodes: nodes.Values.OrderBy(n => n.Hop).ThenBy(n => n.Label).ToList(),
                Edges: edges,
                Truncated: truncated,
                MaxNodes: maxNodes);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            throw new SubstrateUnavailableException(
                $"Explore consensus graph query failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
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

    private static string DisplayLabel(
        IReadOnlyDictionary<string, (string Label, short? Tier)> labels,
        string? idHex,
        string? fallback)
    {
        if (idHex is not null && labels.TryGetValue(idHex, out var found))
            return found.Label;
        if (!string.IsNullOrWhiteSpace(fallback) && !LooksLikeEntityHex(fallback))
            return fallback.Trim();
        return "Unrealized entity";
    }

    private static string TrimGraphLabel(string label)
    {
        // Display labels are Unicode surfaces, not byte strings. Collapse UI-only
        // whitespace and truncate on grapheme boundaries so an emoji/combining sequence is
        // never split merely because the graph sprite has a compact text budget.
        label = string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (label.Length == 0) return "Unrealized entity";

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
    /// Tier/type/existence are read without rendering the entity body; display text has its own
    /// bounded policy in NpgsqlDisplayLabels. This prevents opening a high-tier document from
    /// reconstructing it merely to paint the page heading.
    /// </summary>
    private static async Task<(string Label, short? Tier, string? Type, bool Exists)> ReadEntityFacetsAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        var display = await NpgsqlDisplayLabels.ReadOneAsync(conn, id, ct);
        if (display is null) return (null!, null, null, false);

        var facet = await NpgsqlDisplayLabels.FacetAsync(conn, id, ct);
        return facet is { } f
            ? (display.Value.Label, f.Tier, f.Type, f.Exists)
            : (display.Value.Label, display.Value.Tier, null, false);
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

        return rows;
    }

    private static async Task<IReadOnlyList<ExploreSenseRow>> ReadSensesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.SensesAsync(conn, id, ct);
        var labels = await ReadDisplayLabelsAsync(
            conn, rows.Select(r => r.SynsetIdHex).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);
        return [.. rows.Select(s => new ExploreSenseRow(
            SenseIdHex: s.SenseIdHex,
            SynsetIdHex: s.SynsetIdHex,
            SynsetLabel: DisplayLabel(labels, s.SynsetIdHex, s.SynsetLabel),
            EffMu: s.EffMu,
            Witnesses: s.Witnesses))];
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
        return [.. rows.Select(e => new LabeledEvidenceItem(
            TypeId: e.TypeIdHex,
            TypeLabel: e.TypeLabel,
            ObjectId: e.ObjectIdHex,
            ObjectLabel: DisplayLabel(labels, e.ObjectIdHex, e.ObjectLabel),
            SourceId: "",
            SourceLabel: e.SourceLabels ?? "",
            ContextId: null,
            Outcome: 2,
            ObservationCount: e.WitnessCount,
            EffMu: e.EffMu))];
    }
}
