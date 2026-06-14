using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The substrate read surface consumed by endpoint handlers. The production implementation
/// (<see cref="SubstrateClient"/>) executes laplace.* SQL functions over Npgsql; tests substitute
/// a deterministic fake so route response shapes can be pinned without a live database.
/// </summary>
internal interface ISubstrateClient
{
    /// <inheritdoc cref="SubstrateClient.ConverseTurnsAsync"/>
    Task<IReadOnlyList<ConverseRow>> ConverseTurnsAsync(
        IReadOnlyList<string> userTurns, byte[]? session, CancellationToken ct);

    /// <inheritdoc cref="SubstrateClient.WalkTextStreamAsync"/>
    IAsyncEnumerable<GenerateToken> WalkTextStreamAsync(
        string prompt,
        int steps          = 32,
        int maxOrder       = 5,
        double temperature = 0.7,
        int topK           = 10,
        CancellationToken ct = default);

    Task<IReadOnlyList<CompletionRow>> CompletionsAsync(string prompt, int limit, CancellationToken ct);

    Task<SubstrateAuditReport> AuditReportAsync(
        bool includeConsensus, bool includeConvergence, int topRelationLimit, CancellationToken ct);

    Task<SubstrateVisualizationGraph> VisualizationGraphAsync(
        int limit, bool includeGeometry, bool includeEvidence, CancellationToken ct);

    Task<IReadOnlyList<ExplainTraceStep>> ExplainTraceAsync(
        string prompt, int depth, int beam, bool includeEvidence, CancellationToken ct);

    /// <inheritdoc cref="SubstrateClient.EvidenceAsync"/>
    Task<EntityEvidence?> EvidenceAsync(string target, int limit, CancellationToken ct);
}
