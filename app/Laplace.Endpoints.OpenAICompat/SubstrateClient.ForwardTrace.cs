using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{
    public async Task<IReadOnlyList<ForwardTraceStep>> ForwardTraceAsync(
        string prompt,
        int steps,
        int maxStride,
        double spread,
        int topK,
        int hops,
        int fanout,
        CancellationToken ct)
    {
        try
        {
            var rows = await NpgsqlSubstrateReads.ForwardTraceStepsAsync(
                _dataSource,
                prompt,
                steps,
                maxStride,
                spread,
                topK,
                hops,
                fanout,
                ct);

            return [.. rows.Select(static row => new ForwardTraceStep(
                Step: row.Step,
                EntityIdHex: row.EntityIdHex,
                Entity: row.EntityLabel,
                StrideUsed: row.StrideUsed,
                RootIdHex: row.RootIdHex,
                CandidateCount: row.CandidateCount,
                OrderedContextCount: row.OrderedContextCount,
                ProposalChannelCount: row.ProposalChannelCount,
                ExactChannelCount: row.ExactChannelCount,
                SequenceOccurrences: row.SequenceOccurrences,
                CoveredOccurrences: row.CoveredOccurrences,
                RelationFamilies: row.RelationFamilies,
                OpposedOccurrences: row.OpposedOccurrences,
                SupportAnchorIdHex: row.SupportAnchorIdHex,
                SupportAnchor: row.SupportAnchorLabel,
                SupportRelationIdHex: row.SupportRelationIdHex,
                SupportRelation: row.SupportRelationLabel,
                SupportOutbound: row.SupportOutbound,
                SupportRating: row.SupportRating,
                SupportRd: row.SupportRd,
                SupportWitnesses: row.SupportWitnesses,
                SupportSources: row.SupportSources,
                SupportContexts: row.SupportContexts,
                DeclaredResult: row.DeclaredResult,
                Event: row.Event,
                RoutingRound: row.RoutingRound))];
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException(
                "Canonical forward trace query failed.", ex);
        }
    }
}
