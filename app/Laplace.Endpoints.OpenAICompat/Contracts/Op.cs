using System.Text.Json;
using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// Call an installed substrate operation by name (catalog allow-list).
/// Parity with MCP <c>op</c> — GH #812.
/// </summary>
public sealed record OpRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("args")] Dictionary<string, JsonElement>? Args = null,
    [property: JsonPropertyName("max_rows")] int? MaxRows = null);

public sealed record OpResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rows")] IReadOnlyList<Dictionary<string, object?>> Rows,
    [property: JsonPropertyName("truncated_at")] int? TruncatedAt = null);
