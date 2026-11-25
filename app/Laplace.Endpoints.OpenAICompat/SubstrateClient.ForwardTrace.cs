using Laplace.Api.Contracts;
using Laplace.Data;
using Laplace.Engine.Core;
using Laplace.Native;
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
        if (_engineRuntime?.Runtime is { } engine)
        {
            try
            {
                var abi = await Task.Run(() => engine.Turn(new EngineTurnRequest(
                    FirmwarePath: null,
                    ModelName: "program",
                    Prompt: prompt,
                    Messages: null,
                    SystemPrompt: null,
                    User: "trace",
                    Session: "trace-readonly",
                    ReadOnly: true,
                    MaxTokens: steps,
                    Temperature: 0.0,
                    StopSequences: null,
                    Seed: 0,
                    Authority: null,
                    Envelope: null)), ct).ConfigureAwait(false);

                if (abi.StatusCode == 0 && (abi.Steps is { Count: > 0 } || !string.IsNullOrWhiteSpace(abi.Text)))
                {
                    string rootHex = abi.PromptId == Hash128.Zero ? new string('0', 32) : abi.PromptId.ToHex();
                    int stepCount = Math.Max(1, abi.Steps?.Count ?? 1);

                    var nativeEmitSteps = new List<ForwardTraceStep>();
                    if (abi.Steps is { Count: > 0 } abiSteps)
                    {
                        foreach (var ns in abiSteps)
                        {
                            string entityHex = ns.EntityId == Hash128.Zero
                                ? (abi.ResponseId == Hash128.Zero ? new string('0', 32) : abi.ResponseId.ToHex())
                                : ns.EntityId.ToHex();
                            nativeEmitSteps.Add(new ForwardTraceStep(
                                Step: ns.Step,
                                EntityIdHex: entityHex,
                                Entity: ns.Text,
                                StrideUsed: Math.Max(1, ns.Continuity),
                                RootIdHex: rootHex,
                                CandidateCount: stepCount,
                                OrderedContextCount: 1,
                                ProposalChannelCount: Math.Max(1, ns.Continuity),
                                ExactChannelCount: 1,
                                SequenceOccurrences: 0,
                                CoveredOccurrences: Math.Max(1, ns.Continuity),
                                RelationFamilies: 0,
                                OpposedOccurrences: 0,
                                SupportAnchorIdHex: rootHex,
                                SupportAnchor: null,
                                SupportRelationIdHex: null,
                                SupportRelation: ns.Kind,
                                SupportOutbound: true,
                                SupportRating: (long)Math.Round(1500.0 + ns.Confidence * 300.0),
                                SupportRd: null,
                                SupportWitnesses: null,
                                SupportSources: 0,
                                SupportContexts: 0,
                                DeclaredResult: true,
                                Event: "emit",
                                RoutingRound: 0));
                        }
                    }
                    else
                    {
                        string respHex = abi.ResponseId == Hash128.Zero ? new string('0', 32) : abi.ResponseId.ToHex();
                        nativeEmitSteps.Add(new ForwardTraceStep(
                            Step: 1,
                            EntityIdHex: respHex,
                            Entity: abi.Text!,
                            StrideUsed: 1,
                            RootIdHex: rootHex,
                            CandidateCount: stepCount,
                            OrderedContextCount: 1,
                            ProposalChannelCount: 1,
                            ExactChannelCount: 1,
                            SequenceOccurrences: 0,
                            CoveredOccurrences: 1,
                            RelationFamilies: 0,
                            OpposedOccurrences: 0,
                            SupportAnchorIdHex: rootHex,
                            SupportAnchor: null,
                            SupportRelationIdHex: null,
                            SupportRelation: string.IsNullOrWhiteSpace(abi.Disposition) ? "answers" : abi.Disposition,
                            SupportOutbound: true,
                            SupportRating: (long)Math.Round(1500.0 + abi.MeanConfidence * 300.0),
                            SupportRd: null,
                            SupportWitnesses: null,
                            SupportSources: 0,
                            SupportContexts: 0,
                            DeclaredResult: true,
                            Event: "emit",
                            RoutingRound: 0));
                    }

                    return nativeEmitSteps;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Serilog.Log.ForContext<SubstrateClient>().Warning(ex,
                    "Native forward pass trace failed; falling back to SQL trace rows.");
            }
        }

        IReadOnlyList<NpgsqlSubstrateReads.ForwardTraceRow> rows;
        try
        {
            rows = await NpgsqlSubstrateReads.ForwardTraceStepsAsync(
                _dataSource,
                prompt,
                steps,
                maxStride,
                spread,
                topK,
                hops,
                fanout,
                ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException(
                "Canonical forward trace query failed.", ex);
        }

        var mappedSql = rows.Select(static row => new ForwardTraceStep(
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
            RoutingRound: row.RoutingRound)).ToList();

        return mappedSql;
    }
}
