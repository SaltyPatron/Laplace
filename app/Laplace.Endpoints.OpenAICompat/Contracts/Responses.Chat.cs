using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;



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


public sealed record ChatMetadata(
    [property: JsonPropertyName("witnesses"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Witnesses = null,
    [property: JsonPropertyName("reply_rows"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ReplyRows = null,
    [property: JsonPropertyName("generated_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? GeneratedTokens = null,
    [property: JsonPropertyName("session"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Session = null,
    [property: JsonPropertyName("laplace"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LaplaceChatMetadata? Laplace = null,
    [property: JsonPropertyName("performance"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatPerformance? Performance = null);

public sealed record ChatPerformance(
    [property: JsonPropertyName("substrate_ms")] double SubstrateMs,
    [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
    [property: JsonPropertyName("output_utf8_bytes")] int OutputUtf8Bytes,
    [property: JsonPropertyName("output_codepoints")] int OutputCodepoints,
    [property: JsonPropertyName("output_words")] int OutputWords,
    [property: JsonPropertyName("first_result_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? FirstResultMs = null,
    [property: JsonPropertyName("generated_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? GeneratedTokens = null,
    [property: JsonPropertyName("generated_tokens_per_second"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? GeneratedTokensPerSecond = null);






public sealed record LaplaceChatMetadata(
    [property: JsonPropertyName("provenance")] IReadOnlyList<ProvenanceLine> Provenance,
    [property: JsonPropertyName("forward_proof"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ForwardPassProof? ForwardProof = null);

public sealed record ProvenanceLine(
    [property: JsonPropertyName("reply")] string Reply,
    [property: JsonPropertyName("eff_mu"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? EffMu,
    [property: JsonPropertyName("witnesses"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Witnesses);


public sealed record OodaProofReceipt(
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("unverified")] bool Unverified,
    [property: JsonPropertyName("rounds_run")] int RoundsRun,
    [property: JsonPropertyName("culprit_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CulpritId,
    [property: JsonPropertyName("culprit_contrast")] double CulpritContrast);

public sealed record WorkReceipt(
    [property: JsonPropertyName("wall_us")] ulong WallUs,
    [property: JsonPropertyName("db_probes")] ulong DbProbes,
    [property: JsonPropertyName("rows_scanned")] ulong RowsScanned,
    [property: JsonPropertyName("bytes_alloc")] ulong BytesAlloc,
    [property: JsonPropertyName("hops")] uint Hops,
    [property: JsonPropertyName("tokens_out")] uint TokensOut,
    [property: JsonPropertyName("truncated")] uint Truncated,
    [property: JsonPropertyName("exhausted_reason")] int ExhaustedReason,
    [property: JsonPropertyName("estimated_us")] ulong EstimatedUs,
    [property: JsonPropertyName("preset")] string Preset);

public sealed record WitnessShareReceipt(
    [property: JsonPropertyName("witness_id")] string WitnessId,
    [property: JsonPropertyName("trust")] double Trust,
    [property: JsonPropertyName("fisher_info")] double FisherInfo,
    [property: JsonPropertyName("games")] ulong Games,
    [property: JsonPropertyName("share_bp")] int ShareBp);

/// <summary>
/// The receipt of the turn that produced the response: the Engine's own (<c>laplace_engine_abi</c>),
/// with its trace, OODA verification, salience, compute envelope work, and Fisher share_bp attribution.
/// </summary>
public sealed record ForwardPassProof(
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("participant")] string Participant,
    [property: JsonPropertyName("prompt_id")] string PromptId,
    [property: JsonPropertyName("response_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResponseId,
    [property: JsonPropertyName("turn_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TurnId,
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("turns_before")] int TurnsBefore,
    [property: JsonPropertyName("witnessed")] bool Witnessed,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    [property: JsonPropertyName("disposition")] string Disposition,
    [property: JsonPropertyName("steps")] IReadOnlyList<ForwardTurnStep> Steps,
    [property: JsonPropertyName("trace")] string Trace,
    [property: JsonPropertyName("firmware_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FirmwareId = null,
    [property: JsonPropertyName("program_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProgramId = null,
    [property: JsonPropertyName("decision_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DecisionId = null,
    [property: JsonPropertyName("top_salience"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? TopSalience = null,
    [property: JsonPropertyName("mean_confidence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? MeanConfidence = null,
    [property: JsonPropertyName("ooda"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OodaProofReceipt? Ooda = null,
    [property: JsonPropertyName("work"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkReceipt? Work = null,
    [property: JsonPropertyName("witness_shares"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WitnessShareReceipt>? WitnessShares = null);

/// <summary>One step of the turn: what it emitted, by which kind of proposal, and what it added to the response.</summary>
public sealed record ForwardTurnStep(
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("grounds")] double Grounds,
    [property: JsonPropertyName("continuity")] int Continuity,
    [property: JsonPropertyName("confidence")] double Confidence);


public sealed record ChunkProvenance(
    [property: JsonPropertyName("eff_mu"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? EffMu = null,
    [property: JsonPropertyName("witnesses"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Witnesses = null,
    [property: JsonPropertyName("step"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ForwardTurnStep? Step = null,
    [property: JsonPropertyName("performance"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatPerformance? Performance = null,
    [property: JsonPropertyName("forward_proof"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ForwardPassProof? ForwardProof = null);



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


public sealed record CompletionChoice(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("finish_reason")] string? FinishReason,
    [property: JsonPropertyName("logprobs")] CompletionLogprobs? Logprobs);

public sealed record CompletionLogprobs(
    [property: JsonPropertyName("token_logprobs")] IReadOnlyList<double> TokenLogprobs);




public sealed record BillingReceipt(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("royalties"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RoyaltyShareReceipt>? Royalties = null);

public sealed record RoyaltyShareReceipt(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source_owner_tenant"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceOwnerTenant,
    [property: JsonPropertyName("share_bp")] int ShareBp,
    [property: JsonPropertyName("royalty_cents")] long RoyaltyCents,
    [property: JsonPropertyName("hit_count")] long HitCount);


public sealed record CompletionsReceipt(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("tenant")] string Tenant);



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
    [property: JsonPropertyName("steps")] int Steps,
    [property: JsonPropertyName("max_stride")] int MaxStride,
    [property: JsonPropertyName("spread")] double Spread,
    [property: JsonPropertyName("top_k")] int TopK,
    [property: JsonPropertyName("trace")] IReadOnlyList<ForwardTraceStep> Trace,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);
