using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;




public sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stream")] string Stream);




public sealed record ReadinessResponse(
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("substrate_reachable")] bool SubstrateReachable,
    [property: JsonPropertyName("entities")] long Entities,
    [property: JsonPropertyName("consensus_relations")] long ConsensusRelations,
    [property: JsonPropertyName("perfcache_ready")] bool PerfcacheReady,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null);

public sealed record ModelList(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<ModelInfo> Data);

public sealed record ModelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy,
    [property: JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null);

public sealed record CapabilitiesResponse(
    [property: JsonPropertyName("stream")] string Stream,
    [property: JsonPropertyName("endpoints")] CapabilityEndpoints Endpoints);

public sealed record CapabilityEndpoints(
    [property: JsonPropertyName("chat_completions")] CapabilityStatus ChatCompletions,
    [property: JsonPropertyName("completions")] CapabilityStatus Completions,
    [property: JsonPropertyName("embeddings")] CapabilityStatus Embeddings,
    [property: JsonPropertyName("audit_reports")] CapabilityStatus AuditReports,
    [property: JsonPropertyName("visualizations")] CapabilityStatus Visualizations,
    [property: JsonPropertyName("explainability_reports")] CapabilityStatus ExplainabilityReports,
    [property: JsonPropertyName("billing")] CapabilityStatus Billing,
    [property: JsonPropertyName("models")] CapabilityStatus Models,
    [property: JsonPropertyName("feedback"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CapabilityStatus? Feedback = null);

public sealed record CapabilityStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("backend"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Backend = null,
    [property: JsonPropertyName("billing"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Billing = null,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonPropertyName("provider"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null);



public sealed record ErrorResponse(
    [property: JsonPropertyName("error")] ErrorBody Error);

public sealed record ErrorBody(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record NotImplementedResponse(
    [property: JsonPropertyName("error")] NotImplementedBody Error);

public sealed record NotImplementedBody(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("message")] string Message);

public sealed record PaymentRequiredResponse(
    [property: JsonPropertyName("error")] PaymentRequiredBody Error);


public sealed record PaymentRequiredBody(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("detail")] object? Detail);


public sealed record QuoteServiceDetail(
    [property: JsonPropertyName("service_id")] string ServiceId);


public sealed record QuotePendingDetail(string QuoteId, string Status, string? StripeCheckoutUrl);
