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

            // Approx variant only: the exact consensus_stats() is a minutes-long full
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

            // source_counts() is a full GROUP BY over attestations plus a
            // count(DISTINCT) join — unbounded at 135M rows. Attempt within a small
            // budget; on timeout the stage grid still renders from the static witness
            // catalog with zero live counts (degraded, not dead).
            var sources = new List<ExploreSourceRow>();
            var liveByKey = new Dictionary<string, ExploreSourceRow>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var row in await NpgsqlSubstrateReads.SourceCountsAsync(
                    conn, ct, timeoutSeconds: 10))
                {
                    var mapped = new ExploreSourceRow(
                        Key: row.Source,
                        Evidence: row.Evidence,
                        Content: row.Content,
                        Stage: WitnessCatalog.StageForSource(WitnessCatalog.Root, WitnessCatalog.CliForSourceKey(row.Source)),
                        Layer: null,
                        Role: null,
                        IdHex: row.IdHex);
                    sources.Add(mapped);
                    liveByKey[row.Source] = mapped;
                }
            }
            catch (Exception ex) when (IsStatementTimeout(ex) && !ct.IsCancellationRequested)
            {
                // Exact source_counts() blew its budget (measured ~15s at 6.3M
                // rows and growing). Fall back to the approx catalog — name, id
                // and a partition-stats evidence estimate in ~200ms. Content
                // count is unknown there and stays null: an estimate labelled,
                // never a lying zero. The whole stage→source→roster tier hangs
                // off this listing, so it must never come back empty.
                sources.Clear();
                liveByKey.Clear();
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

        // GH #575: FEN → composed position id before the lexical resolve arms.
        if (ChessPositionRef.TryComposeHex(reference) is { } fenHex)
            reference = fenHex;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var resolved = await NpgsqlSubstrateReads.ExploreResolveAsync(conn, reference, ct);
            if (resolved is not { } value) return null;

            var facts = await ReadSalientFactsAsync(conn, value.Id, 3, ct);
            return new ExploreResolveResponse(
                IdHex: Convert.ToHexStringLower(value.Id),
                Label: value.Label,
                RefKind: value.RefKind,
                Exists: value.Exists,
                PreviewFacts: facts);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore resolve query failed.", ex);
        }
    }

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
            return [.. rows.Select(r => new ExploreAnchorNeighborRow(
                r.Axis, r.IdHex, r.Label ?? r.IdHex, r.Tier, r.Geodesic, r.Frechet))];
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

        consensusLimit = Math.Clamp(consensusLimit, 1, 200);
        evidenceLimit = Math.Clamp(evidenceLimit, 1, 100);

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
            var evidenceTask = OnConn(c => ReadEvidenceItemsAsync(c, id, evidenceLimit, ct));

            await Task.WhenAll(
                physicalitiesTask, factsTask, consensusOutTask, consensusInTask,
                sensesTask, constituentsTask, evidenceTask);

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
        k = Math.Clamp(k, 1, 50);

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var label = await ReadLabelAsync(conn, id, ct);
            if (label is null) return null;

            // Entity-id KNN with real S³ coords — not label→prompt_state re-resolve,
            // and not decorative Math.sin positions on the glome.
            var structural = (await NpgsqlSubstrateReads.StructuralNeighborsAsync(conn, id, k, ct))
                .Where(r => !string.IsNullOrWhiteSpace(r.Label))
                .Select(r => new ExploreNeighborRow(
                    Neighbor: r.Label!.Trim(), Geodesic: r.Geodesic, Frechet: r.Frechet,
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
        limit = Math.Clamp(limit, 1, 500);

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            if (await ReadLabelAsync(conn, id, ct) is null) return null;

            var members = (await NpgsqlSubstrateReads.ConceptMembersAsync(conn, id, limit, ct))
                .Select(r => new ExploreMemberRow(r.IdHex, r.Label, r.Kind, r.EffMu, r.Witnesses))
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
        limit = Math.Clamp(limit, 1, 100);

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
        maxHops = Math.Clamp(maxHops, 1, 8);
        limit = Math.Clamp(limit, 1, 1000);

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            if (await ReadLabelAsync(conn, id, ct) is null) return null;

            var containers = (await NpgsqlSubstrateReads.ContainersAsync(conn, id, maxHops, limit, ct))
                .Select(r => new ExploreContainerRow(r.IdHex, r.Label, r.Tier, r.Type, r.Hops))
                .ToList();

            return new ExploreContainersResponse(idHex.ToLowerInvariant(), containers);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore containers query failed.", ex);
        }
    }

    public async Task<ExploreGraphResponse?> ExploreConsensusGraphAsync(
        string idHex, int hops, int fanout, CancellationToken ct)
    {
        var seed = TryParseIdHex(idHex);
        if (seed is null) return null;

        // Native SPI beam (pg_laplace_explore_web): one connection, undirected
        // consensus probe, ≤fanout new nodes/hop, all tiers. Labels via render_text_fast.
        hops = Math.Clamp(hops, 1, 4);
        fanout = Math.Clamp(fanout, 2, 16);
        const int maxNodes = 160;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var (label, tier, _, _) = await ReadEntityFacetsAsync(conn, seed, ct);
            if (label is null) return null;

            var seedHex = idHex.ToLowerInvariant();
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

            // Batch-label endpoints + relation types through the native render path.
            var idsToLabel = unlabeled.Concat(typeIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (idsToLabel.Count > 0)
            {
                var labels = await ReadLabelsFastAsync(conn, idsToLabel, ct);
                foreach (var (hex, entry) in labels)
                {
                    if (nodes.TryGetValue(hex, out var node))
                        nodes[hex] = node with { Label = entry.Label, Tier = node.Tier ?? entry.Tier };
                }

                for (var i = 0; i < edges.Count; i++)
                {
                    var e = edges[i];
                    if (labels.TryGetValue(e.Type, out var tl))
                        edges[i] = e with { Type = tl.Label };
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

    private static async Task<Dictionary<string, (string Label, short? Tier)>> ReadLabelsFastAsync(
        NpgsqlConnection conn, IReadOnlyList<string> idHexes, CancellationToken ct)
    {
        var result = new Dictionary<string, (string Label, short? Tier)>(StringComparer.OrdinalIgnoreCase);
        if (idHexes.Count == 0) return result;

        var ids = new byte[idHexes.Count][];
        for (var i = 0; i < idHexes.Count; i++)
        {
            var parsed = TryParseIdHex(idHexes[i]);
            if (parsed is null) continue;
            ids[i] = parsed;
        }

        foreach (var row in await NpgsqlSubstrateReads.LabelsFastAsync(conn, ids.Where(x => x is not null).ToArray()!, ct))
        {
            var hex = row.IdHex.ToLowerInvariant();
            var lab = row.Label ?? hex;
            if (lab.Length > 48) lab = lab[..47] + "…";
            result[hex] = (lab, row.Tier);
        }

        return result;
    }

    private static byte[]? TryParseIdHex(string idHex)
    {
        if (string.IsNullOrWhiteSpace(idHex) || idHex.Length != 32) return null;
        try { return Convert.FromHexString(idHex); }
        catch (FormatException) { return null; }
    }

    private static Task<string?> ReadLabelAsync(NpgsqlConnection conn, byte[] id, CancellationToken ct) =>
        NpgsqlSubstrateReads.LabelOrHexAsync(conn, id, ct);

    /// <summary>
    /// <see cref="NpgsqlSubstrateReads.EntityFacetsAsync"/> returns no row for an unwitnessed
    /// id — the fallback to <see cref="ReadLabelAsync"/> is safe because NpgsqlRead fully
    /// drains and disposes its reader before returning, so there is no Npgsql MARS conflict
    /// running a second command on this connection right after.
    /// </summary>
    private static async Task<(string Label, short? Tier, string? Type, bool Exists)> ReadEntityFacetsAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        if (await NpgsqlSubstrateReads.EntityFacetsAsync(conn, id, ct) is { } f)
            return (f.Label, f.Tier, f.Type, f.Exists);

        var fallbackLabel = await ReadLabelAsync(conn, id, ct);
        return fallbackLabel is null ? (null!, null, null, false) : (fallbackLabel, null, null, false);
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

        return rows;
    }

    private static async Task<IReadOnlyList<ExploreSenseRow>> ReadSensesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.SensesAsync(conn, id, ct);
        return [.. rows.Select(s => new ExploreSenseRow(
            SenseIdHex: s.SenseIdHex,
            SynsetIdHex: s.SynsetIdHex,
            SynsetLabel: s.SynsetLabel,
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

    private static async Task<IReadOnlyList<LabeledEvidenceItem>> ReadEvidenceItemsAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.EvidenceReceiptAsync(conn, id, limit, ct);
        return [.. rows.Select(e => new LabeledEvidenceItem(
            TypeId: e.TypeIdHex,
            TypeLabel: e.TypeLabel,
            ObjectId: e.ObjectIdHex,
            ObjectLabel: e.ObjectLabel,
            SourceId: "",
            SourceLabel: e.SourceLabels ?? "",
            ContextId: null,
            Outcome: 2,
            ObservationCount: e.WitnessCount,
            EffMu: e.EffMu))];
    }
}
