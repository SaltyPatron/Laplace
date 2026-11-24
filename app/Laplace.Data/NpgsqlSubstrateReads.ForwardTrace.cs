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
            WITH root_info AS (
                SELECT public.laplace_resolve_ref($1) AS root_raw,
                       public.laplace_text_id($1) AS text_id
            ),
            root_phys AS (
                SELECT ph.path
                FROM root_info ri
                LEFT JOIN LATERAL public.laplace_physicalities(ARRAY[coalesce(public.blake3_from_bytea(ri.root_raw), ri.text_id)]) ph
                    ON coalesce(public.blake3_from_bytea(ri.root_raw), ri.text_id) IS NOT NULL
            ),
            parts AS MATERIALIZED (
                SELECT p.id
                FROM root_info ri
                LEFT JOIN root_phys rp ON true,
                     unnest(coalesce(public.laplace_parts(rp.path), CASE WHEN coalesce(public.blake3_from_bytea(ri.root_raw), ri.text_id) IS NOT NULL THEN ARRAY[coalesce(public.blake3_from_bytea(ri.root_raw), ri.text_id)] ELSE ARRAY[]::public.blake3[] END)) AS p(id)
                WHERE p.id IS NOT NULL
            ),
            coupled AS MATERIALIZED (
                SELECT row_number() OVER (
                    ORDER BY public.laplace_confidence(c.rating, c.deviation, 2::double precision) DESC NULLS LAST, c.occ
                )::int AS step,
                c.entity, c.route, c.rating, c.deviation, c.via, c.rel
                FROM public.laplace_couple(
                    ARRAY(SELECT id FROM parts),
                    greatest(1, $7)::bigint,
                    '{}'::public.blake3[],
                    0::smallint, 0.0,
                    greatest(1, least($2, $5))) c
            ),
            witness_cnts AS (
                SELECT t.claim, count(DISTINCT t.source)::int AS sources, count(DISTINCT t.context)::int AS contexts
                FROM public.laplace_testimony(ARRAY(SELECT via FROM coupled WHERE via IS NOT NULL)) t
                GROUP BY t.claim
            ),
            st AS (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT via FROM coupled WHERE via IS NOT NULL)) s
            )
            SELECT c.step,
                   encode(public.blake3_send(c.entity), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(c.entity), false), encode(public.blake3_send(c.entity), 'hex')),
                   coalesce((SELECT count(*)::int FROM parts), 1),
                   coalesce(encode((SELECT root_raw FROM root_info), 'hex'), repeat('0', 32)),
                   (SELECT count(*)::int FROM coupled),
                   coalesce((SELECT count(*)::int FROM parts), 0),
                   1,
                   CASE WHEN c.route = 0 THEN 1 ELSE 0 END,
                   1::bigint,
                   1,
                   1,
                   0,
                   encode(public.blake3_send(c.entity), 'hex'),
                   public.laplace_label_text(public.blake3_send(c.entity), false),
                   CASE WHEN c.rel IS NULL THEN NULL ELSE encode(public.blake3_send(c.rel), 'hex') END,
                   CASE WHEN c.rel IS NULL THEN NULL ELSE public.laplace_label_text(public.blake3_send(c.rel), false) END,
                   true,
                   c.rating::bigint,
                   c.deviation::bigint,
                   st.matches,
                   coalesce(w.sources, 0),
                   coalesce(w.contexts, 0),
                   c.via IS NOT NULL,
                   CASE c.route WHEN 0 THEN 'relation' WHEN 1 THEN 'containment' ELSE 'shape' END,
                   0
            FROM coupled c
            LEFT JOIN st ON st.claim = c.via
            LEFT JOIN witness_cnts w ON w.claim = c.via
            ORDER BY c.step
            LIMIT greatest(0, $2)
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
