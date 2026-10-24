using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{

    /// <summary><c>ops.modality_counts()</c> — corpus modality breakdown, one row.</summary>
    public static async Task<(long TextEvidence, long Chess, long Models, long Multilingual, long Documents)> ModalityCountsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource,
            SqlCatalog.Get("ops.modality_counts"),
            static r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)),
            timeoutSeconds: 20, ct: ct, label: "modality_counts", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? (0, 0, 0, 0, 0) : rows[0];
    }

    /// <summary><c>ops.substrate_pulse()</c> — the live scoreboard, one row.</summary>
    public static async Task<(long Entities, long? Attestations, long Consensus, long Physicalities,
        long? LastFlushUnix, long FlushesLastMin, bool Folding)?> SubstratePulseAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.substrate_pulse"),
            static r => (
                r.IsDBNull(0) ? 0L : r.GetInt64(0),
                r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2),
                r.IsDBNull(3) ? 0L : r.GetInt64(3),
                r.IsDBNull(4) ? (long?)null : r.GetInt64(4),
                r.IsDBNull(5) ? 0L : r.GetInt64(5),
                !r.IsDBNull(6) && r.GetBoolean(6)),
            ct: ct, label: "substrate_pulse", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>ops.entity_type_counts_approx()</c> — type census estimated from planner statistics (MCV × reltuples).</summary>
    public static Task<IReadOnlyList<(string Type, long EntitiesApprox)>> EntityTypeCountsApproxAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.entity_type_counts_approx"),
            static r => (r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? 0L : r.GetInt64(1)),
            ct: ct, label: "entity_type_counts_approx", onError: onError);

    /// <summary><c>ops.partition_pressure()</c> — each partition's share of its parent, from reltuples.</summary>
    public static Task<IReadOnlyList<(string Parent, string Partition, decimal? Pct)>> PartitionPressureAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.partition_pressure"),
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? (decimal?)null : r.GetDecimal(2)),
            ct: ct, label: "partition_pressure", onError: onError);

    /// <summary>
    /// <c>laplace.perfcache_receipt()</c> — checksum of the mapped Tier-0 perfcache image,
    /// read from the native receipt without reconstructing any geometry.
    /// </summary>
    public static async Task<string?> PerfcacheReceiptHexAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource,
            SqlCatalog.Get("laplace.perfcache_receipt"),
            static r => r.IsDBNull(0) ? null : r.GetString(0),
            ct: ct, label: "perfcache_receipt", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>laplace.atom_census()</c> — Tier-0 entity count against the admitted atom window.</summary>
    public static async Task<(long Tier0, long Window, long Over, long Unresolvable)?> AtomCensusAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("laplace.atom_census"),
            static r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)),
            ct: ct, label: "atom_census", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>ops.source_tier_census(source)</c> — entities by tier under one source.</summary>
    public static Task<IReadOnlyList<(short Tier, long Entities)>> SourceTierCensusAsync(
        NpgsqlDataSource dataSource, byte[] sourceId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.source_tier_census"),
            static r => (r.GetInt16(0), r.GetInt64(1)),
            p => p.AddWithValue("source", sourceId),
            ct: ct, label: "source_tier_census", onError: onError);

    /// <summary><c>ops.surface_sample(source, tier, limit)</c> — a source's surfaces at one tier, ranked by observations.</summary>
    public static Task<IReadOnlyList<(string Surface, string TypeName, long Observations)>> SurfaceSampleAsync(
        NpgsqlDataSource dataSource, byte[] sourceId, short tier, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.surface_sample"),
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            p =>
            {
                p.AddWithValue("source", sourceId);
                p.AddWithValue("tier", tier);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "surface_sample", onError: onError);

    /// <summary><c>ops.arena_counts()</c> — consensus cells and witnesses per relation type.</summary>
    public static Task<IReadOnlyList<(string Type, long Relations, long Witnesses)>> ArenaCountsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.arena_counts"),
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? 0L : r.GetInt64(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            ct: ct, label: "arena_counts", onError: onError);

    /// <summary><c>ops.ingest_runs(limit)</c> — recent ingest journal rows.</summary>
    public static Task<IReadOnlyList<(string Source, string Status, DateTimeOffset Started)>> IngestRunsAsync(
        NpgsqlDataSource dataSource, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.ingest_runs"),
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.GetFieldValue<DateTimeOffset>(2)),
            p => p.AddWithValue("limit", limit),
            ct: ct, label: "ingest_runs", onError: onError);

    /// <summary><c>ops.consensus_count(type)</c> — consensus row count, optional type filter.</summary>
    public static async Task<long> ConsensusCountAsync(
        NpgsqlDataSource dataSource, byte[]? typeId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.consensus_count"),
            static r => r.IsDBNull(0) ? 0L : r.GetInt64(0),
            p => p.Add(new NpgsqlParameter("type", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = (object?)typeId ?? DBNull.Value }),
            ct: ct, label: "consensus_count", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? 0L : rows[0];
    }

    /// <summary><c>laplace.compositional_tier_distribution()</c></summary>
    public static Task<IReadOnlyList<(short Tier, long N)>> CompositionalTierDistributionAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("laplace.compositional_tier_distribution"),
            static r => (r.GetInt16(0), r.GetInt64(1)),
            ct: ct, label: "compositional_tier_distribution", onError: onError);

    /// <summary><c>ops.consensus_tier_distribution()</c></summary>
    public static Task<IReadOnlyList<(short Tier, long Relations)>> ConsensusTierDistributionAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.consensus_tier_distribution"),
            static r => (r.GetInt16(0), r.GetInt64(1)),
            ct: ct, label: "consensus_tier_distribution", onError: onError);

    /// <summary><c>realize.render_gaps(limit)</c> — ids that consensus references but cannot render.</summary>
    public static Task<IReadOnlyList<(string IdHex, string Roles, long Refs)>> RenderGapsAsync(
        NpgsqlDataSource dataSource, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("realize.render_gaps"),
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            p => p.AddWithValue("limit", limit),
            ct: ct, label: "render_gaps", onError: onError);

    /// <summary><c>ops.entity_type_counts()</c> — exact (slow); prefer EntityTypeCountsApproxAsync.</summary>
    public static Task<IReadOnlyList<(string Type, short Tier, long Entities)>> EntityTypeCountsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.entity_type_counts"),
            static r => (r.IsDBNull(0) ? "" : r.GetString(0), r.GetInt16(1), r.GetInt64(2)),
            ct: ct, label: "entity_type_counts", onError: onError);

    /// <summary><c>structural.entities_at_depth(tier)</c>.</summary>
    public static async Task<long> EntitiesAtDepthCountAsync(
        NpgsqlDataSource dataSource, short depth, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("structural.entities_at_depth_count"),
            static r => r.IsDBNull(0) ? 0L : r.GetInt64(0),
            p => p.AddWithValue("d", depth),
            ct: ct, label: "laplace_entities_at_depth", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? 0L : rows[0];
    }

    public readonly record struct SourceRosterRow(
        string SubjectIdHex, string Subject, string Relation,
        string ObjectIdHex, string Object, long Observations);

    /// <summary><c>ops.source_roster(source_id, limit)</c> — a bounded sample of what a source witnessed.</summary>
    public static Task<IReadOnlyList<SourceRosterRow>> SourceRosterAsync(
        NpgsqlDataSource dataSource, byte[] sourceId, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.source_roster"),
            static r => new SourceRosterRow(
                r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2),
                r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4), r.GetInt64(5)),
            p =>
            {
                p.Add("sid", NpgsqlDbType.Bytea).Value = sourceId;
                p.AddWithValue("lim", limit);
            }, timeoutSeconds: 30, ct: ct, label: "source_roster", onError: onError);

    // --- Catalog / inventory: substrate_counts, consensus stats, source counts ---
    // Connection overloads let a caller run several of these on one connection, sharing
    // session state such as TEMP scopes.

    public readonly record struct MetricCountRow(string Metric, long Value);

    /// <summary><c>ops.substrate_counts()</c> — inventory metrics (planner-labeled).</summary>
    public static Task<IReadOnlyList<MetricCountRow>> SubstrateCountsAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("ops.substrate_counts"),
            static r => new MetricCountRow(r.GetString(0), r.GetInt64(1)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "substrate_counts", onError: onError);

    /// <inheritdoc cref="SubstrateCountsAsync(NpgsqlConnection, CancellationToken, int, NpgsqlRead.ErrorTranslator?)"/>
    public static Task<IReadOnlyList<MetricCountRow>> SubstrateCountsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource,
            SqlCatalog.Get("ops.substrate_counts"),
            static r => new MetricCountRow(r.GetString(0), r.GetInt64(1)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "substrate_counts", onError: onError);

    public readonly record struct ConsensusStatsRow(
        long EvidenceRows, long ConsensusRows, decimal? DedupRatio,
        decimal? AvgWitnesses, long? MaxWitnesses);

    private static ConsensusStatsRow MapConsensusStats(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1),
        r.IsDBNull(2) ? null : r.GetDecimal(2),
        r.IsDBNull(3) ? null : r.GetDecimal(3),
        r.IsDBNull(4) ? null : r.GetInt64(4));

    /// <summary><c>consensus.stats()</c> — exact full aggregates (minutes at scale).</summary>
    public static async Task<ConsensusStatsRow?> ConsensusStatsExactAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("consensus.stats"),
            MapConsensusStats, timeoutSeconds: timeoutSeconds, ct: ct,
            label: "consensus_stats", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>consensus.stats_approx()</c> — planner estimates; avg/max may be NULL.</summary>
    public static async Task<ConsensusStatsRow?> ConsensusStatsApproxAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("consensus.stats_approx"),
            MapConsensusStats, timeoutSeconds: timeoutSeconds, ct: ct,
            label: "consensus_stats_approx", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct SourceCountRow(
        string Source, long Evidence, long? Content, string? IdHex);

    /// <summary><c>ops.source_counts()</c> — exact per-source evidence+content (unbounded).</summary>
    public static Task<IReadOnlyList<SourceCountRow>> SourceCountsAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("ops.source_counts"),
            static r => new SourceCountRow(
                SourceLabel(r, 0, 3), r.GetInt64(1), r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetString(3)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "source_counts", onError: onError);

    /// <summary>
    /// A source's label is one realization of its entity; when none exists the row is
    /// labeled with the source id instead of being dropped.
    /// </summary>
    private static string SourceLabel(NpgsqlDataReader r, int label, int idHex) =>
        !r.IsDBNull(label) ? r.GetString(label) : r.IsDBNull(idHex) ? "" : r.GetString(idHex);

    /// <summary><c>ops.source_counts_approx()</c> — partition-stats evidence; content unknown.</summary>
    public static Task<IReadOnlyList<SourceCountRow>> SourceCountsApproxAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("ops.source_counts_approx"),
            static r => new SourceCountRow(
                SourceLabel(r, 0, 2), r.GetInt64(1), null,
                r.IsDBNull(2) ? null : r.GetString(2)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "source_counts_approx", onError: onError);

    /// <summary><c>ops.multi_source_entity_count()</c> — subjects with ≥2 sources.</summary>
    public static Task<long?> MultiSourceEntityCountAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<long?>(conn,
            SqlCatalog.Get("ops.multi_source_entity_count"),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "multi_source_entity_count", onError: onError);

    /// <summary>
    /// <c>ops.eval_ingest_fidelity_positives</c> — consensus scores on
    /// <paramref name="relation"/> for pairs that <paramref name="groundTruth"/> asserts.
    /// </summary>
    public static Task<IReadOnlyList<double>> IngestFidelityPositiveScoresAsync(
        NpgsqlDataSource dataSource, string relation, string groundTruth, int n,
        CancellationToken ct = default, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource,
            SqlCatalog.Get("ops.eval_ingest_fidelity_positives"),
            static r => r.GetDouble(0),
            p =>
            {
                p.AddWithValue("rel", relation);
                p.AddWithValue("gt", groundTruth);
                p.AddWithValue("n", n);
            }, ct: ct, label: "eval_ingest_fidelity_pos", onError: onError);

    /// <summary><c>ops.eval_ingest_fidelity_negatives</c> — consensus scores on
    /// <paramref name="relation"/> for random vocabulary pairs.</summary>
    public static Task<IReadOnlyList<double>> IngestFidelityNegativeScoresAsync(
        NpgsqlDataSource dataSource, string relation, string groundTruth, int n,
        CancellationToken ct = default, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource,
            SqlCatalog.Get("ops.eval_ingest_fidelity_negatives"),
            static r => r.GetDouble(0),
            p =>
            {
                p.AddWithValue("rel", relation);
                p.AddWithValue("n", n);
            }, ct: ct, label: "eval_ingest_fidelity_neg", onError: onError);

    /// <summary>
    /// Probe committed row presence independently of planner statistics.
    /// </summary>
    public static async Task<(bool EntitiesExist, bool ConsensusExist)> EntitiesAndConsensusExistAsync(
        NpgsqlConnection conn, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("readiness.entities_and_consensus"),
            static r => (r.GetBoolean(0), r.GetBoolean(1)),
            timeoutSeconds: 30, ct: ct, label: "substrate_presence", onError: onError).ConfigureAwait(false);
        return rows[0];
    }

    public readonly record struct ReadinessInventory(
        long EstimatedEntities, long EstimatedConsensus, bool HasEntities, bool HasConsensus);

    /// <summary>One inventory query: estimates describe size; EXISTS determines presence.</summary>
    public static async Task<ReadinessInventory> ReadinessInventoryAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("readiness.inventory"), static r => new ReadinessInventory(r.GetInt64(0), r.GetInt64(1), r.GetBoolean(2), r.GetBoolean(3)),
            timeoutSeconds: 30, ct: ct, label: "readiness_inventory").ConfigureAwait(false);
        return rows[0];
    }

    /// <summary>
    /// <c>laplace.word_id('the')</c> as a T0 perfcache liveness probe. No error
    /// translation: the caller catches the specific ObjectNotInPrerequisiteState
    /// PostgresException to mean "not loaded yet", which is not a failure at this layer.
    /// </summary>
    public static Task<object?> PerfCacheProbeAsync(NpgsqlConnection conn, CancellationToken ct) =>
        NpgsqlRead.ExecuteScalarAsync<object>(conn,
            SqlCatalog.Get("readiness.perfcache_probe"), ct: ct, label: "perfcache_probe");

    /// <summary>
    /// One installed operation. <paramref name="Kind"/> decides the statement: a
    /// procedure is invoked with CALL, a function with SELECT.
    /// </summary>
    public readonly record struct ApiCatalogRow(string Name, string? Args, string? Returns, string? Kind);

    /// <summary><c>ops.api(query)</c> — installed-function catalog search.</summary>
    public static Task<IReadOnlyList<ApiCatalogRow>> ApiCatalogAsync(
        NpgsqlDataSource dataSource, string query, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.api_catalog"),
            static r => new ApiCatalogRow(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3)),
            p => p.AddWithValue("q", query),
            ct: ct, label: "api_catalog", onError: onError);

    /// <summary>
    /// A health metric. <paramref name="Value"/> is null for a metric the health pass did
    /// not measure, which is a different fact from zero or false.
    /// </summary>
    public readonly record struct HealthMetricRow(string Metric, string? Value);

    /// <summary>One source's ingest state — see <c>ops.source_status()</c>.</summary>
    public readonly record struct SourceStatusRow(
        string Source, byte[] SourceId, bool Known, bool Ingested,
        long EvidenceApprox, bool HasEntities, string? LastRunStatus, DateTime? LastRunAt,
        string? ThroughputStatus, bool ThroughputCompared, double? ThroughputRowsPerS,
        double? ThroughputBaselineRowsPerS, double? ThroughputSlowdownRatio);

    /// <summary>
    /// <c>ops.source_status()</c> — whether a source is known and ingested, with the
    /// evidence, entity and last-run facts behind that answer. A source with only content
    /// and no evidence still reads as ingested through <c>HasEntities</c>. Asking with a
    /// name always returns exactly one row, so an unknown source is an answer
    /// (<c>Known = false</c>), not an empty result.
    /// </summary>
    public static Task<IReadOnlyList<SourceStatusRow>> SourceStatusAsync(
        NpgsqlDataSource dataSource, string? source, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource,
            SqlCatalog.Get("ops.source_status"),
            static r => new SourceStatusRow(
                r.GetString(0), (byte[])r[1], r.GetBoolean(2), r.GetBoolean(3),
                r.GetInt64(4), r.GetBoolean(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetDateTime(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.GetBoolean(9),
                r.IsDBNull(10) ? null : r.GetDouble(10),
                r.IsDBNull(11) ? null : r.GetDouble(11),
                r.IsDBNull(12) ? null : r.GetDouble(12)),
            p => p.Add("s", NpgsqlDbType.Text).Value = (object?)source ?? DBNull.Value,
            ct: ct, label: "source_status", onError: onError);

    /// <summary><c>laplace.substrate_health()</c> flattened to metric/value rows.</summary>
    public static Task<IReadOnlyList<HealthMetricRow>> SubstrateHealthAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("laplace.substrate_health_metrics"),
            // identity_violations is null when deep_checked is false; a null value maps
            // to a null metric ("not measured") rather than a GetString exception.
            static r => new HealthMetricRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)),
            ct: ct, label: "substrate_health", onError: onError);

    public readonly record struct HighwayPopulationRow(
        bool RegistryReady,
        bool HistoricalPopulationComplete,
        DateTimeOffset? CompletedAt,
        long PendingPairs,
        long PendingRefreshes);

    public static Task<IReadOnlyList<HighwayPopulationRow>> HighwayPopulationAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.highway_population"),
            static r => new HighwayPopulationRow(
                r.GetBoolean(0),
                r.GetBoolean(1),
                r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
                r.GetInt64(3),
                r.GetInt64(4)),
            ct: ct, label: "highway_population", onError: onError);

    public readonly record struct EntityTypeCountRow(byte[] TypeId, long Count);

    /// <summary>
    /// <c>ops.entity_counts_by_types</c> — entity count per requested type_id. Every
    /// requested type returns a row, zero included.
    /// </summary>
    public static Task<IReadOnlyList<EntityTypeCountRow>> EntityCountsByTypesAsync(
        NpgsqlDataSource dataSource, byte[][] typeIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.entity_counts_by_types"),
            static r => new EntityTypeCountRow((byte[])r[0], r.GetInt64(1)),
            p =>
            {
                var parameter = p.AddWithValue("types", typeIds);
                parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            },
            ct: ct, label: "entity_counts_by_types", onError: onError,
            timeoutSeconds: 30);

    /// <summary><c>generation.model_jitter_catalog(relation)</c>.</summary>
    public static Task<IReadOnlyList<(string SubjectHex, string TypeHex, long WitnessCount)>> ModelJitterCatalogAsync(
        NpgsqlDataSource dataSource, string? relation, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("generation.model_jitter_catalog"),
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            p => p.Add(new NpgsqlParameter("rel", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)relation ?? DBNull.Value }),
            ct: ct, label: "model_jitter_catalog", onError: onError);

    public readonly record struct ModelRecipeRow(byte[] RecipeId, string RecipeJson);

    /// <summary><c>structural.model_recipes()</c>.</summary>
    public static Task<IReadOnlyList<ModelRecipeRow>> ModelRecipesAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("structural.model_recipes"),
            static r => new ModelRecipeRow((byte[])r[0], r.GetString(1)),
            ct: ct, label: "model_recipes", onError: onError,
            timeoutSeconds: 30);
}
