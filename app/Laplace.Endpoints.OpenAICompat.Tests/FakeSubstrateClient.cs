using System.Runtime.CompilerServices;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat;

namespace Laplace.Endpoints.OpenAICompat.Tests;





internal sealed class UnreachableSubstrateClient : ISubstrateClient
{
    public Task<IReadOnlyList<ConverseRow>> ConverseTurnsAsync(
        IReadOnlyList<string> userTurns, byte[]? session, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public IAsyncEnumerable<GenerateToken> WalkTextStreamAsync(
        string prompt, int steps = 32, int maxOrder = 5, double temperature = 0.7, int topK = 10,
        CancellationToken ct = default) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<CompletionRow>> CompletionsAsync(string prompt, int limit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<SubstrateAuditReport> AuditReportAsync(
        bool includeConsensus, bool includeConvergence, int topRelationLimit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<SubstrateVisualizationGraph> VisualizationGraphAsync(
        int limit, bool includeGeometry, bool includeEvidence, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<ExplainTraceStep>> ExplainTraceAsync(
        string prompt, int depth, int beam, bool includeEvidence, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<EntityEvidence?> EvidenceAsync(string target, int limit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<EmbeddingResult> EmbeddingAsync(
        string input, bool includeMeaning, int meaningLimit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ReadinessResponse> ReadinessAsync(CancellationToken ct) =>
        Task.FromResult(new ReadinessResponse(
            Ready: false,
            SubstrateReachable: false,
            Entities: 0,
            ConsensusRelations: 0,
            PerfcacheReady: false));

    public Task<ExploreCatalogResponse> ExploreCatalogAsync(CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreResolveResponse?> ExploreResolveAsync(string reference, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreEntityPreviewResponse?> ExploreEntityPreviewAsync(string idHex, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreEntityResponse?> ExploreEntityAsync(
        string idHex, int consensusLimit, int evidenceLimit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<ExploreAnchorNeighborRow>> ExploreAnchorNeighborsAsync(
        ExploreAnchor anchor, int geodesicK, int frechetK, double frechetMax, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<WitnessedWord>> WitnessedWordsAsync(
        IReadOnlyList<string> surfaces, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreTrainingExportResponse?> ExploreTrainingExportAsync(
        string idHex, int consensusLimit, int evidenceLimit, bool includeMembers, bool includePeers, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreNeighborsResponse?> ExploreNeighborsAsync(string idHex, int k, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreMembersResponse?> ExploreMembersAsync(string idHex, int limit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExplorePeersResponse?> ExplorePeersAsync(string idHex, int limit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreContainersResponse?> ExploreContainersAsync(
        string idHex, int maxHops, int limit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreGraphResponse?> ExploreConsensusGraphAsync(
        string idHex, int hops, int fanout, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());
}

internal sealed class FakeSubstrateClient : ISubstrateClient
{
    private const string WhaleIdHex = "00112233445566778899aabbccddeeff";
    private const string CetaceanIdHex = "ffeeddccbbaa99887766554433221100";
    private const string IsAIdHex = "0123456789abcdef0123456789abcdef";
    private const string WordNetIdHex = "fedcba9876543210fedcba9876543210";

    public Task<IReadOnlyList<ConverseRow>> ConverseTurnsAsync(
        IReadOnlyList<string> userTurns, byte[]? session, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ConverseRow>>(
        [
            new ConverseRow("A whale is a marine mammal.", 0.91m, 42),
            new ConverseRow("whale IS_A cetacean.", 0.84m, 17)
        ]);

    public async IAsyncEnumerable<GenerateToken> WalkTextStreamAsync(
        string prompt,
        int steps = 32,
        int maxOrder = 5,
        double temperature = 0.7,
        int topK = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return new GenerateToken(1, " the", 5);

        if (prompt.Contains("trigger-stream-error", StringComparison.OrdinalIgnoreCase))
            throw new SubstrateUnavailableException("substrate went away mid-walk.", new InvalidOperationException());
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
                    TypeLabel: "is a",
                    ObjectId: CetaceanIdHex,
                    ObjectLabel: "cetacean",
                    SourceId: "",
                    SourceLabel: "WordNetDecomposer",
                    ContextId: null,
                    Outcome: 2,
                    ObservationCount: 42,
                    EffMu: 1534.7m)
            ]));
    }

    public Task<EmbeddingResult> EmbeddingAsync(string input, bool includeMeaning, int meaningLimit, CancellationToken ct)
    {
        if (input is "unknown-word")
            return Task.FromResult(new EmbeddingResult(null, null, Array.Empty<MeaningNeighbor>()));

        return Task.FromResult(new EmbeddingResult(
            WhaleIdHex,
            new EmbeddingForm(0.5, -0.25, 0.125, 0.8125, 1.0, 5),
            includeMeaning
                ?
                [
                    new MeaningNeighbor("IS_A", "cetacean", 0.91m, 42),
                    new MeaningNeighbor("HAS_DEFINITION", "a large marine mammal", 0.88m, 30)
                ]
                : Array.Empty<MeaningNeighbor>()));
    }

    public Task<ReadinessResponse> ReadinessAsync(CancellationToken ct) =>
        Task.FromResult(new ReadinessResponse(
            Ready: true,
            SubstrateReachable: true,
            Entities: 4_440_000,
            ConsensusRelations: 6_100_000,
            PerfcacheReady: true));

    public Task<ExploreCatalogResponse> ExploreCatalogAsync(CancellationToken ct) =>
        Task.FromResult(new ExploreCatalogResponse(
            Counts: [new SubstrateCount("entities", 1_000_000), new SubstrateCount("attestations", 5_000_000)],
            Consensus: new ConsensusHealth(5_000_000, 1_500_000, 3.33m, 4.2m, 9001),
            MultiSourceEntityCount: 250_000,
            TopRelations: [TopEdge()],
            Sources: [new ExploreSourceRow("WordNet", 1_000_000, 500_000, "knowledge", "L2", "synsets")],
            Stages: [new ExploreStageRow("knowledge", 2, "WordNet hub", [new ExploreStageSourceRow("wordnet", "L2", "synsets", null)])],
            FeaturedRefs: ["dog", "whale"]));

    public Task<ExploreResolveResponse?> ExploreResolveAsync(string reference, CancellationToken ct)
    {
        if (reference is "unknown-word") return Task.FromResult<ExploreResolveResponse?>(null);
        return Task.FromResult<ExploreResolveResponse?>(new ExploreResolveResponse(
            WhaleIdHex, "whale", "word", true,
            [new SalientFactRow("IS_A", "cetacean", 0.91m, 42)]));
    }

    public Task<ExploreEntityPreviewResponse?> ExploreEntityPreviewAsync(string idHex, CancellationToken ct) =>
        Task.FromResult<ExploreEntityPreviewResponse?>(new ExploreEntityPreviewResponse(
            idHex, "whale", 2, "Word", true, 42,
            [new SalientFactRow("IS_A", "cetacean", 0.91m, 42)]));

    public Task<ExploreEntityResponse?> ExploreEntityAsync(
        string idHex, int consensusLimit, int evidenceLimit, CancellationToken ct) =>
        Task.FromResult<ExploreEntityResponse?>(SampleEntity(idHex));

    public Task<IReadOnlyList<ExploreAnchorNeighborRow>> ExploreAnchorNeighborsAsync(
        ExploreAnchor anchor, int geodesicK, int frechetK, double frechetMax, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExploreAnchorNeighborRow>>(
        [
            new ExploreAnchorNeighborRow("geodesic", CetaceanIdHex, "cetacean", 2, 0.12, null),
            new ExploreAnchorNeighborRow("shape", WhaleIdHex, "whale", 2, null, 0.03),
        ]);

    public Task<IReadOnlyList<WitnessedWord>> WitnessedWordsAsync(
        IReadOnlyList<string> surfaces, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WitnessedWord>>(
            surfaces.Contains("whale")
                ? [new WitnessedWord("whale", WhaleIdHex, 42)]
                : []);

    public Task<ExploreTrainingExportResponse?> ExploreTrainingExportAsync(
        string idHex, int consensusLimit, int evidenceLimit, bool includeMembers, bool includePeers, CancellationToken ct) =>
        Task.FromResult<ExploreTrainingExportResponse?>(new ExploreTrainingExportResponse(
            idHex, "whale", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 42, 2,
            SampleEntity(idHex)!,
            includeMembers ? [new ExploreMemberRow(CetaceanIdHex, "cetacean", "synonym", 0.88m, 17)] : [],
            includePeers ? [new ExplorePeerRow("dolphin", "frame", 0.75)] : []));

    public Task<ExploreNeighborsResponse?> ExploreNeighborsAsync(string idHex, int k, CancellationToken ct) =>
        Task.FromResult<ExploreNeighborsResponse?>(new ExploreNeighborsResponse(
            idHex,
            [new ExploreNeighborRow("cetacean", 0.12, 0.34, "structural",
                NeighborIdHex: CetaceanIdHex, X: 0.1, Y: 0.2, Z: 0.3, M: 0.4, Radius: 0.5)],
            [new SalientFactRow("IS_A", "cetacean", 0.91m, 42)]));

    public Task<ExploreMembersResponse?> ExploreMembersAsync(string idHex, int limit, CancellationToken ct) =>
        Task.FromResult<ExploreMembersResponse?>(new ExploreMembersResponse(
            idHex, [new ExploreMemberRow(CetaceanIdHex, "cetacean", "synonym", 0.88m, 17)]));

    public Task<ExplorePeersResponse?> ExplorePeersAsync(string idHex, int limit, CancellationToken ct) =>
        Task.FromResult<ExplorePeersResponse?>(new ExplorePeersResponse(
            idHex, [new ExplorePeerRow("dolphin", "frame", 0.75)]));

    public Task<ExploreContainersResponse?> ExploreContainersAsync(
        string idHex, int maxHops, int limit, CancellationToken ct) =>
        Task.FromResult<ExploreContainersResponse?>(new ExploreContainersResponse(
            idHex, [new ExploreContainerRow(WhaleIdHex, "whale document", 4, "Document", 1)]));

    public Task<ExploreGraphResponse?> ExploreConsensusGraphAsync(
        string idHex, int hops, int fanout, CancellationToken ct) =>
        Task.FromResult<ExploreGraphResponse?>(new ExploreGraphResponse(
            IdHex: idHex,
            Label: "whale",
            Hops: hops,
            Fanout: fanout,
            Nodes:
            [
                new ExploreGraphNode(idHex, "whale", 0, 2),
                new ExploreGraphNode(CetaceanIdHex, "cetacean", 1, 2),
            ],
            Edges:
            [
                new ExploreGraphEdge(idHex, CetaceanIdHex, "IS_A", 0.91m, 42, 1),
            ],
            Truncated: false,
            MaxNodes: 160));

    private static ExploreEntityResponse SampleEntity(string idHex) => new(
        idHex, "whale", 2, "Word", true, 42,
        [new ExplorePhysicalityRow(1, 0.5, -0.25, 0.125, 0.8125, 1.0, 5)],
        [new SalientFactRow("IS_A", "cetacean", 0.91m, 42)],
        [new ExploreConsensusRow("out", "IS_A", CetaceanIdHex, "cetacean", 0.91m, 42)],
        [],
        [],
        [],
        [
            new LabeledEvidenceItem(
                IsAIdHex, "is a", CetaceanIdHex, "cetacean", "", "WordNetDecomposer", null, 2, 42, 1534.7m)
        ]);

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
