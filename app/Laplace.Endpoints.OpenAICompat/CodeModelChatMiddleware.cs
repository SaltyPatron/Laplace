using System.Text.Json;
using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Exact-model adapter for the OpenAI chat surface. A laplace-code-001 request that
/// explicitly opts into a governed code modality owns the same CodePlayerService as
/// /v1/code/completions. Without code_language it falls through to the ordinary model
/// catalog, so an unproven/underspecified code label is never silently promoted.
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
                    "Streaming is not yet implemented for laplace-code-001; the code lane will not silently fall through to prose streaming."));
                return;
            }

            if (HasUnsupportedCodeChatControl(root, out var unsupported))
            {
                await WriteAsync(context, EndpointJson.BadRequest("unsupported_parameter",
                    $"Field '{unsupported}' is not implemented by laplace-code-001 and will not be ignored."));
                return;
            }

            if (!root.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
            {
                await WriteAsync(context, EndpointJson.BadRequest("invalid_request_error",
                    "Field 'messages' must contain exactly one non-empty user message for laplace-code-001."));
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
                        $"Role '{role}' is not yet implemented by laplace-code-001; role history is never silently discarded."));
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
                    "laplace-code-001 currently requires exactly one non-empty user message; multi-message history is rejected rather than ignored."));
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

            int steps = IntOrDefault(root, "max_completion_tokens",
                IntOrDefault(root, "max_tokens", 384));
            int window = IntOrDefault(root, "window", 8);
            int topK = IntOrDefault(root, "top_k", 24);
            int maxAttempts = IntOrDefault(root, "max_attempts", 4);
            double temperature = DoubleOrDefault(root, "temperature", 0.2);
            if (steps is < 1 or > 4096 || window is < 1 or > 64 || topK is < 1 or > 4096
                || maxAttempts is < 1 or > 8 || !double.IsFinite(temperature) || temperature < 0)
            {
                await WriteAsync(context, EndpointJson.BadRequest("invalid_request_error",
                    "max_tokens 1..4096, window 1..64, top_k 1..4096, max_attempts 1..8, and finite nonnegative temperature are required."));
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

            var substrate = context.RequestServices.GetRequiredService<SubstrateClient>();
            var result = await new CodePlayerService(substrate).GenerateAsync(
                prompt, modality, steps, window, temperature, topK, maxAttempts,
                context.RequestAborted);

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
                int status = result.FailureKind is "toolchain_unavailable" or "code_evidence_unavailable"
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status422UnprocessableEntity;
                await WriteAsync(context, Results.Json(error, statusCode: status));
                return;
            }

            var billingReceipt = gate.Quote is null ? null : new BillingReceipt(
                gate.Quote.QuoteId, gate.Quote.AmountCents, gate.Quote.Currency,
                gate.Quote.Tenant, "chat.completions");
            var response = new ChatCompletionResponse(
                Id: $"chatcmpl-{Guid.NewGuid():N}",
                Object: "chat.completion",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model: ModelCatalog.Code,
                Choices: [new ChatChoice(0,
                    new ChatResponseMessage("assistant", result.Code), "stop")],
                Billing: billingReceipt,
                Metadata: new ChatMetadata(
                    ReplyRows: 1,
                    Laplace: new LaplaceChatMetadata(
                        [new ProvenanceLine(result.Code, null, null)])));
            await WriteAsync(context, Results.Json(response));
        }
    }

    private static bool HasUnsupportedCodeChatControl(JsonElement root, out string field)
    {
        foreach (var name in new[]
        {
            "top_p", "topic_boost", "stop", "web_search", "web_search_results",
            "shape", "bands", "elaborate", "language", "scope", "session"
        })
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            if (value.ValueKind == JsonValueKind.False) continue;
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

    private static double DoubleOrDefault(JsonElement root, string name, double fallback)
        => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed)
            ? parsed : fallback;

    private static Task WriteAsync(HttpContext context, IResult result)
        => result.ExecuteAsync(context);
}
