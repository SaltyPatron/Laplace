using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

public sealed record CodeCompletionRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("code_language")] string? CodeLanguage,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null,
    [property: JsonPropertyName("window")] int? Window = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("top_k")] int? TopK = null,
    [property: JsonPropertyName("max_attempts")] int? MaxAttempts = null);

public sealed record CodeAttemptReceipt(
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("tool_available")] bool ToolAvailable,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("exit_code")] int ExitCode,
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stderr")] string Stderr);

public sealed record CodeCompletionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("code_language")] string CodeLanguage,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("candidate_id")] string? CandidateId,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("failure_kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FailureKind,
    [property: JsonPropertyName("attempts")] IReadOnlyList<CodeAttemptReceipt> Attempts,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);
