using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct ConverseReplyRow(string Reply, decimal? EffMu, long? Witnesses);

    private static ConverseReplyRow MapConverseReply(NpgsqlDataReader r) => new(
        r.IsDBNull(0) ? "" : r.GetString(0),
        r.IsDBNull(1) ? null : r.GetDecimal(1),
        r.IsDBNull(2) ? null : r.GetInt64(2));

    public static Task<IReadOnlyList<ConverseReplyRow>> RecallSessionAsync(
        NpgsqlConnection conn, string prompt, byte[]? session, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH anchor AS (
                SELECT public.laplace_resolve_ref($1) AS tid
            ),
            coupled AS MATERIALIZED (
                SELECT c.entity, c.via, c.rating, c.deviation
                FROM anchor a,
                     public.laplace_couple(ARRAY[public.blake3_from_bytea(a.tid)], 32::bigint, '{}'::public.blake3[], 0::smallint, 0.0, 24) c
                WHERE a.tid IS NOT NULL
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT via FROM coupled WHERE via IS NOT NULL)) s
            )
            SELECT coalesce(public.laplace_label_text(public.blake3_send(c.entity), false), encode(public.blake3_send(c.entity), 'hex')),
                   public.laplace_confidence(c.rating, c.deviation, 2)::numeric,
                   coalesce(s.matches, 1)::bigint
            FROM coupled c
            LEFT JOIN standings s ON s.claim = c.via
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql, MapConverseReply,
            p => p.AddWithValue(prompt),
            ct: ct, label: "recall_session", onError: onError);
    }

    public static async Task<IReadOnlyList<ConverseReplyRow>> RecallSessionAsync(
        NpgsqlDataSource dataSource, string prompt, byte[]? session, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await RecallSessionAsync(conn, prompt, session, ct, onError).ConfigureAwait(false);
    }

    public static Task<IReadOnlyList<ConverseReplyRow>> RecallIntentAsync(
        NpgsqlDataSource dataSource, string shape, byte[] topic, byte[]? topic2,
        string? relationType, string? lang, byte[][]? contextIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH mid AS (
                SELECT CASE WHEN $4 IS NULL OR $4 = '' THEN NULL ELSE ARRAY[public.laplace_text_id($4)] END AS rels
            ),
            pulled AS MATERIALIZED (
                SELECT p.id, p.rating, p.deviation, p.claim
                FROM mid,
                     public.laplace_pull(
                         ARRAY[public.blake3_from_bytea($2)],
                         CASE WHEN $3 IS NULL THEN NULL ELSE ARRAY[public.blake3_from_bytea($3)] END,
                         3, 32::bigint, 256::bigint, mid.rels) p
                WHERE p.claim IS NOT NULL
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claim FROM pulled WHERE claim IS NOT NULL)) s
            )
            SELECT coalesce(public.laplace_label_text(public.blake3_send(p.id), false), encode(public.blake3_send(p.id), 'hex')),
                   public.laplace_confidence(p.rating, p.deviation, 2)::numeric,
                   coalesce(s.matches, 1)::bigint
            FROM pulled p
            LEFT JOIN standings s ON s.claim = p.claim
            ORDER BY public.laplace_confidence(p.rating, p.deviation, 2::double precision) DESC NULLS LAST
            LIMIT 40
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.AddWithValue(shape);
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = (object?)topic2 ?? DBNull.Value });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)relationType ?? DBNull.Value });
            }, ct: ct, label: "recall_intent", onError: onError);
    }

    public readonly record struct CompletionRow(
        string ObjectIdHex, string TypeIdHex, decimal EffectiveMu, long Witnesses, string ObjectLabel);

    public static Task<IReadOnlyList<CompletionRow>> CompletionsAsync(
        NpgsqlConnection conn, string prompt, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH tid AS (
                SELECT public.laplace_text_id($1) AS id
            ),
            phys AS (
                SELECT ph.path
                FROM tid t
                LEFT JOIN LATERAL public.laplace_physicalities(ARRAY[t.id]) ph ON t.id IS NOT NULL
            ),
            parts AS (
                SELECT coalesce(
                    public.laplace_parts(p.path),
                    CASE WHEN t.id IS NOT NULL THEN ARRAY[t.id] ELSE ARRAY[]::public.blake3[] END
                ) AS ids
                FROM tid t
                LEFT JOIN phys p ON true
            )
            SELECT encode(public.blake3_send(f.next), 'hex'),
                   repeat('0', 32),
                   coalesce(f.times, 0)::numeric,
                   coalesce(f.paths, 0)::bigint,
                   coalesce(public.laplace_label_text(public.blake3_send(f.next), false), encode(public.blake3_send(f.next), 'hex'))
            FROM parts, public.laplace_forward(parts.ids, 32::bigint) f
            ORDER BY f.paths DESC NULLS LAST, f.times DESC NULLS LAST
            LIMIT greatest(0, $2)
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new CompletionRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3), r.GetString(4)),
            p =>
            {
                p.AddWithValue(prompt);
                p.AddWithValue(limit);
            }, ct: ct, label: "completions", onError: onError);
    }

    public readonly record struct WalkBranchStepRow(
        int Depth, string[] PathHex, string[] TypePathHex, string EntityIdHex, string EntityLabel,
        decimal EffMu, decimal PathMu, long Witnesses);

    public static Task<IReadOnlyList<WalkBranchStepRow>> ExplainTraceStepsAsync(
        NpgsqlConnection conn, string prompt, int depth, int beam, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH seed AS (
                SELECT public.blake3_from_bytea(public.laplace_resolve_ref($1)) AS id
            ),
            pulled AS MATERIALIZED (
                SELECT p.hops, p.chain, p.claims, p.id, p.rating, p.deviation, p.cost
                FROM seed s,
                     public.laplace_pull(ARRAY[s.id], NULL, greatest(1, $2), greatest(1, $3)::bigint, 512::bigint) p
                WHERE s.id IS NOT NULL
                  AND p.claims IS NOT NULL
                  AND cardinality(p.claims) > 0
                  AND p.claims[cardinality(p.claims)] IS NOT NULL
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claims[cardinality(claims)] FROM pulled)) s
            )
            SELECT p.hops,
                   ARRAY(SELECT encode(public.blake3_send(x), 'hex') FROM unnest(coalesce(p.chain, ARRAY[]::public.blake3[])) AS x),
                   ARRAY(SELECT encode(public.blake3_send(x), 'hex') FROM unnest(coalesce(p.claims, ARRAY[]::public.blake3[])) AS x),
                   encode(public.blake3_send(p.id), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(p.id), false), encode(public.blake3_send(p.id), 'hex')),
                   public.laplace_confidence(p.rating, p.deviation, 2)::numeric,
                   p.cost::numeric,
                   coalesce(st.matches, 1)::bigint
            FROM pulled p
            LEFT JOIN standings st ON st.claim = p.claims[cardinality(p.claims)]
            ORDER BY p.hops, p.cost
            LIMIT greatest(1, $3) * 8
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new WalkBranchStepRow(
                r.GetInt32(0), r.GetFieldValue<string[]>(1), r.GetFieldValue<string[]>(2),
                r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetInt64(7)),
            p =>
            {
                p.AddWithValue(prompt);
                p.AddWithValue(depth);
                p.AddWithValue(beam);
            }, ct: ct, label: "explain_trace_steps", onError: onError);
    }

    public readonly record struct WalkBranchRow(
        int Depth, string Path, decimal EffMu, decimal PathMu, long Witnesses);

    public static Task<IReadOnlyList<WalkBranchRow>> WalkBranchesAsync(
        NpgsqlDataSource dataSource, string? prompt, string? entityHex, string? relationType,
        int depth, int breadth, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH seed AS (
                SELECT CASE
                    WHEN $2 IS NOT NULL AND length($2) = 32 THEN public.blake3_from_bytea(decode($2, 'hex'))
                    WHEN $1 IS NOT NULL THEN public.blake3_from_bytea(public.laplace_resolve_ref($1))
                    ELSE NULL
                END AS id
            ),
            mid AS (
                SELECT CASE WHEN $3 IS NULL OR $3 = '' THEN NULL ELSE ARRAY[public.laplace_text_id($3)] END AS rels
            ),
            pulled AS MATERIALIZED (
                SELECT p.hops, p.chain, p.rating, p.deviation, p.cost, p.claim
                FROM seed s, mid m,
                     public.laplace_pull(ARRAY[s.id], NULL, greatest(1, $4), greatest(1, $5)::bigint, 512::bigint, m.rels) p
                WHERE s.id IS NOT NULL AND p.claim IS NOT NULL
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claim FROM pulled WHERE claim IS NOT NULL)) s
            )
            SELECT p.hops,
                   array_to_string(ARRAY(
                       SELECT coalesce(public.laplace_label_text(public.blake3_send(x), false), encode(public.blake3_send(x), 'hex'))
                       FROM unnest(coalesce(p.chain, ARRAY[]::public.blake3[])) AS x
                   ), ' -> '),
                   public.laplace_confidence(p.rating, p.deviation, 2)::numeric,
                   p.cost::numeric,
                   coalesce(st.matches, 1)::bigint
            FROM pulled p
            LEFT JOIN standings st ON st.claim = p.claim
            ORDER BY p.hops, p.cost
            LIMIT 100
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new WalkBranchRow(r.GetInt32(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)prompt ?? DBNull.Value });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)entityHex ?? DBNull.Value });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)relationType ?? DBNull.Value });
                p.AddWithValue(depth);
                p.AddWithValue(breadth);
            }, ct: ct, label: "walk_branches", onError: onError);
    }

    public readonly record struct QueryShapeRow(
        string Shape, string Summary, bool NeedsTopic2, bool NeedsType, bool AcceptsLang);

    public static Task<IReadOnlyList<QueryShapeRow>> QueryShapesAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        IReadOnlyList<QueryShapeRow> rows =
        [
            new("define", "Definitional assertions for a topic", false, false, false),
            new("what_is", "Taxonomic parent / IS_A assertions", false, false, false),
            new("related", "Rated relation assertions", false, true, false),
            new("is_a", "Test taxonomic relation between two topics", true, false, false),
            new("reason", "Shortest rated chain between two topics", true, false, false),
            new("walk", "Best-first rated beam walk from a topic", false, false, false),
            new("complete", "Forward trajectory suffix continuation", false, false, false),
        ];
        return Task.FromResult(rows);
    }

    public readonly record struct RelationBandRow(
        int Band, string Name, double Rank, long RelationTypes, long ConsensusRows);

    public static Task<IReadOnlyList<RelationBandRow>> RelationBandsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT 1, 'consensus', 1.0::float8, 233::bigint,
                   coalesce((SELECT reltuples::bigint FROM pg_catalog.pg_class WHERE oid = 'public.consensus'::regclass), 0)::bigint
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new RelationBandRow(r.GetInt32(0), r.GetString(1), r.GetDouble(2), r.GetInt64(3), r.GetInt64(4)),
            ct: ct, label: "relation_bands", onError: onError);
    }

    public static Task<IReadOnlyList<ConverseReplyRow>> BandFactsAsync(
        NpgsqlDataSource dataSource, byte[] topic, int[]? bands, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH coupled AS MATERIALIZED (
                SELECT c.entity, c.via, c.rating, c.deviation
                FROM public.laplace_couple(ARRAY[public.blake3_from_bytea($1)], greatest(1, $2)::bigint, '{}'::public.blake3[], 0::smallint, 0.0, greatest(1, $2)) c
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT via FROM coupled WHERE via IS NOT NULL)) s
            )
            SELECT coalesce(public.laplace_label_text(public.blake3_send(c.entity), false), encode(public.blake3_send(c.entity), 'hex')),
                   public.laplace_confidence(c.rating, c.deviation, 2)::numeric,
                   coalesce(s.matches, 1)::bigint
            FROM coupled c
            LEFT JOIN standings s ON s.claim = c.via
            ORDER BY public.laplace_confidence(c.rating, c.deviation, 2::double precision) DESC NULLS LAST
            LIMIT greatest(0, $2)
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.AddWithValue(limit);
            }, ct: ct, label: "band_facts", onError: onError);
    }

    public static Task<IReadOnlyList<ConverseReplyRow>> WalkStrongestAsync(
        NpgsqlDataSource dataSource, byte[] topic, int depth, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        WalkBranchesBeamAsync(dataSource, topic, null, null, depth, 8, 20, ct, onError);

    public static Task<IReadOnlyList<ConverseReplyRow>> WalkBranchesBeamAsync(
        NpgsqlDataSource dataSource, byte[] topic, string? relationType, int[]? bands,
        int depth, int breadth, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH mid AS (
                SELECT CASE WHEN $2 IS NULL OR $2 = '' THEN NULL ELSE ARRAY[public.laplace_text_id($2)] END AS rels
            ),
            pulled AS MATERIALIZED (
                SELECT p.chain, p.rating, p.deviation, p.claim
                FROM mid m,
                     public.laplace_pull(ARRAY[public.blake3_from_bytea($1)], NULL, greatest(1, $3), greatest(1, $4)::bigint, 512::bigint, m.rels) p
                WHERE p.claim IS NOT NULL
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claim FROM pulled WHERE claim IS NOT NULL)) s
            )
            SELECT array_to_string(ARRAY(
                       SELECT coalesce(public.laplace_label_text(public.blake3_send(x), false), encode(public.blake3_send(x), 'hex'))
                       FROM unnest(coalesce(p.chain, ARRAY[]::public.blake3[])) AS x
                   ), ' -> '),
                   public.laplace_confidence(p.rating, p.deviation, 2)::numeric,
                   coalesce(s.matches, 1)::bigint
            FROM pulled p
            LEFT JOIN standings s ON s.claim = p.claim
            ORDER BY public.laplace_confidence(p.rating, p.deviation, 2::double precision) DESC NULLS LAST
            LIMIT greatest(0, $5)
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)relationType ?? DBNull.Value });
                p.AddWithValue(depth);
                p.AddWithValue(breadth);
                p.AddWithValue(limit);
            }, ct: ct, label: "walk_branches_beam", onError: onError);
    }

    public static Task<IReadOnlyList<ConverseReplyRow>> AstarPathAsync(
        NpgsqlDataSource dataSource, byte[] topic, byte[] topic2, int depth,
        bool directed, bool useGeometry, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH pulled AS MATERIALIZED (
                SELECT p.chain, p.rating, p.deviation, p.claim, p.cost
                FROM public.laplace_pull(
                    ARRAY[public.blake3_from_bytea($1)],
                    ARRAY[public.blake3_from_bytea($2)],
                    greatest(1, $3), 64::bigint, 1024::bigint) p
                WHERE p.goal
            ),
            standings AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT claim FROM pulled WHERE claim IS NOT NULL)) s
            )
            SELECT array_to_string(ARRAY(
                       SELECT coalesce(public.laplace_label_text(public.blake3_send(x), false), encode(public.blake3_send(x), 'hex'))
                       FROM unnest(coalesce(p.chain, ARRAY[]::public.blake3[])) AS x
                   ), ' -> '),
                   public.laplace_confidence(p.rating, p.deviation, 2)::numeric,
                   coalesce(s.matches, 1)::bigint
            FROM pulled p
            LEFT JOIN standings s ON s.claim = p.claim
            ORDER BY p.cost ASC
            LIMIT 20
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic2 });
                p.AddWithValue(depth);
            }, ct: ct, label: "astar_path", onError: onError);
    }

    public static Task<IReadOnlyList<ConverseReplyRow>> WalkContinuationsAsync(
        NpgsqlDataSource dataSource, byte[] topic, int steps, int maxStride,
        double spread, int breadth, long? seed, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT coalesce(public.laplace_label_text(public.blake3_send(f.next), false), encode(public.blake3_send(f.next), 'hex')),
                   f.times::numeric,
                   f.paths
            FROM public.laplace_forward(ARRAY[public.blake3_from_bytea($1)], greatest(1, $2)::bigint) f
            ORDER BY f.paths DESC NULLS LAST, f.times DESC NULLS LAST
            LIMIT greatest(1, $2)
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql, MapConverseReply,
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = topic });
                p.AddWithValue(Math.Max(1, steps));
            }, ct: ct, label: "walk_continuations", onError: onError);
    }
}
