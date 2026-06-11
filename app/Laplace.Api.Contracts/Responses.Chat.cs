using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

// OpenAI-compatible chat/completions response shapes. Declaration order = wire order (golden-pinned).

public sealed record ChatCompletionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice> Choices,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing,
    [property: JsonPropertyName("metadata")] ChatMetadata Metadata);

public sealed record ChatChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] ChatResponseMessage Message,
    [property: JsonPropertyName("finish_reason")] string FinishReason);

public sealed record ChatResponseMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>Per-path metadata: converse replies carry witnesses/reply_rows, generation carries generated_tokens.</summary>
public sealed record ChatMetadata(
    [property: JsonPropertyName("witnesses"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Witnesses = null,
    [property: JsonPropertyName("reply_rows"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ReplyRows = null,
    [property: JsonPropertyName("generated_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? GeneratedTokens = null,
    [property: JsonPropertyName("laplace"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LaplaceChatMetadata? Laplace = null);

/// <summary>
/// The anti-black-box receipt: every consensus-grounded reply line with its Glicko-2
/// effective truth value (eff_mu = rating - 2*rd, the 95% confidence lower bound) and
/// witness fan-in. OpenAI clients ignore the extension; Laplace clients render it.
/// </summary>
public sealed record LaplaceChatMetadata(
    [property: JsonPropertyName("provenance")] IReadOnlyList<ProvenanceLine> Provenance);

public sealed record ProvenanceLine(
    [property: JsonPropertyName("reply")] string Reply,
    [property: JsonPropertyName("eff_mu")] decimal EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses);

/// <summary>Per-chunk provenance extension on SSE streams (root-level "laplace" field).</summary>
public sealed record ChunkProvenance(
    [property: JsonPropertyName("eff_mu"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? EffMu = null,
    [property: JsonPropertyName("witnesses"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Witnesses = null,
    [property: JsonPropertyName("ord_used"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OrdUsed = null);

// ---- SSE chunks ----

public sealed record ChatCompletionChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<ChatChunkChoice> Choices,
    [property: JsonPropertyName("laplace"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChunkProvenance? Laplace = null);

public sealed record ChatChunkChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] ChatDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

public sealed record ChatDelta(
    [property: JsonPropertyName("role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Role = null,
    [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null);

// ---- text completions ----

public sealed record CompletionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<CompletionChoice> Choices,
    [property: JsonPropertyName("billing")] CompletionsReceipt? Billing);

public sealed record CompletionChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<CompletionChoice> Choices);

/// <summary><c>logprobs</c> is always emitted (null when not requested) — pinned wire behavior.</summary>
public sealed record CompletionChoice(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("finish_reason")] string? FinishReason,
    [property: JsonPropertyName("logprobs")] CompletionLogprobs? Logprobs);

public sealed record CompletionLogprobs(
    [property: JsonPropertyName("token_logprobs")] IReadOnlyList<double> TokenLogprobs);

// ---- billing receipts attached to execution responses ----

/// <summary>Receipt on report/visualization/explain responses (includes service_id).</summary>
public sealed record BillingReceipt(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("service_id")] string ServiceId);

/// <summary>Receipt on /v1/completions responses (historically without service_id — pinned).</summary>
public sealed record CompletionsReceipt(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("tenant")] string Tenant);

// ---- report wrappers ----

public sealed record AuditReportResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("academic")] bool Academic,
    [property: JsonPropertyName("include_evidence")] bool IncludeEvidence,
    [property: JsonPropertyName("include_consensus")] bool IncludeConsensus,
    [property: JsonPropertyName("include_convergence")] bool IncludeConvergence,
    [property: JsonPropertyName("report")] SubstrateAuditReport Report,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);

public sealed record VisualizationGraphResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("include_geometry")] bool IncludeGeometry,
    [property: JsonPropertyName("include_evidence")] bool IncludeEvidence,
    [property: JsonPropertyName("graph")] SubstrateVisualizationGraph Graph,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);

public sealed record ExplainReportResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("beam")] int Beam,
    [property: JsonPropertyName("academic")] bool Academic,
    [property: JsonPropertyName("trace")] IReadOnlyList<ExplainTraceStep> Trace,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);
