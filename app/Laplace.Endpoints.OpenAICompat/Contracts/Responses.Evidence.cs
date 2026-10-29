using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;





public sealed record EvidenceResponse(
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("entity_label")] string EntityLabel,
    [property: JsonPropertyName("evidence")] IReadOnlyList<LabeledEvidenceItem> Evidence);


/// <summary>
/// A claim receipt or one witness's recorded series. Native series retain
/// games, tokens and total score; outcome and observation_count remain null
/// rather than inventing a categorical outcome or substituting witness count.
/// </summary>
public sealed record LabeledEvidenceItem(
    [property: JsonPropertyName("type_id")] string? TypeId,
    [property: JsonPropertyName("type_label")] string TypeLabel,
    [property: JsonPropertyName("object_id")] string ObjectId,
    [property: JsonPropertyName("object_label")] string ObjectLabel,
    [property: JsonPropertyName("source_id")] string? SourceId,
    [property: JsonPropertyName("source_label")] string SourceLabel,
    [property: JsonPropertyName("context_id")] string? ContextId,
    [property: JsonPropertyName("outcome")] short? Outcome,
    [property: JsonPropertyName("observation_count")] long? ObservationCount,
    [property: JsonPropertyName("eff_mu")] decimal? EffMu = null,
    [property: JsonPropertyName("witness_count")] long? WitnessCount = null,
    [property: JsonPropertyName("claim_id")] string? ClaimId = null,
    [property: JsonPropertyName("games")] double? Games = null,
    [property: JsonPropertyName("tokens")] double? Tokens = null,
    [property: JsonPropertyName("score")] double? Score = null,
    [property: JsonPropertyName("trust")] double? Trust = null,
    [property: JsonPropertyName("dependence_root")] string? DependenceRoot = null,
    [property: JsonPropertyName("position")] int? Position = null);
