using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

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

public sealed record SubstrateVisualizationGraph(
    IReadOnlyList<VisualizationNode> Nodes,
    IReadOnlyList<VisualizationEdge> Edges);

public sealed record EvidenceSample(
    string TypeIdHex,
    string ObjectIdHex,
    string SourceIdHex,
    string? ContextIdHex,
    short Outcome,
    long ObservationCount);

public sealed record MachineCostResourcePressure(
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("pressure_per_iteration")] double PressurePerIteration);

public sealed record MachineCostResponse(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("artifact_sha256")] string ArtifactSha256,
    [property: JsonPropertyName("artifact_bytes")] long ArtifactBytes,
    [property: JsonPropertyName("artifact_name")] string ArtifactName,
    [property: JsonPropertyName("object_format")] string ObjectFormat,
    [property: JsonPropertyName("target_triple")] string TargetTriple,
    [property: JsonPropertyName("cpu")] string Cpu,
    [property: JsonPropertyName("clock_hz")] double ClockHz,
    [property: JsonPropertyName("iterations")] int Iterations,
    [property: JsonPropertyName("static_instruction_count")] long StaticInstructionCount,
    [property: JsonPropertyName("scheduled_instruction_instances")] long ScheduledInstructionInstances,
    [property: JsonPropertyName("total_cycles")] long TotalCycles,
    [property: JsonPropertyName("total_uops")] long? TotalUops,
    [property: JsonPropertyName("dispatch_width")] int? DispatchWidth,
    [property: JsonPropertyName("uops_per_cycle")] double? UopsPerCycle,
    [property: JsonPropertyName("ipc")] double? Ipc,
    [property: JsonPropertyName("block_rthroughput_cycles")] double? BlockRThroughputCycles,
    [property: JsonPropertyName("calculated_seconds")] double CalculatedSeconds,
    [property: JsonPropertyName("calculated_nanoseconds")] double CalculatedNanoseconds,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("control_flow_weighted")] bool ControlFlowWeighted,
    [property: JsonPropertyName("resource_pressure")] IReadOnlyList<MachineCostResourcePressure> ResourcePressure,
    [property: JsonPropertyName("objdump_version")] string ObjdumpVersion,
    [property: JsonPropertyName("mca_version")] string McaVersion,
    [property: JsonPropertyName("assumptions")] IReadOnlyList<string> Assumptions);

/// <summary>
/// Receipt from one routing/election event of the canonical native forward pass.
/// These fields are execution facts, not a reconstruction from a separate graph walk.
/// </summary>
public sealed record ForwardTraceStep(
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("entity_id_hex")] string EntityIdHex,
    [property: JsonPropertyName("entity")] string Entity,
    [property: JsonPropertyName("stride_used")] int StrideUsed,
    [property: JsonPropertyName("root_id_hex")] string RootIdHex,
    [property: JsonPropertyName("candidate_count")] int CandidateCount,
    [property: JsonPropertyName("ordered_context_count")] int OrderedContextCount,
    [property: JsonPropertyName("proposal_channel_count")] int ProposalChannelCount,
    [property: JsonPropertyName("exact_channel_count")] int ExactChannelCount,
    [property: JsonPropertyName("sequence_occurrences")] long SequenceOccurrences,
    [property: JsonPropertyName("covered_occurrences")] int CoveredOccurrences,
    [property: JsonPropertyName("relation_families")] int RelationFamilies,
    [property: JsonPropertyName("opposed_occurrences")] int OpposedOccurrences,
    [property: JsonPropertyName("support_anchor_id_hex"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SupportAnchorIdHex,
    [property: JsonPropertyName("support_anchor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SupportAnchor,
    [property: JsonPropertyName("support_relation_id_hex"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SupportRelationIdHex,
    [property: JsonPropertyName("support_relation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SupportRelation,
    [property: JsonPropertyName("support_outbound"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SupportOutbound,
    [property: JsonPropertyName("support_rating"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SupportRating,
    [property: JsonPropertyName("support_rd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SupportRd,
    [property: JsonPropertyName("support_witnesses"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SupportWitnesses,
    [property: JsonPropertyName("support_sources")] int SupportSources,
    [property: JsonPropertyName("support_contexts")] int SupportContexts,
    [property: JsonPropertyName("declared_result")] bool DeclaredResult,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("routing_round")] int RoutingRound);

// Legacy graph-walk response retained only for consumers that still call the old
// ExplainTraceAsync surface. /v1/explain/report and the Explore UI use ForwardTraceStep.
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
