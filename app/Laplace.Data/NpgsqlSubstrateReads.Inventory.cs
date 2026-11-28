using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

public static partial class NpgsqlSubstrateReads
{
    public static async Task<(long TextEvidence, long Chess, long Models, long Multilingual, long Documents)> ModalityCountsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT coalesce(w.readings, 0)::bigint, 0::bigint, 0::bigint, 0::bigint, 0::bigint
            FROM (SELECT sum(readings)::bigint AS readings FROM public.laplace_witnesses_named()) w
            """;
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)),
            timeoutSeconds: 20, ct: ct, label: "modality_counts", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? (0, 0, 0, 0, 0) : rows[0];
    }

    public static async Task<(long Entities, long? Attestations, long Consensus, long Physicalities,
        long? LastFlushUnix, long FlushesLastMin, bool Folding)?> SubstratePulseAsync(
        NpgsqlDataSource dataSource, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH est AS (SELECT public.laplace_estate_summary(false) AS j)
            SELECT coalesce((j->>'entities')::bigint, (SELECT reltuples::bigint FROM pg_catalog.pg_class WHERE oid='public.entity'::regclass), 0)::bigint,
                   coalesce((j->>'attestations')::bigint, (SELECT reltuples::bigint FROM pg_catalog.pg_class WHERE oid='public.attestation'::regclass), 0)::bigint,
                   coalesce((j->>'consensus')::bigint, (SELECT reltuples::bigint FROM pg_catalog.pg_class WHERE oid='public.consensus'::regclass), 0)::bigint,
                   coalesce((j->>'physicalities')::bigint, (SELECT reltuples::bigint FROM pg_catalog.pg_class WHERE oid='public.physicality'::regclass), 0)::bigint,
                   EXTRACT(EPOCH FROM now())::bigint,
                   0::bigint,
                   false
            FROM est
            """;
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, sql,
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

    public static async Task<string?> PerfcacheReceiptHexAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource,
            "SELECT public.laplace_fingerprint()",
            static r => r.IsDBNull(0) ? null : r.GetString(0),
            ct: ct, label: "perfcache_receipt", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct SourceRosterRow(
        string SubjectIdHex, string Subject, string Relation,
        string ObjectIdHex, string Object, long Observations);

    public static Task<IReadOnlyList<SourceRosterRow>> SourceRosterAsync(
        NpgsqlDataSource dataSource, byte[] sourceId, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH att AS MATERIALIZED (
                SELECT s.claim, greatest(1, round(s.games)::bigint) AS games
                FROM public.laplace_contained_series(ARRAY[public.blake3_from_bytea($1)]) s
                WHERE s.games > 0
                LIMIT greatest(0, $2) * 2
            ),
            parts AS MATERIALIZED (
                SELECT a.games, public.laplace_parts(p.path) AS pts
                FROM att a
                JOIN public.physicality p ON p.entity = a.claim
                LIMIT greatest(0, $2)
            )
            SELECT encode(public.blake3_send(pts[1]), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(pts[1]), false), ''),
                   coalesce(public.laplace_label_text(public.blake3_send(pts[2]), false), encode(public.blake3_send(pts[2]), 'hex')),
                   encode(public.blake3_send(pts[cardinality(pts)]), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(pts[cardinality(pts)]), false), ''),
                   games
            FROM parts
            WHERE cardinality(pts) >= 2
            LIMIT greatest(0, $2)
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new SourceRosterRow(
                r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2),
                r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4), r.GetInt64(5)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = sourceId });
                p.AddWithValue(limit);
            }, timeoutSeconds: 30, ct: ct, label: "source_roster", onError: onError);
    }

    public readonly record struct MetricCountRow(string Metric, long? Value);

    private const string SubstrateCountsSql = """
        WITH est AS (SELECT public.laplace_estate_summary(false) AS j)
        SELECT key, (value::text)::bigint
        FROM est, jsonb_each(j)
        WHERE jsonb_typeof(value) = 'number'
        ORDER BY key
        """;

    public static Task<IReadOnlyList<MetricCountRow>> SubstrateCountsAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SubstrateCountsSql,
            static r => new MetricCountRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "substrate_counts", onError: onError);

    public static Task<IReadOnlyList<MetricCountRow>> SubstrateCountsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SubstrateCountsSql,
            static r => new MetricCountRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "substrate_counts", onError: onError);

    public readonly record struct ConsensusStatsRow(
        long? EvidenceRows, long ConsensusRows, decimal? DedupRatio,
        decimal? AvgWitnesses, long? MaxWitnesses);

    private static ConsensusStatsRow MapConsensusStats(NpgsqlDataReader r) => new(
        r.IsDBNull(0) ? null : r.GetInt64(0), r.GetInt64(1),
        r.IsDBNull(2) ? null : r.GetDecimal(2),
        r.IsDBNull(3) ? null : r.GetDecimal(3),
        r.IsDBNull(4) ? null : r.GetInt64(4));

    public static async Task<ConsensusStatsRow?> ConsensusStatsExactAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH att AS (
                SELECT (public.laplace_estate_summary(false)->>'attestations')::bigint AS ev
            ),
            est AS (
                SELECT (SELECT ev FROM att) AS ev,
                       count(*)::bigint AS con,
                       avg(matches)::numeric AS avg_w,
                       max(matches)::bigint AS max_w
                FROM public.consensus
                WHERE matches > 0
            )
            SELECT ev, con, CASE WHEN con > 0 AND ev IS NOT NULL THEN round(ev::numeric / con, 2) ELSE NULL END, avg_w, max_w
            FROM est
            """;
        var rows = await NpgsqlRead.ReadRowsAsync(conn, sql,
            MapConsensusStats, timeoutSeconds: timeoutSeconds, ct: ct,
            label: "consensus_stats", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public static async Task<ConsensusStatsRow?> ConsensusStatsApproxAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH est AS (SELECT public.laplace_estate_summary(false) AS j)
            SELECT (j->>'attestations')::bigint AS ev,
                   coalesce((j->>'consensus')::bigint, 0)::bigint AS con,
                   CASE WHEN coalesce((j->>'consensus')::bigint, 0) > 0 AND (j->>'attestations') IS NOT NULL
                        THEN round(((j->>'attestations')::bigint)::numeric / ((j->>'consensus')::bigint), 2)
                        ELSE NULL END,
                   NULL::numeric,
                   NULL::bigint
            FROM est
            """;
        var rows = await NpgsqlRead.ReadRowsAsync(conn, sql,
            MapConsensusStats, timeoutSeconds: timeoutSeconds, ct: ct,
            label: "consensus_stats_approx", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct SourceCountRow(string Source, long? Evidence, long? Content, string? IdHex);

    public static Task<IReadOnlyList<SourceCountRow>> SourceCountsApproxAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT coalesce(nullif(name, ''), encode(public.blake3_send(witness), 'hex')),
                   readings,
                   parts,
                   encode(public.blake3_send(witness), 'hex')
            FROM public.laplace_witnesses_named()
            ORDER BY readings DESC NULLS LAST, name
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new SourceCountRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "source_counts_approx", onError: onError);
    }

    public static Task<long?> MultiSourceEntityCountAsync(
        NpgsqlConnection conn, CancellationToken ct,
        int timeoutSeconds = 0, NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT count(*)::bigint
            FROM public.consensus
            WHERE matches > 0 AND matches >= 2
            """;
        return NpgsqlRead.ExecuteScalarAsync<long?>(conn, sql,
            timeoutSeconds: timeoutSeconds, ct: ct, label: "multi_source_entity_count", onError: onError);
    }

    public static async Task<(bool EntitiesExist, bool ConsensusExist)> EntitiesAndConsensusExistAsync(
        NpgsqlConnection conn, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn,
            "SELECT has_entities, has_standings FROM public.laplace_nonempty()",
            static r => (r.GetBoolean(0), r.GetBoolean(1)),
            timeoutSeconds: 30, ct: ct, label: "substrate_presence", onError: onError).ConfigureAwait(false);
        return rows[0];
    }

    public readonly record struct ReadinessInventory(
        long EstimatedEntities, long EstimatedConsensus, bool HasEntities, bool HasConsensus,
        string[] MissingNativeApi, string[] MissingNativeIndexes);

    public static async Task<ReadinessInventory> ReadinessInventoryAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            WITH ne AS (SELECT has_entities, has_standings FROM public.laplace_nonempty()),
                 est AS (SELECT public.laplace_estate_summary(false) AS j),
                 req_api AS (
                     SELECT array_agg(fn) FILTER (
                         WHERE NOT EXISTS (
                             SELECT 1 FROM pg_catalog.pg_proc p
                             JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                             WHERE n.nspname = 'public' AND p.proname = fn
                         )
                     ) AS missing_api
                     FROM unnest(ARRAY['laplace_couple', 'laplace_salience', 'laplace_pull', 'laplace_standings', 'laplace_labels']) AS fn
                 ),
                 bad_idx AS (
                     SELECT array_agg(n.nspname || '.' || ic.relname) FILTER (WHERE NOT i.indisvalid OR NOT i.indisready) AS missing_idx
                     FROM pg_catalog.pg_index i
                     JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid
                     JOIN pg_catalog.pg_class tc ON tc.oid = i.indrelid
                     JOIN pg_catalog.pg_namespace n ON n.oid = tc.relnamespace
                     WHERE n.nspname = 'public' AND tc.relname IN ('entity', 'physicality', 'attestation', 'consensus')
                 )
            SELECT coalesce((e.j->>'entities')::bigint, 0),
                   coalesce((e.j->>'consensus')::bigint, 0),
                   ne.has_entities,
                   ne.has_standings,
                   coalesce(ra.missing_api, ARRAY[]::text[]),
                   coalesce(bi.missing_idx, ARRAY[]::text[])
            FROM ne, est e, req_api ra, bad_idx bi
            """;
        var rows = await NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new ReadinessInventory(
                r.GetInt64(0), r.GetInt64(1), r.GetBoolean(2), r.GetBoolean(3),
                r.GetFieldValue<string[]>(4), r.GetFieldValue<string[]>(5)),
            timeoutSeconds: 30, ct: ct, label: "readiness_inventory").ConfigureAwait(false);
        return rows[0];
    }

    public static Task<object?> PerfCacheProbeAsync(NpgsqlConnection conn, CancellationToken ct) =>
        NpgsqlRead.ExecuteScalarAsync<object>(conn,
            "SELECT public.blake3_send(public.laplace_text_id('the'))",
            ct: ct, label: "perfcache_probe");

    public readonly record struct ApiCatalogRow(string Name, string? Args, string? Returns, string? Kind);

    private static ApiCatalogRow ReadApiCatalogRow(NpgsqlDataReader r) => new(
        r.GetString(0),
        r.IsDBNull(1) ? null : r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3));

    private const string ApiCatalogSql = """
        SELECT CASE WHEN n.nspname = 'public' THEN p.proname ELSE n.nspname || '.' || p.proname END AS name,
               pg_catalog.pg_get_function_arguments(p.oid) AS args,
               pg_catalog.pg_get_function_result(p.oid) AS returns,
               CASE p.prokind WHEN 'p' THEN 'procedure' ELSE 'function' END AS kind
        FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
        WHERE ((n.nspname = 'public' AND (p.proname LIKE 'laplace_%' OR p.proname LIKE 'blake3%'))
               OR n.nspname = 'ops')
          AND ($1 = '' OR position(lower($1) IN lower(n.nspname || '.' || p.proname)) > 0)
        ORDER BY name, args
        """;

    public static Task<IReadOnlyList<ApiCatalogRow>> ApiCatalogAsync(
        NpgsqlDataSource dataSource, string query, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, ApiCatalogSql, ReadApiCatalogRow,
            p => p.AddWithValue(query), ct: ct, label: "api_catalog", onError: onError);

    public static Task<IReadOnlyList<ApiCatalogRow>> ApiCatalogAsync(
        NpgsqlConnection connection, string query, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(connection, ApiCatalogSql, ReadApiCatalogRow,
            p => p.AddWithValue(query), ct: ct, label: "api_catalog", onError: onError);

    public readonly record struct HealthMetricRow(string Metric, string? Value);

    public readonly record struct SourceStatusRow(
        string Source, byte[] SourceId, bool Known, bool Ingested,
        long? EvidenceApprox, bool HasEntities, string? LastRunStatus, DateTime? LastRunAt,
        string? ThroughputStatus, bool ThroughputCompared, double? ThroughputRowsPerS,
        double? ThroughputBaselineRowsPerS, double? ThroughputSlowdownRatio);

    public static Task<IReadOnlyList<SourceStatusRow>> SourceStatusAsync(
        NpgsqlDataSource dataSource, string? source, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH witnesses AS (
                SELECT coalesce(nullif(w.name, ''), encode(public.blake3_send(w.witness), 'hex')) AS src_name,
                       public.blake3_send(w.witness) AS src_id,
                       w.readings, w.parts
                FROM public.laplace_witnesses_named() w
            )
            SELECT src_name, src_id, true, (coalesce(readings, 0) > 0), readings,
                   (coalesce(parts, 0) > 0), NULL::text, NULL::timestamp,
                   NULL::text, false, NULL::float8, NULL::float8, NULL::float8
            FROM witnesses
            WHERE $1 IS NULL OR lower(src_name) = lower($1)
            ORDER BY readings DESC NULLS LAST, src_name
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new SourceStatusRow(
                r.GetString(0), (byte[])r[1], r.GetBoolean(2), r.GetBoolean(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.GetBoolean(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetDateTime(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.GetBoolean(9),
                r.IsDBNull(10) ? null : r.GetDouble(10),
                r.IsDBNull(11) ? null : r.GetDouble(11),
                r.IsDBNull(12) ? null : r.GetDouble(12)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)source ?? DBNull.Value }),
            ct: ct, label: "source_status", onError: onError);
    }

    public static Task<IReadOnlyList<HealthMetricRow>> SubstrateHealthAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT 'fingerprint', public.laplace_fingerprint()
            UNION ALL
            SELECT 'has_entities', has_entities::text FROM public.laplace_nonempty()
            UNION ALL
            SELECT 'has_standings', has_standings::text FROM public.laplace_nonempty()
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new HealthMetricRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)),
            ct: ct, label: "substrate_health", onError: onError);
    }

    public readonly record struct HighwayPopulationRow(
        bool RegistryReady,
        bool HistoricalPopulationComplete,
        DateTimeOffset? CompletedAt,
        long PendingPairs,
        long PendingRefreshes);

    public static Task<IReadOnlyList<HighwayPopulationRow>> HighwayPopulationAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT public.laplace_highway_fingerprint() IS NOT NULL,
                   EXISTS (
                       SELECT 1 FROM public.entity
                       WHERE mask OPERATOR(public.?|) ARRAY[
                           public.laplace_bank_bit('upos', 'NOUN'),
                           public.laplace_bank_bit('kind', 'claim')
                       ]
                       LIMIT 1
                   ),
                   now(),
                   0::bigint,
                   0::bigint
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new HighwayPopulationRow(
                r.GetBoolean(0), r.GetBoolean(1),
                r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
                r.GetInt64(3), r.GetInt64(4)),
            ct: ct, label: "highway_population", onError: onError);
    }
}
