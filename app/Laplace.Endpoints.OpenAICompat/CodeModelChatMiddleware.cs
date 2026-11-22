using System.Text.Json;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Native;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Chat-completions transport for verified code construction when <c>code_language</c> is supplied
/// with <c>laplace-code-001</c> or <c>coding</c>. Requests without <c>code_language</c> pass cleanly
/// through to <c>/v1/chat/completions</c> (<c>coding.firmware</c>).
/// </summary>
internal sealed class CodeModelChatMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method)
            || !context.Request.Path.Equals("/v1/chat/completions"))
        {
            await next(context);
            return;
        }

        context.Request.EnableBuffering();
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body,
                cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            if (context.Request.Body.CanSeek) context.Request.Body.Position = 0;
            await next(context);
            return;
        }

        using (document)
        {
            if (context.Request.Body.CanSeek) context.Request.Body.Position = 0;
            var root = document.RootElement;
            if (!root.TryGetProperty("model", out var modelElement)
                || modelElement.ValueKind != JsonValueKind.String
                || !ModelCatalog.IsCode(modelElement.GetString() ?? string.Empty)
                || !root.TryGetProperty("code_language", out var languageElement))
            {
                await next(context);
                return;
            }

            if (root.TryGetProperty("stream", out var streamElement)
                && streamElement.ValueKind is JsonValueKind.True)
            {
                await WriteAsync(context, EndpointJson.BadRequest("unsupported_parameter",
                    "Streaming is not supported on the verified toolchain code middleware."));
                return;
            }

            if (HasUnsupportedCodeChatControl(root, out var unsupported))
            {
                await WriteAsync(context, EndpointJson.BadRequest("unsupported_parameter",
                    $"Field '{unsupported}' is not implemented by the verified code lane."));
                return;
            }

            if (!root.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
            {
                await WriteAsync(context, EndpointJson.BadRequest("invalid_request_error",
                    "Field 'messages' must contain at least one non-empty user message."));
                return;
            }

            string? prompt = null;
            int userMessages = 0;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("role", out var roleElement)
                    || roleElement.ValueKind != JsonValueKind.String
                    || !message.TryGetProperty("content", out var contentElement)
                    || contentElement.ValueKind != JsonValueKind.String)
                {
                    await WriteAsync(context, EndpointJson.BadRequest("invalid_request_error",
                        "Every code-chat message must contain string 'role' and 'content' fields."));
                    return;
                }

                var role = roleElement.GetString();
                var content = contentElement.GetString();
                if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(context, EndpointJson.BadRequest("unsupported_role",
                        $"Role '{role}' is not supported when invoking code toolchain verification directly; pass plain messages without code_language for multi-turn coding.firmware chat."));
                    return;
                }
                if (string.IsNullOrWhiteSpace(content))
                    continue;
                userMessages++;
                prompt = content;
            }

            if (userMessages != 1 || string.IsNullOrWhiteSpace(prompt))
            {
                await WriteAsync(context, EndpointJson.BadRequest("invalid_request_error",
                    "Verified code lane requires one non-empty user message."));
                return;
            }

            if (languageElement.ValueKind != JsonValueKind.String
                || !CodePlayerService.TryNormalizeModality(languageElement.GetString() ?? string.Empty,
                    out var modality))
            {
                await WriteAsync(context, EndpointJson.BadRequest("invalid_code_language",
                    "Field 'code_language' must name an installed Tree-sitter code modality."));
                return;
            }

            int maxAttempts = IntOrDefault(root, "max_attempts", 4);
            if (maxAttempts is < 1 or > 8)
            {
                await WriteAsync(context, EndpointJson.BadRequest("invalid_request_error", "max_attempts 1..8 is required."));
                return;
            }

            var tenants = context.RequestServices.GetRequiredService<ITenantResolver>();
            var tenant = await tenants.ResolveAsync(context, context.RequestAborted);
            var scopeStr = root.TryGetProperty("scope", out var scEl) && scEl.ValueKind == JsonValueKind.String ? scEl.GetString() : null;
            var auth = AuthorityScopePolicy.Resolve(context.Request, tenant,
                (uint)(LaplaceNative.LP_CAP_READ | LaplaceNative.LP_CAP_ATTEST), requestWrite: true, explicitScope: scopeStr);
            if (!auth.Allowed)
            {
                await WriteAsync(context, Results.Json(
                    new ErrorResponse(new ErrorBody("authorization_error", auth.ErrorCode!, auth.ErrorMessage!)),
                    statusCode: auth.StatusCode));
                return;
            }

            var envName = root.TryGetProperty("envelope", out var envEl) && envEl.ValueKind == JsonValueKind.String ? envEl.GetString() : null;
            var maxTokens = root.TryGetProperty("max_tokens", out var mtEl) && mtEl.TryGetInt32(out var mtVal) ? mtVal : (int?)null;
            var envRes = EnvelopeBudgetPolicy.Resolve(context.Request, envName, openaiMaxTokens: maxTokens);
            if (!envRes.Valid)
            {
                await WriteAsync(context, EndpointJson.BadRequest(envRes.ErrorCode!, envRes.ErrorMessage!));
                return;
            }

            var billing = context.RequestServices.GetRequiredService<IBillingOrchestrator>();
            var gate = await QuoteGate.RequireQuoteAsync(context.Request, billing,
                "chat.completions", context.RequestAborted);
            if (!gate.Allowed)
            {
                await WriteAsync(context, EndpointJson.PaymentRequired(gate.Code, gate.Message,
                    gate.Quote is null
                        ? new QuoteServiceDetail("chat.completions")
                        : (object)new QuotePendingDetail(
                            gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl)));
                return;
            }
            if (gate.Quote is not null)
                await billing.MarkConsumedAndRecordAsync(gate.Quote, context.RequestAborted);

            var player = context.RequestServices.GetRequiredService<CodePlayerService>();
            var codePath = root.TryGetProperty("code_path", out var cpEl) && cpEl.ValueKind == JsonValueKind.String ? cpEl.GetString() : null;
            var codeText = root.TryGetProperty("code_text", out var ctEl) && ctEl.ValueKind == JsonValueKind.String ? ctEl.GetString() : null;
            var verifyCmd = root.TryGetProperty("verify_cmd", out var vcEl) && vcEl.ValueKind == JsonValueKind.String ? vcEl.GetString() : null;

            var result = await player.GenerateAsync(
                prompt, modality, maxAttempts, context.RequestAborted,
                authority: auth.Scope,
                envelope: envRes.Envelope,
                envelopePreset: envRes.PresetName,
                codePath: codePath,
                codeText: codeText,
                verifyCmd: verifyCmd,
                user: tenant.TenantId,
                session: $"code-{Guid.NewGuid():N}");

            if (!result.Verified)
            {
                var final = result.Attempts.LastOrDefault();
                var diagnostic = final is null || string.IsNullOrWhiteSpace(final.Stderr)
                    ? result.FailureKind ?? "code_verification_failed"
                    : final.Stderr;
                var error = new ErrorResponse(new ErrorBody(
                    "invalid_request_error",
                    result.FailureKind ?? "code_verification_failed",
                    diagnostic));
                int status = result.FailureKind is "toolchain_unavailable" or "engine_unavailable"
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status422UnprocessableEntity;
                await WriteAsync(context, Results.Json(error, statusCode: status));
                return;
            }

            IReadOnlyList<BillingRoyaltyRecord>? settledRoyalties = null;
            if (gate.Quote is not null && result.AbiResult?.Shares is { Count: > 0 } shares)
                settledRoyalties = await billing.RecordRoyaltiesAsync(gate.Quote, BillingOrchestrator.FromWitnessShares(shares), context.RequestAborted);

            var billingReceipt = gate.Quote is null ? null : QuoteGate.MakeReceipt(gate.Quote, settledRoyalties);
            var response = new ChatCompletionResponse(
                Id: $"chatcmpl-{Guid.NewGuid():N}",
                Object: "chat.completion",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model: modelElement.GetString()!,
                Choices: [new ChatChoice(0,
                    new ChatResponseMessage("assistant", result.Code), "stop")],
                Billing: billingReceipt,
                Metadata: new ChatMetadata(
                    ReplyRows: 1,
                    Laplace: new LaplaceChatMetadata(
                        [new ProvenanceLine(result.Code, null, null)],
                        ForwardProofHelper.FromAbi(result.AbiResult, $"code-{modality}", tenant.TenantId, envRes.PresetName, envRes.EstimatedUs))));
            await WriteAsync(context, Results.Json(response));
        }
    }

    private static bool HasUnsupportedCodeChatControl(JsonElement root, out string field)
    {
        foreach (var name in new[] { "top_p", "topic_boost", "web_search", "web_search_results", "shape", "bands", "elaborate" })
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False) continue;
            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) continue;
            field = name;
            return true;
        }
        field = string.Empty;
        return false;
    }

    private static int IntOrDefault(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed : fallback;

    private static Task WriteAsync(HttpContext context, IResult result)
        => result.ExecuteAsync(context);
}
