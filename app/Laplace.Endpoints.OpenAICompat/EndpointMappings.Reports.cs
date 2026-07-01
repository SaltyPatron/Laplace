using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

internal static class ReportEndpoints
{
    public static void MapReportEndpoints(this WebApplication app)
    {

        app.MapGet("/v1/evidence/{target}", async (string target, int? limit, ISubstrateClient substrate, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(target))
                return EndpointJson.BadRequest("invalid_request_error", "Route parameter 'target' is required.");

            try
            {
                var evidence = await substrate.EvidenceAsync(target.Trim(), Math.Clamp(limit ?? 10, 1, 50), ct);
                if (evidence is null)
                    return EndpointJson.NotFound("entity_not_found", $"No entity for target '{target.Trim()}'.");

                return Results.Json(new EvidenceResponse(
                    EntityId: evidence.EntityIdHex,
                    EntityLabel: evidence.EntityLabel,
                    Evidence: evidence.Items));
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("openai")
        .Produces<EvidenceResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/audit/report", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<AuditReportRequest>(request, ct) ?? new AuditReportRequest();
            return await RunGatedReportAsync(request, billing, "audit.deep_report", ct, async gateQuote =>
            {
                var report = await substrate.AuditReportAsync(
                    includeConsensus: payload.IncludeConsensus,
                    includeConvergence: payload.IncludeConvergence,
                    topRelationLimit: payload.Academic ? 50 : 20,
                    ct);
                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new AuditReportResponse(
                    Id: $"audit-{Guid.NewGuid():N}",
                    Object: "laplace.audit.report",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Scope: string.IsNullOrWhiteSpace(payload.Scope) ? "summary" : payload.Scope.Trim(),
                    Academic: payload.Academic,
                    IncludeEvidence: payload.IncludeEvidence,
                    IncludeConsensus: payload.IncludeConsensus,
                    IncludeConvergence: payload.IncludeConvergence,
                    Report: report,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("reports")
        .Accepts<AuditReportRequest>("application/json")
        .Produces<AuditReportResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/visualizations/substrate", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<VisualizationExecuteRequest>(request, ct) ?? new VisualizationExecuteRequest();
            return await RunGatedReportAsync(request, billing, "visualization.deep_export", ct, async gateQuote =>
            {
                var graph = await substrate.VisualizationGraphAsync(
                    limit: Math.Clamp(payload.Limit ?? 100, 1, 500),
                    includeGeometry: payload.IncludeGeometry,
                    includeEvidence: payload.IncludeEvidence,
                    ct);
                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new VisualizationGraphResponse(
                    Id: $"viz-{Guid.NewGuid():N}",
                    Object: "laplace.visualization.graph",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Format: string.IsNullOrWhiteSpace(payload.Format) ? "json" : payload.Format.Trim(),
                    IncludeGeometry: payload.IncludeGeometry,
                    IncludeEvidence: payload.IncludeEvidence,
                    Graph: graph,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("reports")
        .Accepts<VisualizationExecuteRequest>("application/json")
        .Produces<VisualizationGraphResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/explain/report", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ExplainReportRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Prompt))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'prompt' is required.");
            if (payload.Depth < 1 || payload.Beam < 1)
                return EndpointJson.BadRequest("invalid_request_error", "Fields 'depth' and 'beam' must each be >= 1.");

            return await RunGatedReportAsync(request, billing, "explain.trace", ct, async gateQuote =>
            {
                var trace = await substrate.ExplainTraceAsync(
                    payload.Prompt.Trim(),
                    payload.Depth,
                    payload.Beam,
                    includeEvidence: payload.Academic,
                    ct);
                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExplainReportResponse(
                    Id: $"explain-{Guid.NewGuid():N}",
                    Object: "laplace.explainability.report",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Prompt: payload.Prompt.Trim(),
                    Depth: payload.Depth,
                    Beam: payload.Beam,
                    Academic: payload.Academic,
                    Trace: trace,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("reports")
        .Accepts<ExplainReportRequest>("application/json")
        .Produces<ExplainReportResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }







    private static async Task<IResult> RunGatedReportAsync(
        HttpRequest request,
        IBillingOrchestrator billing,
        string serviceId,
        CancellationToken ct,
        Func<BillingQuote?, Task<IResult>> produce)
    {
        var gate = await QuoteGate.RequireQuoteAsync(request, billing, serviceId, ct);
        if (!gate.Allowed)
            return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null ? null : new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

        try
        {
            return await produce(gate.Quote);
        }
        catch (SubstrateUnavailableException ex)
        {
            return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
        }
    }
}
