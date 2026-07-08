using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

internal static class FoundryEndpoints
{
    public static void MapFoundryEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/recipe/compile", async (
            HttpRequest request,
            IRecipeCompileService compile,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<RecipeCompileRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Recipe))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'recipe' is required.");

            var quoteId = AppComposition.ResolveQuoteId(request) ?? "";
            var gate = await billing.EnsureExecutableAsync(quoteId, "recipe.compile", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("recipe.compile")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            RecipeCompileResult compiled;
            try
            {
                compiled = compile.Compile(payload.Recipe);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return EndpointJson.BadRequest("invalid_recipe", ex.Message);
            }

            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            return Results.Json(new RecipeCompileResponse(
                Id: $"recipe-{Guid.NewGuid():N}",
                Object: "laplace.recipe.compile",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                RecipeIdHex: compiled.RecipeIdHex,
                Name: compiled.Name,
                Structure: compiled.Structure,
                HiddenSize: compiled.HiddenSize,
                NumLayers: compiled.NumLayers,
                CompileMode: compiled.CompileMode,
                Billing: gate.Quote is null ? null : QuoteGate.MakeReceipt(gate.Quote)));
        })
        .WithTags("foundry")
        .Accepts<RecipeCompileRequest>("application/json")
        .Produces<RecipeCompileResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired);

        app.MapPost("/v1/synthesis/export", async (
            HttpRequest request,
            IFoundryExportService foundry,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<SynthesisExportRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Recipe) && string.IsNullOrWhiteSpace(payload.RecipeIdPrefix))
                return EndpointJson.BadRequest(
                    "invalid_request_error",
                    "Provide 'recipe' JSON or 'recipe_id_prefix' with 'tokenizer_dir'.");

            var quoteId = AppComposition.ResolveQuoteId(request) ?? "";
            var gate = await billing.EnsureExecutableAsync(quoteId, "synthesis", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("synthesis")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            var format = string.IsNullOrWhiteSpace(payload.Format) ? "gguf" : payload.Format.Trim();
            FoundryExportResult export;
            try
            {
                export = await foundry.ExportAsync(
                    payload.Recipe,
                    payload.RecipeIdPrefix,
                    payload.TokenizerDir,
                    format,
                    payload.Filename,
                    ct);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return EndpointJson.ServiceUnavailable("foundry_export_failed", ex.Message);
            }

            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            return Results.Json(new SynthesisExportResponse(
                Id: $"synth-{Guid.NewGuid():N}",
                Object: "laplace.synthesis.export",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Format: export.Format,
                OutputPath: export.OutputPath,
                Bytes: export.Bytes,
                Billing: gate.Quote is null ? null : QuoteGate.MakeReceipt(gate.Quote)));
        })
        .WithTags("foundry")
        .Accepts<SynthesisExportRequest>("application/json")
        .Produces<SynthesisExportResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }
}
