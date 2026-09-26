using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// Per-modality resident counts from targeted per-source and per-plane counts
/// (ops.modality_counts), not the unbounded ops.source_counts() aggregate.
/// </summary>
public sealed record ModalitiesResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("text")] long Text,
    [property: JsonPropertyName("chess")] long Chess,
    [property: JsonPropertyName("models")] long Models,
    [property: JsonPropertyName("multilingual")] long Multilingual,
    [property: JsonPropertyName("documents")] long Documents = 0);
