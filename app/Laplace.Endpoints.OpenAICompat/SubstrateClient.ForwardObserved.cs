using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{
    public async IAsyncEnumerable<ForwardObservedEvent> ForwardTurnObservedStreamAsync(
        string prompt,
        byte[]? session,
        ConverseOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in NpgsqlSubstrateReads.ForwardTurnObservedStepsAsync(
            _dataSource,
            prompt,
            session,
            options.MaxTokens ?? 128,
            options.Window ?? 5,
            options.Temperature ?? 0.6,
            options.TopK ?? 10,
            ct))
        {
            yield return new ForwardObservedEvent(
                Step: row.Step,
                Event: row.Event,
                RoutingRound: row.RoutingRound,
                EntityIdHex: row.EntityIdHex ?? string.Empty,
                Entity: row.EntityLabel ?? string.Empty,
                Surface: row.Surface,
                StrideUsed: row.StrideUsed,
                RootIdHex: row.RootIdHex ?? string.Empty,
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
                ProgramIdHex: row.ProgramIdHex,
                RequiredObligations: row.RequiredObligations,
                SatisfiedObligations: row.SatisfiedObligations,
                RemainingRequired: row.RemainingRequired,
                Completion: row.Completion,
                Disposition: row.Disposition,
                OutputFingerprintHex: row.OutputFingerprintHex,
                SemanticActIdHex: row.SemanticActIdHex,
                OutputCount: row.OutputCount,
                PriorDiscourseIds: row.PriorDiscourseIds);
        }
    }
}
