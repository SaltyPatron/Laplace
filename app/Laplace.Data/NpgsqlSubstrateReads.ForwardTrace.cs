using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct ForwardTraceRow(
        int Step,
        string EntityIdHex,
        string EntityLabel,
        int StrideUsed,
        string RootIdHex,
        int CandidateCount,
        int OrderedContextCount,
        int ProposalChannelCount,
        int ExactChannelCount,
        long SequenceOccurrences,
        int CoveredOccurrences,
        int RelationFamilies,
        int OpposedOccurrences,
        string? SupportAnchorIdHex,
        string? SupportAnchorLabel,
        string? SupportRelationIdHex,
        string? SupportRelationLabel,
        bool? SupportOutbound,
        long? SupportRating,
        long? SupportRd,
        long? SupportWitnesses,
        int SupportSources,
        int SupportContexts,
        bool DeclaredResult,
        string Event,
        int RoutingRound);

    public static Task<IReadOnlyList<ForwardTraceRow>> ForwardTraceStepsAsync(
        NpgsqlDataSource dataSource,
        string prompt,
        int steps,
        int maxStride,
        double spread,
        int topK,
        int hops,
        int fanout,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH root_info AS MATERIALIZED (
                SELECT coalesce(public.laplace_resolve_ref($1), public.blake3_send(public.laplace_text_id($1))) AS root_raw,
                       coalesce(public.blake3_from_bytea(public.laplace_resolve_ref($1)), public.laplace_text_id($1)) AS root_id
            ),
            root_ent AS MATERIALIZED (
                SELECT e.id, e.tier
                FROM root_info ri
                JOIN public.laplace_entities(ARRAY[ri.root_id]) e ON e.id = ri.root_id
            ),
            token_parts AS MATERIALIZED (
                SELECT s.i::int AS ord,
                       s.id
                FROM public.laplace_segments($1) s
                WHERE length(trim(s.segment)) > 0
                  AND s.id IS NOT NULL
            ),
            witnessed_tokens AS MATERIALIZED (
                SELECT tp.ord, e.id
                FROM token_parts tp
                JOIN public.laplace_entities(ARRAY(SELECT id FROM token_parts)) e
                  ON e.id = tp.id AND e.tier IS NOT NULL
                ORDER BY tp.ord
            ),
            root_phys AS MATERIALIZED (
                SELECT public.laplace_parts(ph.path) AS pts
                FROM root_ent re,
                     public.laplace_physicalities(ARRAY[re.id]) ph
                WHERE re.tier > 1
                   OR NOT EXISTS (SELECT 1 FROM witnessed_tokens)
                LIMIT 1
            ),
            parts AS MATERIALIZED (
                SELECT ord, id
                FROM (
                    SELECT 1 AS src, u.ord::int AS ord, u.id
                    FROM root_phys rp, unnest(rp.pts) WITH ORDINALITY AS u(id, ord)
                    WHERE cardinality(rp.pts) > 0 AND u.id IS NOT NULL
                    UNION ALL
                    SELECT 2 AS src, wt.ord, wt.id
                    FROM witnessed_tokens wt
                    WHERE NOT EXISTS (SELECT 1 FROM root_phys rp WHERE cardinality(rp.pts) > 0)
                    UNION ALL
                    SELECT 3 AS src, 1 AS ord, ri.root_id AS id
                    FROM root_info ri
                    WHERE ri.root_id IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM root_phys rp WHERE cardinality(rp.pts) > 0)
                      AND NOT EXISTS (SELECT 1 FROM witnessed_tokens)
                ) all_p
            ),
            part_count AS MATERIALIZED (
                SELECT count(*)::int AS cnt FROM parts
            ),
            stride_parts AS MATERIALIZED (
                SELECT id
                FROM parts, part_count pc
                WHERE $3 <= 0 OR ord > greatest(0, pc.cnt - $3)
                ORDER BY ord
            ),
            coupled_raw AS MATERIALIZED (
                SELECT c.entity, c.route, c.rating, c.deviation, c.via, c.rel,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision) AS conf
                FROM public.laplace_couple(
                    ARRAY(SELECT id FROM parts ORDER BY ord),
                    greatest(4096::bigint, $7::bigint),
                    ARRAY(SELECT public.laplace_text_id(r)
                          FROM unnest(ARRAY[
                              'pronunciation',
                              'pronunciation of a form',
                              'variety of language',
                              'other form',
                              'identifies',
                              'syntactic behaviour',
                              'type',
                              'identifier vocabulary',
                              'CLDR short name of sequence'
                          ]) r),
                    0::smallint, 0.0,
                    greatest(8, $5 * 4)) c
                WHERE c.entity IS NOT NULL
            ),
            pulled_raw AS MATERIALIZED (
                SELECT p.id, p.hops, p.via, p.claim, p.relation, p.rating, p.deviation, p.cost,
                       public.laplace_confidence(p.rating, p.deviation, 2::double precision) AS conf
                FROM public.laplace_pull(
                    ARRAY(SELECT id FROM parts ORDER BY ord),
                    NULL,
                    greatest(1, $6),
                    greatest(1, $7)::bigint,
                    greatest(64, $5 * 16)::bigint) p
                WHERE p.id IS NOT NULL
            ),
            suffix_raw AS MATERIALIZED (
                SELECT f.i, f.j, f.paths, f.runs, f.next, f.times
                FROM public.laplace_forward_suffix(
                    ARRAY(SELECT id FROM stride_parts),
                    greatest(4096::bigint, $7::bigint)) f
                WHERE f.next IS NOT NULL
            ),
            all_claims AS MATERIALIZED (
                SELECT DISTINCT cid
                FROM (
                    SELECT via AS cid FROM coupled_raw WHERE via IS NOT NULL
                    UNION ALL
                    SELECT claim AS cid FROM pulled_raw WHERE claim IS NOT NULL
                ) q
            ),
            st AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT cid FROM all_claims)) s
            ),
            witness_cnts AS MATERIALIZED (
                SELECT t.claim, count(DISTINCT t.source)::int AS sources, count(DISTINCT t.context)::int AS contexts
                FROM public.laplace_testimony(ARRAY(SELECT cid FROM all_claims)) t
                GROUP BY t.claim
            ),
            via_phys AS MATERIALIZED (
                SELECT ph.entity, public.laplace_parts(ph.path) AS pts
                FROM public.laplace_physicalities(ARRAY(SELECT cid FROM all_claims)) ph
            ),
            route_rows AS (
                SELECT row_number() OVER (ORDER BY pr.hops ASC, pr.cost ASC)::int AS step,
                       encode(public.blake3_send(pr.id), 'hex') AS entity_id_hex,
                       coalesce(public.laplace_label_text(public.blake3_send(pr.id), false), encode(public.blake3_send(pr.id), 'hex')) AS entity_label,
                       coalesce((SELECT count(*)::int FROM stride_parts), 1) AS stride_used,
                       coalesce(encode((SELECT root_raw FROM root_info), 'hex'), repeat('0', 32)) AS root_id_hex,
                       (SELECT count(DISTINCT entity)::int FROM coupled_raw) + (SELECT count(DISTINCT next)::int FROM suffix_raw) AS candidate_count,
                       (SELECT cnt FROM part_count) AS ordered_context_count,
                       0 AS proposal_channel_count,
                       CASE WHEN pr.claim IS NOT NULL THEN 1 ELSE 0 END AS exact_channel_count,
                       coalesce((SELECT sum(times)::bigint FROM suffix_raw), 0::bigint) AS sequence_occurrences,
                       coalesce((SELECT sum(paths)::int FROM suffix_raw), 0) AS covered_occurrences,
                       CASE WHEN pr.relation IS NOT NULL THEN 1 ELSE 0 END AS relation_families,
                       CASE WHEN pr.rating < 1450 THEN 1 ELSE 0 END AS opposed_occurrences,
                       CASE WHEN pr.via IS NULL THEN NULL ELSE encode(public.blake3_send(pr.via), 'hex') END AS support_anchor_id_hex,
                       CASE WHEN pr.via IS NULL THEN NULL ELSE public.laplace_label_text(public.blake3_send(pr.via), false) END AS support_anchor_label,
                       CASE WHEN pr.relation IS NULL THEN NULL ELSE encode(public.blake3_send(pr.relation), 'hex') END AS support_relation_id_hex,
                       CASE WHEN pr.relation IS NULL THEN NULL ELSE public.laplace_label_text(public.blake3_send(pr.relation), false) END AS support_relation_label,
                       coalesce((SELECT vp.pts[1] = ANY(ARRAY(SELECT id FROM parts)) FROM via_phys vp WHERE vp.entity = pr.claim AND cardinality(vp.pts) >= 2 LIMIT 1), true) AS support_outbound,
                       pr.rating::bigint AS support_rating,
                       pr.deviation::bigint AS support_rd,
                       st.matches AS support_witnesses,
                       coalesce(w.sources, 0) AS support_sources,
                       coalesce(w.contexts, 0) AS support_contexts,
                       (pr.claim IS NOT NULL) AS declared_result,
                       'route'::text AS event,
                       pr.hops AS routing_round
                FROM pulled_raw pr
                LEFT JOIN st ON st.claim = pr.claim
                LEFT JOIN witness_cnts w ON w.claim = pr.claim
                ORDER BY pr.hops ASC, pr.cost ASC
                LIMIT greatest(1, $5)
            ),
            emit_coupled AS (
                SELECT entity,
                       count(DISTINCT route)::int AS proposal_channels,
                       count(*) FILTER (WHERE route = 0)::int AS exact_channels,
                       count(DISTINCT rel) FILTER (WHERE rel IS NOT NULL)::int AS rel_families,
                       count(*) FILTER (WHERE rating < 1450)::int AS opposed_cnt,
                       max(conf) AS best_conf,
                       (ARRAY_AGG(via ORDER BY conf DESC NULLS LAST))[1] AS top_via,
                       (ARRAY_AGG(rel ORDER BY conf DESC NULLS LAST))[1] AS top_rel,
                       (ARRAY_AGG(rating ORDER BY conf DESC NULLS LAST))[1] AS top_rating,
                       (ARRAY_AGG(deviation ORDER BY conf DESC NULLS LAST))[1] AS top_rd
                FROM coupled_raw
                GROUP BY entity
            ),
            emit_suffix AS (
                SELECT next AS entity,
                       max(j - i + 1)::int AS stride_len,
                       sum(times)::bigint AS seq_occ,
                       sum(paths)::int AS cov_occ
                FROM suffix_raw
                GROUP BY next
            ),
            emit_pool AS (
                SELECT coalesce(ec.entity, es.entity) AS entity,
                       coalesce(es.stride_len, (SELECT count(*)::int FROM stride_parts), 1) AS stride_used,
                       coalesce(ec.proposal_channels, 0) + CASE WHEN es.entity IS NOT NULL THEN 1 ELSE 0 END AS proposal_channel_count,
                       coalesce(ec.exact_channels, 0) AS exact_channel_count,
                       coalesce(es.seq_occ, 0::bigint) AS sequence_occurrences,
                       coalesce(es.cov_occ, 0) AS covered_occurrences,
                       coalesce(ec.rel_families, 0) AS relation_families,
                       coalesce(ec.opposed_cnt, 0) AS opposed_occurrences,
                       ec.top_via,
                       ec.top_rel,
                       ec.top_rating,
                       ec.top_rd,
                       coalesce(ec.best_conf, 0.0) AS best_conf
                FROM emit_coupled ec
                FULL OUTER JOIN emit_suffix es ON es.entity = ec.entity
            ),
            emit_mx AS MATERIALIZED (
                SELECT max(best_conf) AS top_conf FROM emit_pool
            ),
            emit_filtered AS (
                SELECT ep.*
                FROM emit_pool ep, emit_mx mx
                WHERE $4 <= 0.0 OR mx.top_conf IS NULL OR ep.best_conf >= mx.top_conf * (1.0 - least(1.0, $4))
                ORDER BY ep.exact_channel_count DESC,
                         ep.proposal_channel_count DESC,
                         ep.best_conf DESC,
                         ep.covered_occurrences DESC,
                         ep.sequence_occurrences DESC
                LIMIT greatest(1, least($2, $5))
            ),
            emit_rows AS (
                SELECT row_number() OVER (
                           ORDER BY ef.exact_channel_count DESC,
                                    ef.proposal_channel_count DESC,
                                    ef.best_conf DESC,
                                    ef.covered_occurrences DESC,
                                    ef.sequence_occurrences DESC
                       )::int AS step,
                       encode(public.blake3_send(ef.entity), 'hex') AS entity_id_hex,
                       coalesce(public.laplace_label_text(public.blake3_send(ef.entity), false), encode(public.blake3_send(ef.entity), 'hex')) AS entity_label,
                       ef.stride_used,
                       coalesce(encode((SELECT root_raw FROM root_info), 'hex'), repeat('0', 32)) AS root_id_hex,
                       (SELECT count(*)::int FROM emit_pool) AS candidate_count,
                       (SELECT cnt FROM part_count) AS ordered_context_count,
                       ef.proposal_channel_count,
                       ef.exact_channel_count,
                       ef.sequence_occurrences,
                       ef.covered_occurrences,
                       ef.relation_families,
                       ef.opposed_occurrences,
                       encode(public.blake3_send(ef.entity), 'hex') AS support_anchor_id_hex,
                       public.laplace_label_text(public.blake3_send(ef.entity), false) AS support_anchor_label,
                       CASE WHEN ef.top_rel IS NULL THEN NULL ELSE encode(public.blake3_send(ef.top_rel), 'hex') END AS support_relation_id_hex,
                       CASE WHEN ef.top_rel IS NULL THEN NULL ELSE public.laplace_label_text(public.blake3_send(ef.top_rel), false) END AS support_relation_label,
                       coalesce((SELECT vp.pts[1] = ANY(ARRAY(SELECT id FROM parts)) FROM via_phys vp WHERE vp.entity = ef.top_via AND cardinality(vp.pts) >= 2 LIMIT 1), true) AS support_outbound,
                       ef.top_rating::bigint AS support_rating,
                       ef.top_rd::bigint AS support_rd,
                       st.matches AS support_witnesses,
                       coalesce(w.sources, 0) AS support_sources,
                       coalesce(w.contexts, 0) AS support_contexts,
                       (ef.top_via IS NOT NULL) AS declared_result,
                       'emit'::text AS event,
                       0 AS routing_round
                FROM emit_filtered ef
                LEFT JOIN st ON st.claim = ef.top_via
                LEFT JOIN witness_cnts w ON w.claim = ef.top_via
            )
            SELECT * FROM route_rows
            UNION ALL
            SELECT * FROM emit_rows
            """;

        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new ForwardTraceRow(
                Step: r.GetInt32(0),
                EntityIdHex: r.GetString(1),
                EntityLabel: r.IsDBNull(2) ? "" : r.GetString(2),
                StrideUsed: r.GetInt32(3),
                RootIdHex: r.GetString(4),
                CandidateCount: r.GetInt32(5),
                OrderedContextCount: r.GetInt32(6),
                ProposalChannelCount: r.GetInt32(7),
                ExactChannelCount: r.GetInt32(8),
                SequenceOccurrences: r.GetInt64(9),
                CoveredOccurrences: r.GetInt32(10),
                RelationFamilies: r.GetInt32(11),
                OpposedOccurrences: r.GetInt32(12),
                SupportAnchorIdHex: r.IsDBNull(13) ? null : r.GetString(13),
                SupportAnchorLabel: r.IsDBNull(14) ? null : r.GetString(14),
                SupportRelationIdHex: r.IsDBNull(15) ? null : r.GetString(15),
                SupportRelationLabel: r.IsDBNull(16) ? null : r.GetString(16),
                SupportOutbound: r.IsDBNull(17) ? null : r.GetBoolean(17),
                SupportRating: r.IsDBNull(18) ? null : r.GetInt64(18),
                SupportRd: r.IsDBNull(19) ? null : r.GetInt64(19),
                SupportWitnesses: r.IsDBNull(20) ? null : r.GetInt64(20),
                SupportSources: r.GetInt32(21),
                SupportContexts: r.GetInt32(22),
                DeclaredResult: r.GetBoolean(23),
                Event: r.GetString(24),
                RoutingRound: r.GetInt32(25)),
            p =>
            {
                p.AddWithValue(NpgsqlDbType.Text, prompt);
                p.AddWithValue(steps);
                p.AddWithValue(maxStride);
                p.AddWithValue(spread);
                p.AddWithValue(topK);
                p.AddWithValue(hops);
                p.AddWithValue(fanout);
            },
            timeoutSeconds: 60,
            ct: ct,
            label: "forward_trace",
            onError: onError);
    }
}
