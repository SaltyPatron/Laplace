using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// The receipt drill-down: the raw witnessed attestations behind an entity, with every
/// id resolved to a label. This is the trust surface — quote-free by design.
/// </summary>
public sealed record EvidenceResponse(
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("entity_label")] string EntityLabel,
    [property: JsonPropertyName("evidence")] IReadOnlyList<LabeledEvidenceItem> Evidence);

/// <summary>One attestation row: outcome 0=refute 1=draw 2=confirm (class, never magnitude).</summary>
public sealed record LabeledEvidenceItem(
    [property: JsonPropertyName("type_id")] string TypeId,
    [property: JsonPropertyName("type_label")] string TypeLabel,
    [property: JsonPropertyName("object_id")] string ObjectId,
    [property: JsonPropertyName("object_label")] string ObjectLabel,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source_label")] string SourceLabel,
    [property: JsonPropertyName("context_id")] string? ContextId,
    [property: JsonPropertyName("outcome")] short Outcome,
    [property: JsonPropertyName("observation_count")] long ObservationCount);
