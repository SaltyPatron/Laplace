using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// Honest per-modality resident counts. Computed from FAST targeted queries
/// (per-source / per-plane counts, tens of ms) rather than the full
/// source_counts() aggregate, which degrades to empty under a seed and would
/// make a live modality read as awaiting.
/// </summary>
public sealed record ModalitiesResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("text")] long Text,
    [property: JsonPropertyName("chess")] long Chess,
    [property: JsonPropertyName("models")] long Models,
    [property: JsonPropertyName("multilingual")] long Multilingual);
