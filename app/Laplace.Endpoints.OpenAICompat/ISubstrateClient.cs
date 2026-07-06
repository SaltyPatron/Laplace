using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;






internal interface ISubstrateClient
{

    Task<IReadOnlyList<ConverseRow>> ConverseTurnsAsync(
        IReadOnlyList<string> userTurns, byte[]? session, CancellationToken ct);


    IAsyncEnumerable<GenerateToken> WalkTextStreamAsync(
        string prompt,
        int steps = 32,
        int maxOrder = 5,
        double temperature = 0.7,
        int topK = 10,
        CancellationToken ct = default);

    Task<IReadOnlyList<CompletionRow>> CompletionsAsync(string prompt, int limit, CancellationToken ct);

    Task<SubstrateAuditReport> AuditReportAsync(
        bool includeConsensus, bool includeConvergence, int topRelationLimit, CancellationToken ct);

    Task<SubstrateVisualizationGraph> VisualizationGraphAsync(
        int limit, bool includeGeometry, bool includeEvidence, CancellationToken ct);

    Task<IReadOnlyList<ExplainTraceStep>> ExplainTraceAsync(
        string prompt, int depth, int beam, bool includeEvidence, CancellationToken ct);


    Task<EntityEvidence?> EvidenceAsync(string target, int limit, CancellationToken ct);


    Task<ReadinessResponse> ReadinessAsync(CancellationToken ct);


    Task<EmbeddingResult> EmbeddingAsync(string input, bool includeMeaning, int meaningLimit, CancellationToken ct);

    Task<ExploreCatalogResponse> ExploreCatalogAsync(CancellationToken ct);

    Task<ExploreResolveResponse?> ExploreResolveAsync(string reference, CancellationToken ct);

    Task<ExploreEntityPreviewResponse?> ExploreEntityPreviewAsync(string idHex, CancellationToken ct);

    Task<ExploreEntityResponse?> ExploreEntityAsync(
        string idHex, int consensusLimit, int evidenceLimit, CancellationToken ct);

    Task<ExploreTrainingExportResponse?> ExploreTrainingExportAsync(
        string idHex, int consensusLimit, int evidenceLimit, bool includeMembers, bool includePeers, CancellationToken ct);

    Task<ExploreNeighborsResponse?> ExploreNeighborsAsync(string idHex, int k, CancellationToken ct);

    Task<ExploreMembersResponse?> ExploreMembersAsync(string idHex, int limit, CancellationToken ct);

    Task<ExplorePeersResponse?> ExplorePeersAsync(string idHex, int limit, CancellationToken ct);

    Task<ExploreContainersResponse?> ExploreContainersAsync(string idHex, int maxHops, int limit, CancellationToken ct);
}
