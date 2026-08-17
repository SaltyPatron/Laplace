using System.Text.Json.Nodes;
using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal readonly record struct ConverseOptions(
    string? Shape = null,
    int[]? Bands = null,
    bool Elaborate = false,
    string? LanguageCode = null,
    byte[]? Language = null);






internal interface ISubstrateClient
{

    Task<IReadOnlyList<QueryShape>> QueryShapesAsync(CancellationToken ct);

    Task<IReadOnlyList<RelationBand>> RelationBandsAsync(CancellationToken ct);

    Task<(byte[] Id, string Label)?> ResolveTopicAsync(string reference, CancellationToken ct);

    Task<IReadOnlyList<QueryRow>> QueryAsync(
        string shape, byte[] topic, byte[]? topic2, string? relationType, string? lang,
        byte[][]? contextIds, int[]? bands, QueryDials dials, CancellationToken ct);

    Task<IReadOnlyList<ConverseRow>> ConverseAsync(
        string prompt, byte[]? session, CancellationToken ct);

    Task<IReadOnlyList<ConverseRow>> ConverseAsync(
        string prompt, byte[]? session, ConverseOptions options, CancellationToken ct) =>
        ConverseAsync(prompt, session, ct);

    Task<IReadOnlyList<ConverseRow>> ConverseTenantScopedAsync(
        string prompt, byte[]? session, byte[][] scopeSources, CancellationToken ct);

    Task<IReadOnlyList<ConverseRow>> ConverseTenantScopedAsync(
        string prompt, byte[]? session, byte[][] scopeSources,
        ConverseOptions options, CancellationToken ct) =>
        ConverseTenantScopedAsync(prompt, session, scopeSources, ct);

    Task<PulseResponse> PulseAsync(long nowUnix, CancellationToken ct);

    Task<ModalitiesResponse> ModalitiesAsync(CancellationToken ct);

    Task<MeshResponse?> MeshAsync(string idHex, CancellationToken ct);

    Task<TaxonomyResponse?> TaxonomyAsync(string idHex, CancellationToken ct);

    Task<IReadOnlyList<SourceRosterRow>> SourceRosterAsync(byte[] sourceId, int limit, CancellationToken ct);

    Task<IReadOnlyList<BandLeaders>> LeadersAsync(int[] bands, int perBand, CancellationToken ct);

    Task<EntityRecordResponse?> EntityRecordAsync(string idHex, CancellationToken ct);

    Task<MatchupResponse?> MatchupAsync(string xRef, string yRef, CancellationToken ct);

    Task<MatchupVerdictResponse?> MatchupVerdictAsync(string xRef, string yRef, CancellationToken ct);

    Task<IReadOnlyList<ChessPlayerRow>> ChessRosterAsync(int limit, int offset, CancellationToken ct);

    Task<ChessPlayersResponse> ChessPlayersAsync(int limit, int offset, string? search, CancellationToken ct);

    Task<ChessPlayersResponse> ChessPlayersAsync(int limit, int offset, string? search, string? initial, CancellationToken ct);

    Task<ChessPlayerResponse?> ChessPlayerAsync(string idHex, int opponentLimit, CancellationToken ct);

    Task<ChessGamesResponse?> ChessPlayerGamesAsync(string idHex, int limit, int offset, CancellationToken ct);

    Task<ChessGameResponse?> ChessGameAsync(string idHex, CancellationToken ct);

    Task<ChessGamePliesResponse?> ChessGamePliesAsync(string idHex, CancellationToken ct);

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
        string idHex, int hops, int fanout, CancellationToken ct);

    /// <summary>Installed-catalog op invoker — MCP <c>op</c> / HTTP <c>POST /v1/op</c> (GH #812).</summary>
    Task<InstalledOpInvoker.OpResult> InvokeOpAsync(
        string name, IReadOnlyDictionary<string, JsonNode?>? args, int maxRows,
        int timeoutSeconds, CancellationToken ct);
}
