using System.Text.Json;
using System.Text.Json.Serialization;

namespace Laplace.Endpoints.OpenAICompat;

// ── chat completions ──────────────────────────────────────────────────────
internal sealed record ChatCompletionsRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage>? Messages,
    [property: JsonPropertyName("stream")] bool Stream = false,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null,
    [property: JsonPropertyName("max_completion_tokens")] int? MaxCompletionTokens = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("top_p")] double? TopP = null,
    // Laplace-native decode knobs (OpenAI-superset; ignored by stock clients).
    [property: JsonPropertyName("top_k")] int? TopK = null,
    [property: JsonPropertyName("window")] int? Window = null,
    [property: JsonPropertyName("topic_boost")] double? TopicBoost = null,
    [property: JsonPropertyName("stop")] JsonElement? Stop = null,
    // Server-side retrieval augmentation (ephemeral; does NOT mutate the substrate).
    [property: JsonPropertyName("web_search")] bool WebSearch = false,
    [property: JsonPropertyName("web_search_results")] int? WebSearchResults = null);

internal sealed record ChatMessage(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content);

// ── text completions ──────────────────────────────────────────────────────
internal sealed record CompletionsRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("stream")] bool Stream = false,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("top_p")] double? TopP = null,
    [property: JsonPropertyName("top_k")] int? TopK = null,
    [property: JsonPropertyName("window")] int? Window = null,
    [property: JsonPropertyName("topic_boost")] double? TopicBoost = null,
    [property: JsonPropertyName("stop")] JsonElement? Stop = null,
    [property: JsonPropertyName("echo")] bool Echo = false,
    [property: JsonPropertyName("logprobs")] int? Logprobs = null);

// ── embeddings ────────────────────────────────────────────────────────────
internal sealed record EmbeddingsRequest(
    [property: JsonPropertyName("model")] string? Model,
    // OpenAI allows string OR array of strings. We accept both; token-id arrays
    // are rejected with a clear error (the substrate addresses content, not BPE ids).
    [property: JsonPropertyName("input")] JsonElement? Input);

// ── web search ────────────────────────────────────────────────────────────
internal sealed record SearchRequest(
    [property: JsonPropertyName("query")] string? Query,
    [property: JsonPropertyName("count")] int? Count);

// ── billing ───────────────────────────────────────────────────────────────
internal sealed record BillingPreflightRequest(
    [property: JsonPropertyName("service_id")] string? ServiceId,
    [property: JsonPropertyName("units")] int Units,
    [property: JsonPropertyName("tenant")] string? Tenant);

internal sealed record PlanSubscribeRequest(
    [property: JsonPropertyName("tenant")] string? Tenant);

internal sealed record CreditConsumeRequest(
    [property: JsonPropertyName("tenant")] string? Tenant,
    [property: JsonPropertyName("service_id")] string? ServiceId,
    [property: JsonPropertyName("units")] int Units = 1);

// Build-a-model synthesis quote: the recipe dimensions drive a metered price
// (base job fee + per-million-parameter rate). Mirrors the recipe.json shape
// consumed by `laplace synthesize substrate`.
internal sealed record SynthesisQuoteRequest(
    [property: JsonPropertyName("tenant")] string? Tenant,
    [property: JsonPropertyName("vocab_size")] long VocabSize,
    [property: JsonPropertyName("hidden_size")] long HiddenSize,
    [property: JsonPropertyName("num_layers")] int NumLayers,
    [property: JsonPropertyName("num_heads")] int NumHeads,
    [property: JsonPropertyName("num_kv_heads")] int? NumKvHeads = null,
    [property: JsonPropertyName("intermediate_size")] long IntermediateSize = 0,
    [property: JsonPropertyName("tied_embeddings")] bool TiedEmbeddings = false,
    [property: JsonPropertyName("format")] string? Format = null);

// Step-by-step explainability report quote: depth/beam drive the trace size
// (and metered price); the academic tier adds evidence-provenance / citation
// expansion per node. Mirrors `laplace.generate_tree(p_prompt, p_kind, depth, beam)`.
internal sealed record ExplainQuoteRequest(
    [property: JsonPropertyName("tenant")] string? Tenant,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("beam")] int Beam,
    [property: JsonPropertyName("academic")] bool Academic = false);

// Premium audit/report quote. The paid surface maps to the ops/read SRFs such
// as substrate_counts(), consensus_stats(), evidence_count(), and convergence
// fan-in reports.
internal sealed record AuditQuoteRequest(
    [property: JsonPropertyName("tenant")] string? Tenant,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("include_evidence")] bool IncludeEvidence = true,
    [property: JsonPropertyName("include_consensus")] bool IncludeConsensus = true,
    [property: JsonPropertyName("include_convergence")] bool IncludeConvergence = true,
    [property: JsonPropertyName("academic")] bool Academic = false);

// Visualization/export quote for graph/geometry renders of substrate neighborhoods.
internal sealed record VisualizationQuoteRequest(
    [property: JsonPropertyName("tenant")] string? Tenant,
    [property: JsonPropertyName("nodes")] int Nodes,
    [property: JsonPropertyName("edges")] int Edges = 0,
    [property: JsonPropertyName("include_geometry")] bool IncludeGeometry = true,
    [property: JsonPropertyName("include_evidence")] bool IncludeEvidence = false,
    [property: JsonPropertyName("interactive")] bool Interactive = false,
    [property: JsonPropertyName("format")] string? Format = null);

// Recipe marketplace/build workflow quote: publish/access are flat, compile and
// private export scale with selected content volume and commercial use.
internal sealed record RecipeQuoteRequest(
    [property: JsonPropertyName("tenant")] string? Tenant,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("content_items")] int ContentItems = 1,
    [property: JsonPropertyName("commercial")] bool Commercial = false,
    [property: JsonPropertyName("private_export")] bool PrivateExport = false);

// Paid execution requests for the report/visualization SKUs above.
internal sealed record AuditReportRequest(
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("include_evidence")] bool IncludeEvidence = true,
    [property: JsonPropertyName("include_consensus")] bool IncludeConsensus = true,
    [property: JsonPropertyName("include_convergence")] bool IncludeConvergence = true,
    [property: JsonPropertyName("academic")] bool Academic = false);

internal sealed record VisualizationExecuteRequest(
    [property: JsonPropertyName("limit")] int? Limit = null,
    [property: JsonPropertyName("include_geometry")] bool IncludeGeometry = true,
    [property: JsonPropertyName("include_evidence")] bool IncludeEvidence = false,
    [property: JsonPropertyName("format")] string? Format = null);

internal sealed record ExplainReportRequest(
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("beam")] int Beam,
    [property: JsonPropertyName("academic")] bool Academic = false);

// ── substrate row projections ─────────────────────────────────────────────
internal sealed record ConverseRow(string Reply, decimal EffectiveMu, long Witnesses);

internal sealed record CompletionRow(
    string ObjectIdHex,
    string TypeIdHex,
    decimal EffectiveMu,
    long Witnesses,
    string ObjectLabel);

internal sealed record GenerateToken(int Step, string Token, decimal Mu);

internal sealed record EmbeddingVector(bool Resolved, IReadOnlyList<double> Values);

internal sealed record StructuralNeighbor(string Neighbor, double Geodesic, double? Frechet);

internal sealed record SubstrateCount(string Metric, long Value);

internal sealed record ConsensusHealth(long EvidenceRows, long ConsensusRows, decimal? DedupRatio, decimal? AvgWitnesses, long? MaxWitnesses);

internal sealed record SubstrateAuditReport(
    IReadOnlyList<SubstrateCount> Counts,
    ConsensusHealth? Consensus,
    long? MultiSourceEntityCount,
    IReadOnlyList<VisualizationEdge> TopRelations);

internal sealed record VisualizationNode(
    string IdHex,
    string Label,
    double? X,
    double? Y,
    double? Z,
    double? M,
    double? Radius,
    int? Constituents,
    long? EvidenceRows);

internal sealed record VisualizationEdge(
    string SubjectIdHex,
    string Subject,
    string TypeIdHex,
    string Type,
    string ObjectIdHex,
    string Object,
    decimal EffectiveMu,
    long Witnesses);

internal sealed record SubstrateVisualizationGraph(IReadOnlyList<VisualizationNode> Nodes, IReadOnlyList<VisualizationEdge> Edges);

internal sealed record EvidenceSample(
    string TypeIdHex,
    string ObjectIdHex,
    string SourceIdHex,
    string? ContextIdHex,
    short Outcome,
    long ObservationCount);

internal sealed record ExplainTraceStep(
    int Depth,
    IReadOnlyList<string> PathHex,
    IReadOnlyList<string> KindPathHex,
    string EntityIdHex,
    string EntityLabel,
    decimal EffectiveMu,
    decimal PathMu,
    long Witnesses,
    IReadOnlyList<EvidenceSample> Evidence);
