using Laplace.Api.Contracts;
using Npgsql;
using NpgsqlTypes;

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

            var counts = new List<SubstrateCount>();
            await using (var cmd = new NpgsqlCommand("SELECT metric, value FROM laplace.substrate_counts();", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    counts.Add(new SubstrateCount(reader.GetString(0).TrimEnd(' ', '~'), reader.GetInt64(1)));
            }

            // Approx variant only: the exact consensus_stats() is a minutes-long full
            // aggregate and this is the UI landing call. The audit report is the place
            // that attempts exactness (AuditReportAsync).
            ConsensusHealth? consensus = null;
            await using (var cmd = new NpgsqlCommand(
                "SELECT evidence_rows, consensus_rows, dedup_ratio, avg_witnesses, max_witnesses FROM laplace.consensus_stats_approx();", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    consensus = new ConsensusHealth(
                        EvidenceRows: reader.GetInt64(0),
                        ConsensusRows: reader.GetInt64(1),
                        DedupRatio: reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                        AvgWitnesses: reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                        MaxWitnesses: reader.IsDBNull(4) ? null : reader.GetInt64(4));
                }
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
                await using var cmd = new NpgsqlCommand("SELECT source, evidence, content FROM laplace.source_counts();", conn);
                cmd.CommandTimeout = 10;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var key = reader.GetString(0);
                    var row = new ExploreSourceRow(
                        Key: key,
                        Evidence: reader.GetInt64(1),
                        Content: reader.GetInt64(2),
                        Stage: WitnessCatalog.StageForSource(WitnessCatalog.Root, key),
                        Layer: null,
                        Role: null);
                    sources.Add(row);
                    liveByKey[key] = row;
                }
            }
            catch (Exception ex) when (IsStatementTimeout(ex) && !ct.IsCancellationRequested)
            {
                sources.Clear();
                liveByKey.Clear();
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

        const string sql = """
            WITH resolved AS (
                SELECT CASE
                    WHEN @ref ~ '^[0-9a-f]{32}$' THEN decode(@ref, 'hex')
                    ELSE COALESCE(laplace.concept_ref(@ref), laplace.word_id(@ref))
                END AS id,
                CASE
                    WHEN @ref ~ '^[0-9a-f]{32}$' THEN 'hex'
                    WHEN laplace.concept_ref(@ref) IS NOT NULL THEN 'concept'
                    WHEN laplace.word_id(@ref) IS NOT NULL THEN 'word'
                    ELSE 'not_found'
                END AS ref_kind
            )
            SELECT r.id, laplace.label_or_hex(r.id), r.ref_kind,
                   laplace.entity_exists(r.id) AS exists
            FROM resolved r
            WHERE r.id IS NOT NULL;
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("ref", reference.Trim());

            byte[]? id = null;
            string? label = null;
            string refKind = "not_found";
            bool exists = false;

            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return null;
                id = (byte[])reader[0];
                label = reader.GetString(1);
                refKind = reader.GetString(2);
                exists = reader.GetBoolean(3);
            }

            var facts = await ReadSalientFactsAsync(conn, id, 3, ct);
            return new ExploreResolveResponse(
                IdHex: Convert.ToHexStringLower(id),
                Label: label,
                RefKind: refKind,
                Exists: exists,
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
            var facts = await ReadSalientFactsAsync(conn, id, 3, ct);

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

            var structural = new List<ExploreNeighborRow>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT neighbor, geodesic, frechet FROM laplace.nearest_neighbors_4d(@w, @k);", conn))
            {
                cmd.Parameters.AddWithValue("w", label);
                cmd.Parameters.AddWithValue("k", k);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    structural.Add(new ExploreNeighborRow(
                        Neighbor: reader.GetString(0),
                        Geodesic: reader.GetDouble(1),
                        Frechet: reader.IsDBNull(2) ? null : reader.GetDouble(2),
                        Axis: "structural"));
                }
            }

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

        const string sql = """
            SELECT encode(m.member, 'hex'), m.kind,
                   laplace.label_or_hex(m.member), m.mu, m.witnesses
            FROM laplace.concept_members(@id) m
            ORDER BY m.mu DESC NULLS LAST, m.member
            LIMIT @limit;
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            if (await ReadLabelAsync(conn, id, ct) is null) return null;

            var members = new List<ExploreMemberRow>();
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                members.Add(new ExploreMemberRow(
                    MemberIdHex: reader.GetString(0),
                    MemberLabel: reader.GetString(2),
                    Kind: reader.GetString(1),
                    EffMu: reader.GetDecimal(3),
                    Witnesses: reader.GetInt64(4)));
            }

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

        const string sql = """
            SELECT peer, kind, strength
            FROM laplace.concept_peers(@id, @limit);
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            if (await ReadLabelAsync(conn, id, ct) is null) return null;

            var peers = new List<ExplorePeerRow>();
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                peers.Add(new ExplorePeerRow(
                    Peer: reader.GetString(0),
                    Kind: reader.GetString(1),
                    Strength: reader.GetDouble(2)));
            }

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

        const string sql = """
            SELECT encode(c.entity_id, 'hex'), c.tier,
                   laplace.render(c.type_id), c.hops,
                   laplace.label_or_hex(c.entity_id)
            FROM laplace.containers_of(@id, @hops, @limit) c;
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            if (await ReadLabelAsync(conn, id, ct) is null) return null;

            var containers = new List<ExploreContainerRow>();
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("hops", maxHops);
            cmd.Parameters.AddWithValue("limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                containers.Add(new ExploreContainerRow(
                    EntityIdHex: reader.GetString(0),
                    EntityLabel: reader.GetString(4),
                    Tier: reader.GetInt16(1),
                    Type: reader.GetString(2),
                    Hops: reader.GetInt32(3)));
            }

            return new ExploreContainersResponse(idHex.ToLowerInvariant(), containers);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Explore containers query failed.", ex);
        }
    }

    private static byte[]? TryParseIdHex(string idHex)
    {
        if (string.IsNullOrWhiteSpace(idHex) || idHex.Length != 32) return null;
        try { return Convert.FromHexString(idHex); }
        catch (FormatException) { return null; }
    }

    private static async Task<string?> ReadLabelAsync(NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT laplace.label_or_hex(@id);", conn);
        cmd.Parameters.AddWithValue("id", id);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : (string)value;
    }

    private static async Task<(string Label, short? Tier, string? Type, bool Exists)> ReadEntityFacetsAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT f.tier, laplace.render(f.type_id), laplace.label_or_hex(@id), laplace.entity_exists(@id) FROM laplace.entity_facets(@id) f;", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            var label = await ReadLabelAsync(conn, id, ct);
            return label is null ? (null!, null, null, false) : (label, null, null, false);
        }

        return (
            Label: reader.GetString(2),
            Tier: reader.GetInt16(0),
            Type: reader.GetString(1),
            Exists: reader.GetBoolean(3));
    }

    private static async Task<long> ReadEvidenceCountAsync(NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT laplace.evidence_count(NULL, NULL, @id);", conn);
        cmd.Parameters.AddWithValue("id", id);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0L : Convert.ToInt64(value);
    }

    private static async Task<IReadOnlyList<SalientFactRow>> ReadSalientFactsAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT type, fact, eff_mu, witnesses
            FROM laplace.salient_facts(@id, NULL, @limit);
            """;
        var facts = new List<SalientFactRow>(limit);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            facts.Add(new SalientFactRow(
                Type: reader.GetString(0),
                Fact: reader.GetString(1),
                EffMu: reader.GetDecimal(2),
                Witnesses: reader.GetInt64(3)));
        }

        return facts;
    }

    private static async Task<IReadOnlyList<ExplorePhysicalityRow>> ReadPhysicalitiesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        const string sql = """
            SELECT p.type, p.x, p.y, p.z, p.m, p.radius, p.n_constituents
            FROM laplace.entity_physicalities(@id) p
            ORDER BY p.type;
            """;
        var rows = new List<ExplorePhysicalityRow>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ExplorePhysicalityRow(
                Type: reader.GetInt16(0),
                X: reader.GetDouble(1),
                Y: reader.GetDouble(2),
                Z: reader.GetDouble(3),
                M: reader.GetDouble(4),
                Radius: reader.GetDouble(5),
                Constituents: reader.GetInt32(6)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<ExploreConsensusRow>> ReadConsensusAsync(
        NpgsqlConnection conn, byte[] id, string direction, int limit, CancellationToken ct)
    {
        var rows = new List<ExploreConsensusRow>(limit);
        if (direction == "out")
        {
            const string sql = """
                SELECT c.type_label, c.object_label, encode(c.object_id, 'hex'),
                       c.eff_mu, c.witnesses
                FROM laplace.consensus_out_labeled(@id, @limit) c;
                """;
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new ExploreConsensusRow(
                    Direction: "out",
                    Type: reader.GetString(0),
                    EntityIdHex: reader.GetString(2),
                    EntityLabel: reader.GetString(1),
                    EffMu: reader.GetDecimal(3),
                    Witnesses: reader.GetInt64(4)));
            }
        }
        else
        {
            const string sql = """
                SELECT encode(c.subject_id, 'hex'),
                       laplace.type_label(c.type_id),
                       COALESCE(
                           NULLIF(laplace._realize_synset_lemma(c.subject_id, laplace.word_language(@id)), ''),
                           NULLIF(laplace.render_text(c.subject_id, 12), ''),
                           left(encode(c.subject_id, 'hex'), 16)),
                       laplace.eff_mu_display(c.rating, c.rd), c.witness_count
                FROM laplace.consensus_in(@id, @limit) c;
                """;
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new ExploreConsensusRow(
                    Direction: "in",
                    Type: reader.GetString(1),
                    EntityIdHex: reader.GetString(0),
                    EntityLabel: reader.GetString(2),
                    EffMu: reader.GetDecimal(3),
                    Witnesses: reader.GetInt64(4)));
            }
        }

        return rows;
    }

    private static async Task<IReadOnlyList<ExploreSenseRow>> ReadSensesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        const string sql = """
            SELECT encode(s.sense_id, 'hex'), encode(s.synset_id, 'hex'),
                   COALESCE(
                       NULLIF(laplace._realize_synset_lemma(s.synset_id, laplace.word_language(@id)), ''),
                       NULLIF(laplace.render_text(s.synset_id, 12), ''),
                       left(encode(s.synset_id, 'hex'), 16)),
                   s.eff_mu, s.witnesses
            FROM laplace.senses(@id) s;
            """;
        var rows = new List<ExploreSenseRow>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ExploreSenseRow(
                SenseIdHex: reader.GetString(0),
                SynsetIdHex: reader.GetString(1),
                SynsetLabel: reader.GetString(2),
                EffMu: reader.GetDecimal(3),
                Witnesses: reader.GetInt64(4)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<ExploreConstituentRow>> ReadConstituentsAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct)
    {
        const string sql = """
            SELECT c.ordinal, encode(c.child_id, 'hex'), c.run_length, c.flags,
                   COALESCE(
                       NULLIF(laplace.render_text(c.child_id, 12), ''),
                       left(encode(c.child_id, 'hex'), 16))
            FROM laplace.constituents(@id) c
            LIMIT 512;
            """;
        var rows = new List<ExploreConstituentRow>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ExploreConstituentRow(
                Ordinal: reader.GetInt32(0),
                ChildIdHex: reader.GetString(1),
                ChildLabel: reader.GetString(4),
                RunLength: reader.GetInt32(2),
                Flags: reader.GetInt64(3)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<LabeledEvidenceItem>> ReadEvidenceItemsAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT encode(e.type_id, 'hex'), e.type_label,
                   encode(e.object_id, 'hex'), e.object_label,
                   e.source_labels, e.witness_count, e.eff_mu
            FROM laplace.evidence_receipt(@id, @limit) e;
            """;
        var items = new List<LabeledEvidenceItem>(limit);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new LabeledEvidenceItem(
                TypeId: reader.GetString(0),
                TypeLabel: reader.GetString(1),
                ObjectId: reader.GetString(2),
                ObjectLabel: reader.GetString(3),
                SourceId: "",
                SourceLabel: reader.IsDBNull(4) ? "" : reader.GetString(4),
                ContextId: null,
                Outcome: 2,
                ObservationCount: reader.GetInt64(5),
                EffMu: reader.GetDecimal(6)));
        }

        return items;
    }
}
