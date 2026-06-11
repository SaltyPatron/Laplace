using System.Runtime.CompilerServices;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Deterministic substrate stand-in for shape tests: fixed rows, fixed ids, no database.
/// Values are arbitrary but stable — golden files pin the serialized form of exactly this data.
/// </summary>
internal sealed class FakeSubstrateClient : ISubstrateClient
{
    private const string WhaleIdHex    = "00112233445566778899aabbccddeeff";
    private const string CetaceanIdHex = "ffeeddccbbaa99887766554433221100";
    private const string IsAIdHex      = "0123456789abcdef0123456789abcdef";
    private const string WordNetIdHex  = "fedcba9876543210fedcba9876543210";

    public Task<IReadOnlyList<ConverseRow>> ConverseTurnsAsync(
        IReadOnlyList<string> userTurns, byte[]? session, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ConverseRow>>(
        [
            new ConverseRow("A whale is a marine mammal.", 0.91m, 42),
            new ConverseRow("whale IS_A cetacean.", 0.84m, 17)
        ]);

    public async IAsyncEnumerable<GenerateToken> GenerateNgramStreamAsync(
        string prompt,
        int steps = 32,
        int maxOrder = 5,
        double temperature = 0.7,
        int topK = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return new GenerateToken(1, " the", 5);
        yield return new GenerateToken(2, " whale", 4);
        yield return new GenerateToken(3, " sings", 3);
    }

    public Task<IReadOnlyList<CompletionRow>> CompletionsAsync(string prompt, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CompletionRow>>(
        [
            new CompletionRow(CetaceanIdHex, IsAIdHex, 0.88m, 23, "cetacean")
        ]);

    public Task<SubstrateAuditReport> AuditReportAsync(
        bool includeConsensus, bool includeConvergence, int topRelationLimit, CancellationToken ct) =>
        Task.FromResult(new SubstrateAuditReport(
            Counts:
            [
                new SubstrateCount("entities", 1_000_000),
                new SubstrateCount("attestations", 5_000_000)
            ],
            Consensus: includeConsensus
                ? new ConsensusHealth(5_000_000, 1_500_000, 3.33m, 4.2m, 9001)
                : null,
            MultiSourceEntityCount: includeConvergence ? 250_000 : null,
            TopRelations: [TopEdge()]));

    public Task<SubstrateVisualizationGraph> VisualizationGraphAsync(
        int limit, bool includeGeometry, bool includeEvidence, CancellationToken ct) =>
        Task.FromResult(new SubstrateVisualizationGraph(
            Nodes:
            [
                new VisualizationNode(
                    WhaleIdHex, "whale",
                    X: includeGeometry ? 0.5 : null,
                    Y: includeGeometry ? -0.25 : null,
                    Z: includeGeometry ? 0.125 : null,
                    M: includeGeometry ? 0.8125 : null,
                    Radius: includeGeometry ? 1.0 : null,
                    Constituents: includeGeometry ? 5 : null,
                    EvidenceRows: includeEvidence ? 42 : null),
                new VisualizationNode(
                    CetaceanIdHex, "cetacean",
                    X: includeGeometry ? -0.5 : null,
                    Y: includeGeometry ? 0.25 : null,
                    Z: includeGeometry ? -0.125 : null,
                    M: includeGeometry ? 0.8125 : null,
                    Radius: includeGeometry ? 1.0 : null,
                    Constituents: includeGeometry ? 8 : null,
                    EvidenceRows: includeEvidence ? 17 : null)
            ],
            Edges: [TopEdge()]));

    public Task<IReadOnlyList<ExplainTraceStep>> ExplainTraceAsync(
        string prompt, int depth, int beam, bool includeEvidence, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExplainTraceStep>>(
        [
            new ExplainTraceStep(
                Depth: 1,
                PathHex: [WhaleIdHex],
                TypePathHex: [IsAIdHex],
                EntityIdHex: WhaleIdHex,
                EntityLabel: "whale",
                EffectiveMu: 0.91m,
                PathMu: 0.91m,
                Witnesses: 42,
                Evidence: includeEvidence ? [Sample()] : Array.Empty<EvidenceSample>()),
            new ExplainTraceStep(
                Depth: 2,
                PathHex: [WhaleIdHex, CetaceanIdHex],
                TypePathHex: [IsAIdHex, IsAIdHex],
                EntityIdHex: CetaceanIdHex,
                EntityLabel: "cetacean",
                EffectiveMu: 0.84m,
                PathMu: 0.7644m,
                Witnesses: 17,
                Evidence: includeEvidence ? [Sample()] : Array.Empty<EvidenceSample>())
        ]);

    public Task<EntityEvidence?> EvidenceAsync(string target, int limit, CancellationToken ct)
    {
        if (target is "unknown-word" or "00000000000000000000000000000000")
            return Task.FromResult<EntityEvidence?>(null);

        return Task.FromResult<EntityEvidence?>(new EntityEvidence(
            WhaleIdHex,
            "whale",
            [
                new LabeledEvidenceItem(
                    TypeId: IsAIdHex,
                    TypeLabel: "IS_A",
                    ObjectId: CetaceanIdHex,
                    ObjectLabel: "cetacean",
                    SourceId: WordNetIdHex,
                    SourceLabel: "WordNet",
                    ContextId: null,
                    Outcome: 2,
                    ObservationCount: 12),
                new LabeledEvidenceItem(
                    TypeId: IsAIdHex,
                    TypeLabel: "IS_A",
                    ObjectId: CetaceanIdHex,
                    ObjectLabel: "cetacean",
                    SourceId: WordNetIdHex,
                    SourceLabel: "ConceptNet",
                    ContextId: null,
                    Outcome: 2,
                    ObservationCount: 7)
            ]));
    }

    private static VisualizationEdge TopEdge() => new(
        SubjectIdHex: WhaleIdHex,
        Subject: "whale",
        TypeIdHex: IsAIdHex,
        Type: "IS_A",
        ObjectIdHex: CetaceanIdHex,
        Object: "cetacean",
        EffectiveMu: 0.91m,
        Witnesses: 42);

    private static EvidenceSample Sample() => new(
        TypeIdHex: IsAIdHex,
        ObjectIdHex: CetaceanIdHex,
        SourceIdHex: WordNetIdHex,
        ContextIdHex: null,
        Outcome: 2,
        ObservationCount: 12);
}
