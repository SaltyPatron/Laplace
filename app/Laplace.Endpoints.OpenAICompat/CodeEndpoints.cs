using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Native;

namespace Laplace.Endpoints.OpenAICompat;

internal static class CodeEndpoints
{
    public static void MapCodeEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/code/completions", async (
            HttpRequest request,
            CodePlayerService player,
            IBillingOrchestrator billing,
            ITenantResolver tenants,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<CodeCompletionRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Model) || !ModelCatalog.IsCode(payload.Model))
                return EndpointJson.BadRequest("unknown_model",
                    $"Code completions require model '{ModelCatalog.Code}' or 'coding'.");
            if (string.IsNullOrWhiteSpace(payload.Prompt))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'prompt' is required.");
            if (string.IsNullOrWhiteSpace(payload.CodeLanguage)
                || !CodePlayerService.TryNormalizeModality(payload.CodeLanguage, out var modality))
                return EndpointJson.BadRequest("invalid_code_language",
                    "Field 'code_language' must name an installed Tree-sitter code modality.");

            if (payload.Window is not null || payload.TopK is not null)
                return EndpointJson.BadRequest("unsupported_parameter",
                    "Fields 'window' and 'top_k' are not supported; use 'envelope' budgets and 'temperature'.");

            int attempts = payload.MaxAttempts ?? 4;
            if (attempts is < 1 or > 8)
                return EndpointJson.BadRequest("invalid_request_error", "max_attempts 1..8 is required.");

            var tenant = await tenants.ResolveAsync(request.HttpContext, ct);
            var auth = AuthorityScopePolicy.Resolve(
                request,
                tenant,
                (uint)(LaplaceNative.LP_CAP_READ | LaplaceNative.LP_CAP_ATTEST),
                requestWrite: true,
                explicitScope: payload.Scope,
                explicitWitnesses: payload.Witnesses);
            if (!auth.Allowed)
                return Results.Json(
                    new ErrorResponse(new ErrorBody("authorization_error", auth.ErrorCode!, auth.ErrorMessage!)),
                    statusCode: auth.StatusCode);

            var envRes = EnvelopeBudgetPolicy.Resolve(request, payload.Envelope, openaiMaxTokens: payload.MaxTokens);
            if (!envRes.Valid)
                return EndpointJson.BadRequest(envRes.ErrorCode!, envRes.ErrorMessage!);

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "chat.completions", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("chat.completions")
                    : (object)new QuotePendingDetail(
                        gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));
            if (gate.Quote is not null)
                await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            var result = await player.GenerateAsync(
                payload.Prompt,
                modality,
                attempts,
                ct,
                authority: auth.Scope,
                envelope: envRes.Envelope,
                envelopePreset: envRes.PresetName,
                codePath: payload.CodePath,
                codeText: payload.CodeText,
                verifyCmd: payload.VerifyCmd,
                user: tenant.TenantId,
                session: $"code-{Guid.NewGuid():N}");

            IReadOnlyList<BillingRoyaltyRecord>? settledRoyalties = null;
            if (gate.Quote is not null && result.AbiResult?.Shares is { Count: > 0 } shares)
                settledRoyalties = await billing.RecordRoyaltiesAsync(gate.Quote, BillingOrchestrator.FromWitnessShares(shares), ct);

            var abi = result.AbiResult;
            var response = new CodeCompletionResponse(
                Id: $"codecmpl-{Guid.NewGuid():N}",
                Object: "code.completion",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model: payload.Model,
                CodeLanguage: result.Modality,
                Code: result.Code,
                CandidateId: result.CandidateId,
                Verified: result.Verified,
                FailureKind: result.FailureKind,
                Attempts: result.Attempts.Select(a => new CodeAttemptReceipt(
                    a.Attempt, a.CandidateId, a.Verified, a.Tool, a.ToolAvailable,
                    a.TimedOut, a.ExitCode, a.Stdout, a.Stderr)).ToArray(),
                Billing: gate.Quote is null ? null : QuoteGate.MakeReceipt(gate.Quote, settledRoyalties),
                Ooda: abi is null ? null : new OodaProofReceipt(
                    Verified: abi.OodaVerified,
                    Unverified: abi.OodaUnverified,
                    RoundsRun: abi.OodaRoundsRun,
                    CulpritId: abi.CulpritId == Laplace.Engine.Core.Hash128.Zero ? null : abi.CulpritId.ToHex(),
                    CulpritContrast: abi.CulpritContrast),
                Work: abi is null ? null : ForwardProofHelper.WorkFromAbi(abi.Work, envRes.PresetName, envRes.EstimatedUs),
                WitnessShares: abi is null || abi.Shares.Count == 0
                    ? null
                    : abi.Shares.Select(ForwardProofHelper.ShareFromAbi).ToArray());

            if (result.Verified)
                return Results.Json(response);

            int status = result.FailureKind is "toolchain_unavailable" or "engine_unavailable"
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
