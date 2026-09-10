using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Read surface for the canonical query-relative forward program. This is not an
/// explanation replay: <c>generation.forward_trace</c> is the same native pass that
/// <c>generation.forward_prompt</c>, <c>generation.forward_text</c>, conversation and
/// streaming project for ordinary execution.
/// </summary>
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

    /// <summary>
    /// Execute the canonical native forward pass once and return its per-election
    /// receipts. No consensus.walk_branches/replay path is involved.
    /// </summary>
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
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT
                t.step,
                encode(t.entity, 'hex'),
                converse.label_or_hex(t.entity),
                t.stride_used,
                encode(t.root_id, 'hex'),
                t.candidate_count,
                t.ordered_context_count,
                t.proposal_channel_count,
                t.exact_channel_count,
                t.sequence_occurrences,
                t.covered_occurrences,
                t.relation_families,
                t.opposed_occurrences,
                CASE WHEN t.support_anchor IS NULL THEN NULL ELSE encode(t.support_anchor, 'hex') END,
                CASE WHEN t.support_anchor IS NULL THEN NULL ELSE converse.label_or_hex(t.support_anchor) END,
                CASE WHEN t.support_relation IS NULL THEN NULL ELSE encode(t.support_relation, 'hex') END,
                CASE WHEN t.support_relation IS NULL THEN NULL ELSE converse.label_or_hex(t.support_relation) END,
                t.support_outbound,
                t.support_rating,
                t.support_rd,
                t.support_witnesses,
                t.support_sources,
                t.support_contexts,
                t.declared_result,
                t.event,
                t.routing_round
            FROM generation.forward_trace(
                @prompt,
                @steps,
                @max_stride,
                @spread,
                @top_k,
                NULL::bigint,
                @hops,
                @fanout,
                NULL::bytea[],
                NULL::bytea[]) AS t
            ORDER BY t.step, t.routing_round,
                     CASE t.event WHEN 'route' THEN 0 ELSE 1 END
            """,
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
                p.AddWithValue("prompt", prompt);
                p.AddWithValue("steps", steps);
                p.AddWithValue("max_stride", maxStride);
                p.AddWithValue("spread", spread);
                p.AddWithValue("top_k", topK);
                p.AddWithValue("hops", hops);
                p.AddWithValue("fanout", fanout);
            },
            timeoutSeconds: 60,
            ct: ct,
            label: "forward_trace",
            onError: onError);
}
