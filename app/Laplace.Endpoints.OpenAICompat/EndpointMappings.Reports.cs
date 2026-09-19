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

            var evidence = await substrate.EvidenceAsync(target.Trim(), Math.Max(0, limit ?? 10), ct);
            if (evidence is null)
                return EndpointJson.NotFound("entity_not_found", $"No entity for target '{target.Trim()}'.");

            return Results.Json(new EvidenceResponse(
                EntityId: evidence.EntityIdHex,
                EntityLabel: evidence.EntityLabel,
                Evidence: evidence.Items));
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
                    limit: Math.Max(0, payload.Limit ?? 100),
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

        app.MapPost("/v1/analyze/machine-cost", async (HttpRequest request, CancellationToken ct) =>
        {
            string cpu = request.Query["cpu"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(cpu))
                return EndpointJson.BadRequest("cpu_required", "Query parameter 'cpu' is required (for example cortex-a72, neoverse-n2, znver4).");

            string clockText = request.Query["clock_hz"].ToString().Trim();
            if (!double.TryParse(clockText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double clockHz)
                || !double.IsFinite(clockHz) || clockHz <= 0.0)
                return EndpointJson.BadRequest("clock_hz_invalid", "Query parameter 'clock_hz' must be a finite positive number.");

            int iterations = 1;
            string iterationsText = request.Query["iterations"].ToString().Trim();
            if (iterationsText.Length > 0
                && (!int.TryParse(iterationsText, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out iterations)
                    || iterations < 1 || iterations > MachineCostAnalyzer.MaxIterations))
                return EndpointJson.BadRequest("iterations_invalid",
                    $"Query parameter 'iterations' must be between 1 and {MachineCostAnalyzer.MaxIterations}.");

            if (request.ContentLength is 0)
                return EndpointJson.BadRequest("artifact_required", "Request body must contain an executable/object artifact.");
            if (request.ContentLength is > MachineCostAnalyzer.MaxArtifactBytes)
                return EndpointJson.BadRequest("artifact_too_large",
                    $"Artifact exceeds the {MachineCostAnalyzer.MaxArtifactBytes} byte analysis limit.");

            string? triple = request.Query["triple"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(triple)) triple = null;
            string artifactName = request.Query["filename"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(artifactName)) artifactName = "artifact.bin";

            try
            {
                MachineCostResponse result = await MachineCostAnalyzer.AnalyzeAsync(
                    request.Body, artifactName, cpu, clockHz, triple, iterations, ct);
                return Results.Json(result);
            }
            catch (MachineCostAnalysisException ex)
            {
                return ex.ServiceUnavailable
                    ? EndpointJson.ServiceUnavailable(ex.Code, ex.Message)
                    : EndpointJson.BadRequest(ex.Code, ex.Message);
            }
        })
        .WithTags("analysis")
        .Produces<MachineCostResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
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
            if (payload.Steps < 1 || payload.MaxStride < 0 || payload.TopK < 1 ||
                !double.IsFinite(payload.Spread) || payload.Spread < 0.0)
                return EndpointJson.BadRequest(
                    "invalid_request_error",
                    "Forward controls require steps >= 1, max_stride >= 0, top_k >= 1, and finite spread >= 0.");

            return await RunGatedReportAsync(request, billing, "explain.trace", ct, async gateQuote =>
            {
                // This is the same native forward execution consumed by normal
                // generation. depth/beam retain the public report spelling but
                // bind directly to semantic hops/fanout; no walk_branches replay.
                var trace = await substrate.ForwardTraceAsync(
                    payload.Prompt.Trim(),
                    steps: payload.Steps,
                    maxStride: payload.MaxStride,
                    spread: payload.Spread,
                    topK: payload.TopK,
                    hops: payload.Depth,
                    fanout: payload.Beam,
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
                    Steps: payload.Steps,
                    MaxStride: payload.MaxStride,
                    Spread: payload.Spread,
                    TopK: payload.TopK,
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

        return await produce(gate.Quote);
    }
}
