using System.Text.Json;
using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// Calls an installed substrate operation by name from the catalog allow-list; the same
/// call as MCP <c>op</c>.
/// </summary>
public sealed record OpRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("args")] Dictionary<string, JsonElement>? Args = null,
    [property: JsonPropertyName("max_rows")] int? MaxRows = null,
    [property: JsonPropertyName("timeout_seconds")] int? TimeoutSeconds = null);

public sealed record OpResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rows")] IReadOnlyList<Dictionary<string, object?>> Rows,
    [property: JsonPropertyName("truncated_at")] int? TruncatedAt = null);
