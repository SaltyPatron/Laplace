using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

/// <summary>
/// Typed read-only queries against canonical PostgreSQL substrate tables and <c>public.laplace_*</c> functions.
/// </summary>
public static partial class NpgsqlSubstrateReads
{
    internal static int RequestedLimit(int limit) => Math.Max(0, limit);

    public readonly record struct SalientFactRow(string Type, string Fact, decimal EffMu, long Witnesses);

    private const string SalientFactsSql = """
        WITH claims AS MATERIALIZED (
            SELECT c.entity AS claim, public.laplace_parts(c.path) AS parts,
                   public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS eff_mu,
                   c.matches AS witnesses
            FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], greatest(16, $2 * 2)::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint) c
        ),
        parsed AS (
            SELECT CASE WHEN cardinality(parts) >= 3 THEN parts[2] ELSE public.laplace_text_id('member') END AS rel_id,
                   parts[cardinality(parts)] AS obj_id, eff_mu, witnesses
            FROM claims
            WHERE cardinality(parts) >= 2
        )
        SELECT coalesce(public.laplace_label_text(public.blake3_send(rel_id), false), encode(public.blake3_send(rel_id), 'hex')),
               coalesce(public.laplace_label_text(public.blake3_send(obj_id), false), encode(public.blake3_send(obj_id), 'hex')),
               eff_mu,
               witnesses
        FROM parsed
        ORDER BY eff_mu DESC, witnesses DESC
        LIMIT greatest(0, $2)
        """;

    public static Task<IReadOnlyList<SalientFactRow>> SalientFactsAsync(
        NpgsqlDataSource dataSource, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SalientFactsSql,
            static r => new SalientFactRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(limit);
            }, ct: ct, label: "salient_facts", onError: onError);

    public static Task<IReadOnlyList<SalientFactRow>> SalientFactsAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SalientFactsSql,
            static r => new SalientFactRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(limit);
            }, ct: ct, label: "salient_facts", onError: onError);

    public static async Task<(long Confirmed, long Contested, long Refuted, long Thin)> EntityRecordAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH claims AS (
                SELECT rating, deviation, matches
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], 256::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 0::smallint)
            )
            SELECT count(*) FILTER (WHERE matches >= 2 AND rating > 1510 AND public.laplace_confidence(rating, deviation, 2::double precision) >= 0.52)::bigint,
                   count(*) FILTER (WHERE matches >= 2 AND rating BETWEEN 1480 AND 1510)::bigint,
                   count(*) FILTER (WHERE matches >= 2 AND rating < 1480)::bigint,
                   count(*) FILTER (WHERE matches < 2)::bigint
            FROM claims
            """;
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id }),
            ct: ct, label: "entity_record", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? (0, 0, 0, 0) : rows[0];
    }

    public readonly record struct TapeRow(string Holder, string Type, string Fact, decimal? Mu);

    public static Task<IReadOnlyList<TapeRow>> ContrastAsync(
        NpgsqlDataSource dataSource, byte[] x, byte[] y, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH both_claims AS (
                SELECT 0 AS side,
                       coalesce(public.laplace_label_text($1, false), encode($1, 'hex')) AS holder,
                       c.path, public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS mu
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], greatest(1, $3)::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint) c
                UNION ALL
                SELECT 1 AS side,
                       coalesce(public.laplace_label_text($2, false), encode($2, 'hex')) AS holder,
                       c.path, public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS mu
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($2)], greatest(1, $3)::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint) c
            ),
            parsed AS (
                SELECT holder, public.laplace_parts(path) AS parts, mu
                FROM both_claims
            )
            SELECT holder,
                   CASE
                       WHEN cardinality(parts) >= 3 THEN coalesce(public.laplace_label_text(public.blake3_send(parts[2]), false), encode(public.blake3_send(parts[2]), 'hex'))
                       ELSE 'member'
                   END,
                   coalesce(public.laplace_label_text(public.blake3_send(parts[cardinality(parts)]), false), encode(public.blake3_send(parts[cardinality(parts)]), 'hex')),
                   mu
            FROM parsed
            WHERE cardinality(parts) >= 2
            ORDER BY mu DESC NULLS LAST
            LIMIT greatest(0, $3)
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new TapeRow(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetDecimal(3)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = x });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = y });
                p.AddWithValue(limit);
            }, ct: ct, label: "contrast", onError: onError);
    }

    public readonly record struct RelationSummaryRow(
        string? Relation, string? Plane, decimal? Mu, long? Usage, double? Geodesic, string? Verdict);

    public static async Task<RelationSummaryRow?> RelationSummaryAsync(
        NpgsqlDataSource dataSource, byte[] x, byte[] y, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH coords AS (
                SELECT ordinal, coord
                FROM public.laplace_entities(ARRAY[public.blake3_from_bytea($1), public.blake3_from_bytea($2)])
            ),
            dist AS (
                SELECT public.laplace_point4d_distance(
                    (SELECT coord FROM coords WHERE ordinal = 1),
                    (SELECT coord FROM coords WHERE ordinal = 2)) AS geo
            ),
            direct AS (
                SELECT c.path, c.rating, c.deviation, c.matches
                FROM public.laplace_claims_each(
                    ARRAY[public.blake3_from_bytea($1)], 64::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[],
                    1::smallint, NULL, 0::bigint, ARRAY[public.blake3_from_bytea($2)]) c
                ORDER BY c.matches DESC
                LIMIT 1
            ),
            parts AS (
                SELECT public.laplace_parts(path) AS p,
                       public.laplace_confidence(rating, deviation, 2)::numeric AS mu,
                       matches
                FROM direct
            )
            SELECT CASE WHEN cardinality(p.p) >= 2 THEN public.laplace_label_text(public.blake3_send(p.p[2]), false) END,
                   CASE WHEN p.p IS NOT NULL THEN 'relation' ELSE 'geometric' END,
                   p.mu,
                   p.matches,
                   d.geo,
                   CASE WHEN p.mu IS NULL THEN 'unattested'
                        WHEN p.mu >= 0.5 THEN 'supported'
                        ELSE 'contested' END
            FROM dist d
            LEFT JOIN parts p ON true
            """;
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new RelationSummaryRow(
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetDecimal(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetString(5)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = x });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = y });
            }, ct: ct, label: "relation_summary", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct ConsensusOutLabeledRow(
        string TypeLabel, string ObjectLabel, string ObjectIdHex, decimal EffMu, long Witnesses);

    public static Task<IReadOnlyList<ConsensusOutLabeledRow>> ConsensusOutLabeledAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH claims AS (
                SELECT public.laplace_parts(c.path) AS parts,
                       public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS eff_mu,
                       c.matches
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], greatest(1, $2)::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 1::smallint) c
            )
            SELECT CASE
                       WHEN cardinality(parts) >= 3 THEN coalesce(public.laplace_label_text(public.blake3_send(parts[2]), false), encode(public.blake3_send(parts[2]), 'hex'))
                       ELSE 'member'
                   END,
                   coalesce(public.laplace_label_text(public.blake3_send(parts[cardinality(parts)]), false), encode(public.blake3_send(parts[cardinality(parts)]), 'hex')),
                   encode(public.blake3_send(parts[cardinality(parts)]), 'hex'),
                   eff_mu,
                   matches
            FROM claims
            WHERE cardinality(parts) >= 2
            ORDER BY eff_mu DESC NULLS LAST, matches DESC
            LIMIT greatest(0, $2)
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new ConsensusOutLabeledRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(limit);
            }, ct: ct, label: "consensus_out_labeled", onError: onError);
    }

    public readonly record struct ConsensusInLabeledRow(
        string SubjectIdHex, string TypeLabel, string SubjectLabel, decimal EffMu, long Witnesses);

    public static Task<IReadOnlyList<ConsensusInLabeledRow>> ConsensusInLabeledAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH claims AS (
                SELECT public.laplace_parts(c.path) AS parts,
                       public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS eff_mu,
                       c.matches
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], greatest(1, $2)::bigint, ARRAY[public.laplace_bank_bit('kind','claim')], '{}'::public.blake3[], 2::smallint) c
            )
            SELECT encode(public.blake3_send(parts[1]), 'hex'),
                   CASE
                       WHEN cardinality(parts) >= 3 THEN coalesce(public.laplace_label_text(public.blake3_send(parts[2]), false), encode(public.blake3_send(parts[2]), 'hex'))
                       ELSE 'member'
                   END,
                   coalesce(public.laplace_label_text(public.blake3_send(parts[1]), false), encode(public.blake3_send(parts[1]), 'hex')),
                   eff_mu,
                   matches
            FROM claims
            WHERE cardinality(parts) >= 2
            ORDER BY eff_mu DESC NULLS LAST, matches DESC
            LIMIT greatest(0, $2)
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new ConsensusInLabeledRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(limit);
            }, ct: ct, label: "consensus_in_labeled", onError: onError);
    }

    public readonly record struct TopRelationEdgeRow(
        string SubjectIdHex, string Subject, string TypeIdHex, string Type,
        string ObjectIdHex, string Object, decimal EffMu, long Witnesses);

    public static Task<IReadOnlyList<TopRelationEdgeRow>> TopRelationsAsync(
        NpgsqlConnection conn, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH tops AS MATERIALIZED (
                SELECT c.claim,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches AS witnesses
                FROM public.consensus c
                WHERE c.matches > 0
                ORDER BY public.laplace_confidence(c.rating, c.deviation, 2::double precision) DESC, c.matches DESC, c.claim
                LIMIT greatest(0, $1) * 8
            ),
            parts AS MATERIALIZED (
                SELECT t.claim, t.eff_mu, t.witnesses,
                       public.laplace_parts(p.path) AS pts
                FROM tops t
                JOIN public.physicality p ON p.entity = t.claim
            ),
            triples AS (
                SELECT pts[1] AS s_id, pts[2] AS r_id, pts[cardinality(pts)] AS o_id, eff_mu, witnesses
                FROM parts
                WHERE cardinality(pts) = 3
                LIMIT greatest(0, $1)
            )
            SELECT encode(public.blake3_send(s_id), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(s_id), false), encode(public.blake3_send(s_id), 'hex')),
                   encode(public.blake3_send(r_id), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(r_id), false), encode(public.blake3_send(r_id), 'hex')),
                   encode(public.blake3_send(o_id), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(o_id), false), encode(public.blake3_send(o_id), 'hex')),
                   eff_mu,
                   witnesses
            FROM triples
            ORDER BY eff_mu DESC NULLS LAST, witnesses DESC
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new TopRelationEdgeRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetDecimal(6), r.GetInt64(7)),
            p => p.AddWithValue(limit),
            ct: ct, label: "top_relations", onError: onError);
    }
}
