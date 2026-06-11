namespace Laplace.Api.Contracts;

// Records embedded inside response payloads (audit report, visualization graph,
// explainability trace). These serialize through the host's web-default JsonOptions —
// camelCase on the wire — and that form is pinned by the endpoint golden shape tests.
// Do NOT add [JsonPropertyName] snake_case here; it would break the served contract.

public sealed record SubstrateCount(string Metric, long Value);

public sealed record ConsensusHealth(long EvidenceRows, long ConsensusRows, decimal? DedupRatio, decimal? AvgWitnesses, long? MaxWitnesses);

public sealed record SubstrateAuditReport(
    IReadOnlyList<SubstrateCount> Counts,
    ConsensusHealth? Consensus,
    long? MultiSourceEntityCount,
    IReadOnlyList<VisualizationEdge> TopRelations);

public sealed record VisualizationNode(
    string IdHex,
    string Label,
    double? X,
    double? Y,
    double? Z,
    double? M,
    double? Radius,
    int? Constituents,
    long? EvidenceRows);

public sealed record VisualizationEdge(
    string SubjectIdHex,
    string Subject,
    string TypeIdHex,
    string Type,
    string ObjectIdHex,
    string Object,
    decimal EffectiveMu,
    long Witnesses);

public sealed record SubstrateVisualizationGraph(IReadOnlyList<VisualizationNode> Nodes, IReadOnlyList<VisualizationEdge> Edges);

public sealed record EvidenceSample(
    string TypeIdHex,
    string ObjectIdHex,
    string SourceIdHex,
    string? ContextIdHex,
    short Outcome,
    long ObservationCount);

public sealed record ExplainTraceStep(
    int Depth,
    IReadOnlyList<string> PathHex,
    IReadOnlyList<string> TypePathHex,
    string EntityIdHex,
    string EntityLabel,
    decimal EffectiveMu,
    decimal PathMu,
    long Witnesses,
    IReadOnlyList<EvidenceSample> Evidence);
