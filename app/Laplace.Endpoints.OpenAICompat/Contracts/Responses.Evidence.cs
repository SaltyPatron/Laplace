using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;





public sealed record EvidenceResponse(
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("entity_label")] string EntityLabel,
    [property: JsonPropertyName("evidence")] IReadOnlyList<LabeledEvidenceItem> Evidence);


public sealed record LabeledEvidenceItem(
    [property: JsonPropertyName("type_id")] string TypeId,
    [property: JsonPropertyName("type_label")] string TypeLabel,
    [property: JsonPropertyName("object_id")] string ObjectId,
    [property: JsonPropertyName("object_label")] string ObjectLabel,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source_label")] string SourceLabel,
    [property: JsonPropertyName("context_id")] string? ContextId,
    [property: JsonPropertyName("outcome")] short Outcome,
    [property: JsonPropertyName("observation_count")] long ObservationCount,
    [property: JsonPropertyName("eff_mu")] decimal? EffMu = null);
