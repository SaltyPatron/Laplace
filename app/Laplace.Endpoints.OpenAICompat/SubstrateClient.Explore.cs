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

        // Expired counts need a successful refresh. A failed load must reach
        // the caller's error path, rather than silently returning stale data.
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
                    var facts = await ReadPreviewFactsAsync(conn, value.Id, ct);
                    var resolvedHex = Convert.ToHexStringLower(value.Id);
                    return new ExploreResolveResponse(
                        IdHex: resolvedHex,
                        Label: value.Label,
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
            var facts = exists ? await ReadPreviewFactsAsync(conn,id,ct) : [];

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
            var missingIds = rows
                .Where(r => string.IsNullOrWhiteSpace(r.Label)
                         || LooksLikeEntityHex(r.Label)
                         || IsGenericUnrealizedLabel(r.Label))
                .Select(r => r.IdHex)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var labels = missingIds.Count > 0
                ? await ReadDisplayLabelsAsync(conn, missingIds, ct)
                : new Dictionary<string, (string Label, short? Tier)>(StringComparer.OrdinalIgnoreCase);
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
            // Every section describes the same entity under one read snapshot.
            // Borrowing eight more pooled connections while retaining this one
            // both exhausted small pools and let concurrent ingestion make the
            // count, evidence and graph sections disagree.
            await using var conn = await _dataSourceReadOnly.OpenConnectionAsync(ct);
            await using var transaction = await conn.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead, ct);
            var (label, tier, type, exists) = await ReadEntityFacetsAsync(conn, id, ct);
            if (label is null) return null;

            var evidenceCount = await ReadEvidenceCountAsync(conn, id, ct);

            var physicalities = await ReadPhysicalitiesAsync(conn, id, ct);
            var consensusOut = await ReadConsensusAsync(conn, id, "out", consensusLimit, ct);
            var consensusIn = await ReadConsensusAsync(conn, id, "in", consensusLimit, ct);
            var senses = await ReadSensesAsync(conn, id, ct);
            var lexicon = await ReadLexiconEntryAsync(conn,id,ct);
            var constituents = await ReadConstituentsAsync(conn, id, ct);
            var packed = await ReadPackedVerticesAsync(conn, id, ct);
            var realized = await ReadRealizedVerticesAsync(conn, id, ct);
            var evidence = await ReadEvidenceItemsAsync(conn, id, evidenceLimit, ct);
            await transaction.CommitAsync(ct);
            IReadOnlyList<SalientFactRow> exactFacts =
            [
                .. senses.Select(r=>new SalientFactRow("meaning",r.SynsetLabel,r.EffMu,r.Witnesses))
            ];

            return new ExploreEntityResponse(
                IdHex: idHex.ToLowerInvariant(),
                Label: label,
                Tier: tier,
                Type: type,
                Exists: exists,
                EvidenceCount: evidenceCount,
                Physicalities: physicalities,
                SalientFacts: exactFacts,
                ConsensusOut: consensusOut,
                ConsensusIn: consensusIn,
                Senses: senses,
                Constituents: constituents,
                PackedVertices: packed,
                RealizedVertices: realized,
                Evidence: evidence,
                Lexicon: lexicon);
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
            var missingIds = structuralRows
                .Where(r => r.IdHex is not null &&
                    (string.IsNullOrWhiteSpace(r.Label)
                     || LooksLikeEntityHex(r.Label)
                     || IsGenericUnrealizedLabel(r.Label)))
                .Select(r => r.IdHex!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var structuralLabels = missingIds.Count > 0
                ? await ReadDisplayLabelsAsync(conn, missingIds, ct)
                : new Dictionary<string, (string Label, short? Tier)>(StringComparer.OrdinalIgnoreCase);
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
            var missingIds = memberRows
                .Where(r => string.IsNullOrWhiteSpace(r.Label)
                         || LooksLikeEntityHex(r.Label)
                         || IsGenericUnrealizedLabel(r.Label))
                .Select(r => r.IdHex)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var labels = missingIds.Count > 0
                ? await ReadDisplayLabelsAsync(conn, missingIds, ct)
                : new Dictionary<string, (string Label, short? Tier)>(StringComparer.OrdinalIgnoreCase);
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
}
