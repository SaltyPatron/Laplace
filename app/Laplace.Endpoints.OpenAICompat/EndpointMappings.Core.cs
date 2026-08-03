using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

internal static class CoreEndpoints
{
    public static void MapCoreEndpoints(this WebApplication app)
    {

        app.MapGet("/health", () => Results.Json(new HealthResponse("ok", "F-scaffold")))
            .WithTags("core").Produces<HealthResponse>();



        app.MapGet("/health/ready", async (ISubstrateClient substrate, CancellationToken ct) =>
        {
            var report = await substrate.ReadinessAsync(ct);
            return Results.Json(report, statusCode: report.Ready
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable);
        }).WithTags("core")
          .Produces<ReadinessResponse>()
          .Produces<ReadinessResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/models", () => Results.Json(new ModelList("list", ModelCatalog.All)))
            .WithTags("core").Produces<ModelList>();

        app.MapGet("/v1/pulse", async (ISubstrateClient substrate, CancellationToken ct) =>
        {
            var pulse = await substrate.PulseAsync(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
            return Results.Json(pulse);
        }).WithTags("core").Produces<PulseResponse>()
          .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/modalities", async (ISubstrateClient substrate, CancellationToken ct) =>
            Results.Json(await substrate.ModalitiesAsync(ct)))
          .WithTags("explore").Produces<ModalitiesResponse>()
          .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/capabilities", () =>
        {
            var endpoints = new CapabilityEndpoints(
                ChatCompletions: new CapabilityStatus("live", Backend: "laplace.recall_session", Billing: "preflight_quote_required"),
                Completions: new CapabilityStatus("live", Backend: "laplace.completions", Billing: "preflight_quote_required"),
                Embeddings: new CapabilityStatus("live", Backend: "laplace.entity_physicalities (form) + laplace.consensus_out_readable (meaning)", Billing: "embeddings"),
                AuditReports: new CapabilityStatus("live", Backend: "laplace.substrate_counts + laplace.consensus_stats + laplace.top_relations", Billing: "audit.deep_report"),
                Visualizations: new CapabilityStatus("live", Backend: "laplace.top_relations + laplace.entity_physicalities", Billing: "visualization.deep_export"),
                ExplainabilityReports: new CapabilityStatus("live", Backend: "laplace.walk_branches + laplace.attestations_out", Billing: "explain.trace"),
                Billing: new CapabilityStatus("live", Provider: "stripe_or_manual"),
                Models: new CapabilityStatus("live"),
                Feedback: new CapabilityStatus("live", Backend: "laplace.attestations (confirm/refute) + consensus fold"),
                RecipeCompile: new CapabilityStatus("live", Backend: "laplace.recipe validation + RecipeDescriptor", Billing: "recipe.compile"),
                SynthesisExport: new CapabilityStatus("live", Backend: "foundry CLI export (writes GGUF; never loaded on chat path)", Billing: "synthesis"),
                Op: new CapabilityStatus("live", Backend: "laplace.api catalog allow-list; named function call; no SQL text"));
            return Results.Json(new CapabilitiesResponse("F-scaffold", endpoints));
        })
        .WithTags("core").Produces<CapabilitiesResponse>();
    }
}
