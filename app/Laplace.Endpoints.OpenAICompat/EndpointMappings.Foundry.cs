using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Native;

namespace Laplace.Endpoints.OpenAICompat;

internal static class FoundryEndpoints
{
    public static void MapFoundryEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/recipe/compile", async (
            HttpRequest request,
            IRecipeCompileService compile,
            IBillingOrchestrator billing,
            ITenantResolver tenants,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<RecipeCompileRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Recipe))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'recipe' is required.");

            var tenant = await tenants.ResolveAsync(request.HttpContext, ct);
            var auth = AuthorityScopePolicy.Resolve(request, tenant, (uint)LaplaceNative.LP_CAP_READ, requestWrite: false);
            if (!auth.Allowed)
                return Results.Json(new ErrorResponse(new ErrorBody("authorization_error", auth.ErrorCode!, auth.ErrorMessage!)),
                    statusCode: auth.StatusCode);

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "recipe.compile", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("recipe.compile")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            RecipeCompileResult compiled;
            try
            {
                compiled = compile.Compile(payload.Recipe);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
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
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            ITenantResolver tenants,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<SynthesisExportRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Recipe) && string.IsNullOrWhiteSpace(payload.RecipeIdPrefix))
                return EndpointJson.BadRequest(
                    "invalid_request_error",
                    "Provide 'recipe' JSON or 'recipe_id_prefix' with 'tokenizer_dir'.");

            var tenant = await tenants.ResolveAsync(request.HttpContext, ct);
            var auth = AuthorityScopePolicy.Resolve(
                request,
                tenant,
                (uint)(LaplaceNative.LP_CAP_READ | LaplaceNative.LP_CAP_EXPORT),
                requestWrite: false,
                explicitScope: payload.Scope,
                explicitWitnesses: payload.Witnesses);
            if (!auth.Allowed)
                return Results.Json(
                    new ErrorResponse(new ErrorBody("authorization_error", auth.ErrorCode!, auth.ErrorMessage!)),
                    statusCode: auth.StatusCode);

            var envRes = EnvelopeBudgetPolicy.Resolve(request, payload.Envelope);
            if (!envRes.Valid)
                return EndpointJson.BadRequest(envRes.ErrorCode!, envRes.ErrorMessage!);

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "synthesis", ct);
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
                    ct,
                    authority: auth.Scope,
                    modelName: payload.ModelName,
                    maxVocab: payload.MaxVocab ?? 0,
                    hiddenDim: payload.HiddenDim ?? 0,
                    numLayers: payload.NumLayers ?? 0,
                    numHeads: payload.NumHeads ?? 0,
                    f16Weights: payload.F16Weights ?? true);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return EndpointJson.ServiceUnavailable("foundry_export_failed", ex.Message);
            }

            IReadOnlyList<BillingRoyaltyRecord>? settledRoyalties = null;
            if (gate.Quote is not null)
            {
                var scopeIds = (auth.Scope.Witnesses ?? Array.Empty<Laplace.Engine.Core.Hash128>())
                    .Select(static w => w.ToHex()).ToArray();
                var attributions = await TryResolveExportAttributionAsync(substrate, scopeIds, ct);
                settledRoyalties = attributions.Count > 0
                    ? await billing.MarkConsumedAndRecordWithRoyaltiesAsync(gate.Quote, attributions, ct)
                    : await RecordWithoutRoyaltiesAsync(billing, gate.Quote, ct);
            }

            var receipt = gate.Quote is null
                ? null
                : QuoteGate.MakeReceipt(gate.Quote, settledRoyalties);

            return Results.Json(new SynthesisExportResponse(
                Id: $"export-{Guid.NewGuid():N}",
                Object: "laplace.synthesis.export",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Format: export.Format,
                OutputPath: export.OutputPath,
                Bytes: export.Bytes,
                Billing: receipt,
                NVocab: export.NVocab > 0 ? export.NVocab : null,
                NEdges: export.NEdges > 0 ? export.NEdges : null,
                HiddenDim: export.HiddenDim > 0 ? export.HiddenDim : null,
                RawWeightBytesRetained: export.RawWeightBytesRetained,
                Scheme: export.Scheme));
        })
        .WithTags("foundry")
        .Accepts<SynthesisExportRequest>("application/json")
        .Produces<SynthesisExportResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IReadOnlyList<RoyaltyAttributionShare>> TryResolveExportAttributionAsync(
        ISubstrateClient substrate, IReadOnlyList<string> scopeWitnesses, CancellationToken ct)
    {
        try
        {
            return await substrate.ResolveSourceAttributionAsync(Array.Empty<string>(), scopeWitnesses, ct);
        }
        catch (Exception)
        {
            return Array.Empty<RoyaltyAttributionShare>();
        }
    }

    private static async Task<IReadOnlyList<BillingRoyaltyRecord>> RecordWithoutRoyaltiesAsync(
        IBillingOrchestrator billing, BillingQuote quote, CancellationToken ct)
    {
        await billing.MarkConsumedAndRecordAsync(quote, ct);
        return Array.Empty<BillingRoyaltyRecord>();
    }
}
