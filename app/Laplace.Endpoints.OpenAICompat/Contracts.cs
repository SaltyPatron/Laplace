using System.Text.Json.Serialization;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record ChatCompletionsRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage>? Messages);

internal sealed record ChatMessage(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content);

internal sealed record CompletionsRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("prompt")] string? Prompt);

internal sealed record EmbeddingsRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("input")] string? Input);

internal sealed record BillingPreflightRequest(
    [property: JsonPropertyName("service_id")] string? ServiceId,
    [property: JsonPropertyName("units")] int Units,
    [property: JsonPropertyName("tenant")] string? Tenant);

internal sealed record ConverseRow(string Reply, decimal EffectiveMu, long Witnesses);

internal sealed record CompletionRow(
    string ObjectIdHex,
    string KindIdHex,
    decimal EffectiveMu,
    long Witnesses,
    string ObjectLabel);
