using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

internal static class CoreEndpoints
{
    public static void MapCoreEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Json(new HealthResponse("ok", "F-scaffold")))
            .WithTags("core").Produces<HealthResponse>();

        app.MapGet("/v1/models", () => Results.Json(new ModelList("list",
        [
            new ModelInfo("laplace-converse-001", "model", 0, "laplace"),
            new ModelInfo("laplace-completions-001", "model", 0, "laplace"),
            new ModelInfo("laplace-code-001", "model", 0, "laplace"),
            new ModelInfo("laplace-embeddings-pending", "model", 0, "laplace", Status: "pending")
        ]))).WithTags("core").Produces<ModelList>();

        app.MapGet("/v1/capabilities", () => Results.Json(new CapabilitiesResponse("F-scaffold", new CapabilityEndpoints(
            ChatCompletions: new CapabilityStatus("live", Backend: "laplace.recall_session", Billing: "preflight_quote_required"),
            Completions: new CapabilityStatus("live", Backend: "laplace.completions", Billing: "preflight_quote_required"),
            Embeddings: new CapabilityStatus("pending", Reason: "requires Stream E physicality lookup path"),
            AuditReports: new CapabilityStatus("live", Backend: "laplace.substrate_counts + laplace.consensus_stats + laplace.top_relations", Billing: "audit.deep_report"),
            Visualizations: new CapabilityStatus("live", Backend: "laplace.top_relations + laplace.entity_physicalities", Billing: "visualization.deep_export"),
            ExplainabilityReports: new CapabilityStatus("live", Backend: "laplace.walk_branches + laplace.attestations_out", Billing: "explain.trace"),
            Billing: new CapabilityStatus("live", Provider: "stripe_or_manual"),
            Models: new CapabilityStatus("live")))))
            .WithTags("core").Produces<CapabilitiesResponse>();
    }
}
