using System.Text.Json.Nodes;
using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal interface ISubstrateClient
{

    Task<string?> PerfcacheReceiptHexAsync(CancellationToken ct) =>
        Task.FromResult<string?>(null);

    Task<IReadOnlyList<QueryShape>> QueryShapesAsync(CancellationToken ct);

    Task<IReadOnlyList<RelationBand>> RelationBandsAsync(CancellationToken ct);

    Task<HighwayPopulationStatus> HighwayPopulationAsync(CancellationToken ct) =>
        Task.FromException<HighwayPopulationStatus>(
            new NotSupportedException("This substrate client does not expose Highway population state."));

    Task<(byte[] Id, string Label)?> ResolveTopicAsync(string reference, CancellationToken ct);

    Task<IReadOnlyList<QueryRow>> QueryAsync(
        string shape, byte[] topic, byte[]? topic2, string? relationType, string? lang,
        byte[][]? contextIds, int[]? bands, QueryDials dials, CancellationToken ct);

    Task<PulseResponse> PulseAsync(long nowUnix, CancellationToken ct);

    Task<ModalitiesResponse> ModalitiesAsync(CancellationToken ct);

    Task<MeshResponse?> MeshAsync(string idHex, int relationLimit, int memberLimit, CancellationToken ct);

    Task<TaxonomyResponse?> TaxonomyAsync(string idHex, int depth, int childLimit, CancellationToken ct);

    Task<IReadOnlyList<SourceRosterRow>> SourceRosterAsync(byte[] sourceId, int limit, CancellationToken ct);

    Task<IReadOnlyList<BandLeaders>> LeadersAsync(int[] bands, int perBand, CancellationToken ct);

    Task<EntityRecordResponse?> EntityRecordAsync(string idHex, CancellationToken ct);

    Task<MatchupResponse?> MatchupAsync(string xRef, string yRef, CancellationToken ct);

    Task<MatchupVerdictResponse?> MatchupVerdictAsync(string xRef, string yRef, CancellationToken ct);

    Task<ResponseFieldResponse?> ResponseFieldAsync(string xRef, string yRef, int fanout, int nearest, CancellationToken ct);
    Task<ResponseFieldResponse?> LexiconAsync(string xRef, string? yRef, CancellationToken ct);

    Task<IReadOnlyList<CompletionRow>> CompletionsAsync(string prompt, int limit, CancellationToken ct);

    Task<SubstrateAuditReport> AuditReportAsync(
        bool includeConsensus, bool includeConvergence, int topRelationLimit, CancellationToken ct);

    Task<SubstrateVisualizationGraph> VisualizationGraphAsync(
        int limit, bool includeGeometry, bool includeEvidence, CancellationToken ct);

    /// <summary>
    /// Runs the native forward pass that generation runs and returns its receipts; the
    /// controls are the forward pass's own.
    /// </summary>
    Task<IReadOnlyList<ForwardTraceStep>> ForwardTraceAsync(
        string prompt, int steps, int maxStride, double spread, int topK,
        int hops, int fanout, CancellationToken ct);

    Task<IReadOnlyList<ExplainTraceStep>> ExplainTraceAsync(
        string prompt, int depth, int beam, bool includeEvidence, CancellationToken ct);


    Task<EntityEvidence?> EvidenceAsync(string target, int limit, CancellationToken ct);


    Task<ReadinessResponse> ReadinessAsync(CancellationToken ct);


    Task<EmbeddingResult> EmbeddingAsync(string input, bool includeMeaning, int meaningLimit, CancellationToken ct);

    Task<ExploreCatalogResponse> ExploreCatalogAsync(CancellationToken ct);

    Task<ExploreResolveResponse?> ExploreResolveAsync(string reference, CancellationToken ct);

    Task<ExploreBrowseResponse> ExploreBrowseAsync(
        string query,
        string queryRootIdHex,
        IReadOnlyList<string> queryMemberIdsHex,
        int offset,
        int limit,
        int candidateCapacity,
        CancellationToken ct) =>
        throw new NotSupportedException("This substrate client does not implement Browse.");

    Task<ExploreEntityPreviewResponse?> ExploreEntityPreviewAsync(string idHex, CancellationToken ct);

    Task<ExploreEntityResponse?> ExploreEntityAsync(
        string idHex, int consensusLimit, int evidenceLimit, CancellationToken ct);

    Task<IReadOnlyList<ExploreAnchorNeighborRow>> ExploreAnchorNeighborsAsync(
        ExploreAnchor anchor, int geodesicK, int frechetK, double frechetMax, CancellationToken ct);

    Task<IReadOnlyList<WitnessedWord>> WitnessedWordsAsync(
        IReadOnlyList<string> surfaces, CancellationToken ct);

    Task<ExploreTrainingExportResponse?> ExploreTrainingExportAsync(
        string idHex, int consensusLimit, int evidenceLimit, bool includeMembers, bool includePeers, CancellationToken ct);

    Task<ExploreNeighborsResponse?> ExploreNeighborsAsync(string idHex, int k, CancellationToken ct);

    Task<ExploreMembersResponse?> ExploreMembersAsync(string idHex, int limit, CancellationToken ct);

    Task<ExplorePeersResponse?> ExplorePeersAsync(string idHex, int limit, CancellationToken ct);

    Task<ExploreContainersResponse?> ExploreContainersAsync(string idHex, int maxHops, int limit, CancellationToken ct);

    Task<ExploreGraphResponse?> ExploreConsensusGraphAsync(
        string idHex, int hops, int fanout, int maxNodes, CancellationToken ct);

    /// <summary>Invokes an installed catalog operation by name; the same call as MCP <c>op</c> and <c>POST /v1/op</c>.</summary>
    Task<InstalledOpInvoker.OpResult> InvokeOpAsync(
        string name, IReadOnlyDictionary<string, JsonNode?>? args, int maxRows,
        int timeoutSeconds, CancellationToken ct);

    Task<IReadOnlyDictionary<Laplace.Engine.Core.Hash128, Laplace.Engine.Core.Hash128[]>?> ResolveSemanticPeersAsync(
        IEnumerable<Laplace.Engine.Core.Hash128> wordIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Laplace.Engine.Core.Hash128, Laplace.Engine.Core.Hash128[]>?>(null);
}
