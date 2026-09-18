using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

internal static class CodeEndpoints
{
    public static void MapCodeEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/code/completions", async (
            HttpRequest request,
            SubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<CodeCompletionRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (!string.Equals(payload.Model, ModelCatalog.Code, StringComparison.Ordinal))
                return EndpointJson.BadRequest("unknown_model",
                    $"Code completions require model '{ModelCatalog.Code}'.");
            if (string.IsNullOrWhiteSpace(payload.Prompt))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'prompt' is required.");
            if (string.IsNullOrWhiteSpace(payload.CodeLanguage)
                || !CodePlayerService.TryNormalizeModality(payload.CodeLanguage, out var modality))
                return EndpointJson.BadRequest("invalid_code_language",
                    "Field 'code_language' must name an installed Tree-sitter code modality.");

            int steps = payload.MaxTokens ?? 384;
            int window = payload.Window ?? 8;
            double temperature = payload.Temperature ?? 0.2;
            int topK = payload.TopK ?? 24;
            int attempts = payload.MaxAttempts ?? 4;
            if (steps is < 1 or > 4096 || window is < 1 or > 64 || topK is < 1 or > 4096
                || attempts is < 1 or > 8 || !double.IsFinite(temperature) || temperature < 0)
                return EndpointJson.BadRequest("invalid_request_error",
                    "max_tokens 1..4096, window 1..64, top_k 1..4096, max_attempts 1..8, and finite nonnegative temperature are required.");

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "chat.completions", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("chat.completions")
                    : (object)new QuotePendingDetail(
                        gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));
            if (gate.Quote is not null)
                await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            var result = await new CodePlayerService(substrate).GenerateAsync(
                payload.Prompt, modality, steps, window, temperature, topK, attempts, ct);
            var response = new CodeCompletionResponse(
                Id: $"codecmpl-{Guid.NewGuid():N}",
                Object: "code.completion",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model: ModelCatalog.Code,
                CodeLanguage: result.Modality,
                Code: result.Code,
                CandidateId: result.CandidateId,
                Verified: result.Verified,
                FailureKind: result.FailureKind,
                Attempts: result.Attempts.Select(a => new CodeAttemptReceipt(
                    a.Attempt, a.CandidateId, a.Verified, a.Tool, a.ToolAvailable,
                    a.TimedOut, a.ExitCode, a.Stdout, a.Stderr)).ToArray(),
                Billing: gate.Quote is null ? null : new BillingReceipt(
                    gate.Quote.QuoteId, gate.Quote.AmountCents, gate.Quote.Currency,
                    gate.Quote.Tenant, "chat.completions"));

            if (result.Verified)
                return Results.Json(response);

            int status = result.FailureKind is "toolchain_unavailable"
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status422UnprocessableEntity;
            return Results.Json(response, statusCode: status);
        })
        .WithTags("openai", "code")
        .Accepts<CodeCompletionRequest>("application/json")
        .Produces<CodeCompletionResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<CodeCompletionResponse>(StatusCodes.Status422UnprocessableEntity)
        .Produces<CodeCompletionResponse>(StatusCodes.Status503ServiceUnavailable);
    }
}
