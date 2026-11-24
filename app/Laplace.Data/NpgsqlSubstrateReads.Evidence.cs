using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

public static partial class NpgsqlSubstrateReads
{
    public static Task<long?> EvidenceCountAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH claims AS (
                SELECT entity AS claim
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], 256, NULL, '{}'::public.blake3[], 0::smallint)
            )
            SELECT count(*)::bigint
            FROM public.laplace_testimony(ARRAY(SELECT claim FROM claims))
            """;
        return NpgsqlRead.ExecuteScalarAsync<long?>(conn, sql,
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id }),
            ct: ct, label: "evidence_count", onError: onError);
    }

    public readonly record struct EvidenceSeriesRow(
        string ClaimId, string? TypeId, string ObjectId, string TypeLabel, string ObjectLabel,
        decimal EffMu, string SourceId, string SourceLabel, double Games, double Tokens,
        double Score, double Trust, string? DependenceRoot, int? Position);

    public static Task<IReadOnlyList<EvidenceSeriesRow>> EvidenceSeriesReceiptAsync(
        NpgsqlConnection conn, byte[] id, int limit, bool inbound, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH target AS (SELECT public.blake3_from_bytea($1) AS tid),
            claims AS MATERIALIZED (
                SELECT c.entity AS claim, public.laplace_parts(c.path) AS parts,
                       public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS eff_mu
                FROM public.laplace_claims_each(ARRAY[(SELECT tid FROM target)], greatest(1, $2)::bigint, NULL, '{}'::public.blake3[],
                    CASE WHEN $3 THEN 2::smallint ELSE 1::smallint END) c
                LIMIT greatest(0, $2)
            ),
            parsed AS MATERIALIZED (
                SELECT c.claim, c.eff_mu,
                       c.parts[2] AS rel_id,
                       CASE WHEN $3 THEN c.parts[1] ELSE c.parts[cardinality(c.parts)] END AS peer_id
                FROM claims c
                WHERE cardinality(c.parts) >= 2
            ),
            testi AS MATERIALIZED (
                SELECT t.claim, t.source, t.occurrences::double precision AS games, t.tokens::double precision AS tokens,
                       t.score, t.trust, t.dependence_root, t."position"
                FROM public.laplace_testimony(ARRAY(SELECT claim FROM parsed)) t
            ),
            ids_to_label AS (
                SELECT DISTINCT x AS id FROM (
                    SELECT rel_id AS x FROM parsed
                    UNION ALL SELECT peer_id FROM parsed
                    UNION ALL SELECT source FROM testi
                ) u WHERE x IS NOT NULL
            ),
            lbls AS MATERIALIZED (
                SELECT l.entity, l.content
                FROM public.laplace_labels(ARRAY(SELECT id FROM ids_to_label)) l
            ),
            wnames AS MATERIALIZED (
                SELECT w.witness, w.name
                FROM public.laplace_witnesses_named() w
            )
            SELECT encode(public.blake3_send(p.claim), 'hex'),
                   CASE WHEN p.rel_id IS NULL THEN NULL ELSE encode(public.blake3_send(p.rel_id), 'hex') END,
                   encode(public.blake3_send(p.peer_id), 'hex'),
                   coalesce((SELECT content FROM lbls WHERE entity = p.rel_id LIMIT 1), encode(public.blake3_send(p.rel_id), 'hex'), 'related'),
                   coalesce((SELECT content FROM lbls WHERE entity = p.peer_id LIMIT 1), encode(public.blake3_send(p.peer_id), 'hex')),
                   p.eff_mu,
                   encode(public.blake3_send(t.source), 'hex'),
                   coalesce((SELECT wn.name FROM wnames wn WHERE wn.witness = t.source LIMIT 1), (SELECT content FROM lbls WHERE entity = t.source LIMIT 1), encode(public.blake3_send(t.source), 'hex')),
                   t.games, t.tokens, t.score, t.trust,
                   CASE WHEN t.dependence_root IS NULL THEN NULL ELSE encode(public.blake3_send(t.dependence_root), 'hex') END,
                   t."position"
            FROM parsed p
            JOIN testi t ON t.claim = p.claim
            ORDER BY p.eff_mu DESC NULLS LAST, t.trust DESC NULLS LAST, t.claim
            LIMIT greatest(0, $2)
            """;

        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new EvidenceSeriesRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetString(6), r.GetString(7),
                r.GetDouble(8), r.GetDouble(9), r.GetDouble(10), r.GetDouble(11),
                r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetInt32(13)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(limit);
                p.AddWithValue(inbound);
            }, ct: ct, label: "evidence_series_receipt", onError: onError);
    }

    public readonly record struct OrdinalCountRow(long Ordinal, long? Count);

    public static Task<IReadOnlyList<OrdinalCountRow>> EvidenceCountsBatchAsync(
        NpgsqlConnection conn, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH inputs AS (
                SELECT ord, public.blake3_from_bytea(raw_id) AS id
                FROM unnest($1::bytea[]) WITH ORDINALITY AS u(raw_id, ord)
            ),
            claims AS MATERIALIZED (
                SELECT c.i, c.entity AS claim
                FROM public.laplace_claims_each(ARRAY(SELECT id FROM inputs), 128, NULL, '{}'::public.blake3[], 0::smallint) c
            ),
            cnt AS (
                SELECT cl.i, count(t.claim)::bigint AS cnt
                FROM claims cl
                JOIN public.laplace_testimony(ARRAY(SELECT DISTINCT claim FROM claims)) t ON t.claim = cl.claim
                GROUP BY cl.i
            )
            SELECT inp.ord::bigint, coalesce(cnt.cnt, 0)::bigint
            FROM inputs inp
            LEFT JOIN cnt ON cnt.i = inp.ord - 1
            ORDER BY inp.ord
            """;

        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new OrdinalCountRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = ids }),
            ct: ct, label: "evidence_counts_batch", onError: onError);
    }

    public readonly record struct OrdinalAttestationRow(
        long Ordinal, string TypeIdHex, string ObjectIdHex, string SourceIdHex, string? ContextIdHex,
        short Outcome, long ObservationCount);

    public static Task<IReadOnlyList<OrdinalAttestationRow>> AttestationsOutBatchAsync(
        NpgsqlConnection conn, byte[][] ids, int perId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH claims AS MATERIALIZED (
                SELECT c.i, c.entity AS claim, public.laplace_parts(c.path) AS parts
                FROM public.laplace_claims_each(
                    ARRAY(SELECT public.blake3_from_bytea(raw_id) FROM unnest($1::bytea[]) AS raw_id),
                    greatest(1, $2)::bigint, NULL, '{}'::public.blake3[], 1::smallint) c
            ),
            testi AS MATERIALIZED (
                SELECT claim, source, context, outcome, occurrences
                FROM public.laplace_testimony(ARRAY(SELECT DISTINCT claim FROM claims))
            )
            SELECT (c.i + 1)::bigint,
                   encode(public.blake3_send(c.parts[2]), 'hex'),
                   encode(public.blake3_send(c.parts[cardinality(c.parts)]), 'hex'),
                   encode(public.blake3_send(t.source), 'hex'),
                   CASE WHEN t.context IS NULL THEN NULL ELSE encode(public.blake3_send(t.context), 'hex') END,
                   t.outcome,
                   t.occurrences
            FROM claims c
            JOIN testi t ON t.claim = c.claim
            WHERE cardinality(c.parts) >= 2
            ORDER BY c.i, t.occurrences DESC NULLS LAST
            """;

        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new OrdinalAttestationRow(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt16(5), r.GetInt64(6)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = ids });
                p.AddWithValue(perId);
            }, ct: ct, label: "attestations_out_batch", onError: onError);
    }

    public readonly record struct TargetEvidenceRow(
        byte[]? EntityId, string? EntityLabel, string? TypeIdHex, string? TypeLabel,
        string? ObjectIdHex, string? ObjectLabel, string? SourceLabels, long? WitnessCount, decimal? EffMu);

    public static Task<IReadOnlyList<TargetEvidenceRow>> EvidenceForTargetAsync(
        NpgsqlConnection conn, string target, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH anchor AS (
                SELECT coalesce(public.laplace_resolve_ref($2), $1) AS id_raw
            ),
            anchor_check AS (
                SELECT a.id_raw,
                       e.id AS entity_id,
                       coalesce(public.laplace_label_text(a.id_raw, false), encode(a.id_raw, 'hex')) AS entity_label
                FROM anchor a
                LEFT JOIN public.laplace_entities(ARRAY[public.blake3_from_bytea(a.id_raw)]) e ON true
            ),
            claims AS MATERIALIZED (
                SELECT c.entity AS claim, public.laplace_parts(c.path) AS parts,
                       public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS eff_mu,
                       c.matches
                FROM public.laplace_claims_each(
                    ARRAY(SELECT public.blake3_from_bytea(id_raw) FROM anchor_check WHERE entity_id IS NOT NULL),
                    greatest(1, $3)::bigint, NULL, '{}'::public.blake3[], 1::smallint) c
                LIMIT greatest(0, $3)
            ),
            wnames AS MATERIALIZED (
                SELECT w.witness, w.name
                FROM public.laplace_witnesses_named() w
            ),
            testi AS MATERIALIZED (
                SELECT t.claim,
                       string_agg(DISTINCT coalesce((SELECT wn.name FROM wnames wn WHERE wn.witness = t.source LIMIT 1), public.laplace_label_text(public.blake3_send(t.source), true), encode(public.blake3_send(t.source), 'hex')), ', ' ORDER BY coalesce((SELECT wn.name FROM wnames wn WHERE wn.witness = t.source LIMIT 1), public.laplace_label_text(public.blake3_send(t.source), true), encode(public.blake3_send(t.source), 'hex'))) AS src_labels,
                       count(DISTINCT t.source)::bigint AS witnesses
                FROM public.laplace_testimony(ARRAY(SELECT claim FROM claims)) t
                GROUP BY t.claim
            ),
            facts AS (
                SELECT cl.parts[2] AS rel_id,
                       cl.parts[cardinality(cl.parts)] AS obj_id,
                       t.src_labels,
                       coalesce(t.witnesses, cl.matches)::bigint AS witnesses,
                       cl.eff_mu
                FROM claims cl
                LEFT JOIN testi t ON t.claim = cl.claim
                WHERE cardinality(cl.parts) >= 2
            )
            SELECT ac.id_raw,
                   ac.entity_label,
                   encode(public.blake3_send(f.rel_id), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(f.rel_id), false), encode(public.blake3_send(f.rel_id), 'hex')),
                   encode(public.blake3_send(f.obj_id), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(f.obj_id), false), encode(public.blake3_send(f.obj_id), 'hex')),
                   f.src_labels,
                   f.witnesses,
                   f.eff_mu
            FROM anchor_check ac
            LEFT JOIN facts f ON true
            WHERE ac.entity_id IS NOT NULL
            ORDER BY coalesce(f.eff_mu, -1e9) DESC NULLS LAST
            LIMIT greatest(1, $3)
            """;

        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new TargetEvidenceRow(
                r.IsDBNull(0) ? null : (byte[])r[0],
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetDecimal(8)),
            p =>
            {
                BindCanonicalReference(p, target);
                p.AddWithValue(limit);
            }, ct: ct, label: "evidence_for_target", onError: onError);
    }
}
