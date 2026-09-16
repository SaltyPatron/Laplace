using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

public sealed record OpParameterDescription(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("optional")] bool Optional);

public sealed record OpDescription(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("args")] string Args,
    [property: JsonPropertyName("returns")] string? Returns,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("parameters")] IReadOnlyList<OpParameterDescription> Parameters,
    [property: JsonPropertyName("writable")] bool Writable,
    [property: JsonPropertyName("destructive")] bool Destructive);

public sealed record OpCatalogResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("operations")] IReadOnlyList<OpDescription> Operations,
    [property: JsonPropertyName("truncated_at")] int? TruncatedAt);
