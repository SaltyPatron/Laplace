using System.Text;
using System.Text.Json;
using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

internal static class InferenceEndpoints
{
    public static void MapOpenAiCompatEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/chat/completions", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, TurnWitness turnWitness, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ChatCompletionsRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Model))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'model' is required.");
            if (payload.Messages is null || payload.Messages.Count == 0)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'messages' must contain at least one message.");

            var quoteId = AppComposition.ResolveQuoteId(request) ?? "";
            var gate = await billing.EnsureExecutableAsync(quoteId, "chat.completions", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("chat.completions")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            var prompt = payload.Messages
                .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => m.Content!.Trim())
                .LastOrDefault();
            if (string.IsNullOrWhiteSpace(prompt))
                return EndpointJson.BadRequest("invalid_request_error", "At least one message must include non-empty 'content'.");

            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            
            
            
            
            
            if (!payload.Model.Contains("converse", StringComparison.OrdinalIgnoreCase))
            {
                int genSteps = payload.MaxTokens ?? payload.MaxCompletionTokens ?? 128;
                double genTemp = payload.Temperature ?? 0.6;

                if (payload.Stream)
                {
                    var genId = $"chatcmpl-{Guid.NewGuid():N}";
                    var genCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var response = request.HttpContext.Response;
                    ServerSentEvents.Begin(response);

                    await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                        genId, "chat.completion.chunk", genCreated, payload.Model,
                        [new ChatChunkChoice(0, new ChatDelta(Role: "assistant"), null)]), ct);

                    var genStreamText = new StringBuilder();
                    await foreach (var token in substrate.WalkTextStreamAsync(
                        prompt, steps: genSteps, temperature: genTemp, ct: ct))
                    {
                        genStreamText.Append(token.Token);
                        await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                            genId, "chat.completion.chunk", genCreated, payload.Model,
                            [new ChatChunkChoice(0, new ChatDelta(Content: token.Token), null)],
                            Laplace: new ChunkProvenance(OrdUsed: (int)token.Mu)), ct);
                    }
                    turnWitness.Enqueue(prompt, "prompt");
                    turnWitness.Enqueue(genStreamText.ToString().TrimStart(), "reply");
                    await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                        genId, "chat.completion.chunk", genCreated, payload.Model,
                        [new ChatChunkChoice(0, new ChatDelta(Content: ""), "stop")]), ct);
                    await ServerSentEvents.WriteDoneAsync(response, ct);
                    return Results.Empty;
                }

                var genTokens = new List<GenerateToken>(genSteps);
                await foreach (var token in substrate.WalkTextStreamAsync(
                    prompt, steps: genSteps, temperature: genTemp, ct: ct))
                    genTokens.Add(token);

                var genContent = string.Concat(genTokens.Select(t => t.Token)).TrimStart();
                turnWitness.Enqueue(prompt, "prompt");
                turnWitness.Enqueue(genContent, "reply");

                return Results.Json(new ChatCompletionResponse(
                    Id: $"chatcmpl-{Guid.NewGuid():N}",
                    Object: "chat.completion",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model: payload.Model,
                    Choices: [new ChatChoice(0, new ChatResponseMessage("assistant", genContent), "stop")],
                    Billing: null,
                    Metadata: new ChatMetadata(GeneratedTokens: genTokens.Count)));
            }

            
            
            
            
            var sessionId = DeriveSessionId(payload.Messages);
            var userTurns = payload.Messages
                .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => m.Content!.Trim())
                .ToList();
            if (userTurns.Count == 0) userTurns.Add(prompt);
            var rows = await substrate.ConverseTurnsAsync(userTurns, sessionId, ct);
            var content = rows.Count == 0
                ? "I hold no consensus about that yet."
                : string.Join("\n", rows.Select(r => r.Reply));

            
            
            turnWitness.Enqueue(userTurns[^1], "prompt");
            if (rows.Count > 0) turnWitness.Enqueue(content, "reply");

            if (payload.Stream)
            {
                var completionId = $"chatcmpl-{Guid.NewGuid():N}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var response = request.HttpContext.Response;
                ServerSentEvents.Begin(response);

                await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                    completionId, "chat.completion.chunk", created, payload.Model,
                    [new ChatChunkChoice(0, new ChatDelta(Role: "assistant"), null)]), ct);

                
                for (int i = 0; i < rows.Count; i++)
                {
                    var line = rows[i].Reply + (i + 1 < rows.Count ? "\n" : "");
                    await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                        completionId, "chat.completion.chunk", created, payload.Model,
                        [new ChatChunkChoice(0, new ChatDelta(Content: line), null)],
                        Laplace: new ChunkProvenance(EffMu: rows[i].EffectiveMu, Witnesses: rows[i].Witnesses)), ct);
                }

                await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                    completionId, "chat.completion.chunk", created, payload.Model,
                    [new ChatChunkChoice(0, new ChatDelta(Content: ""), "stop")]), ct);
                await ServerSentEvents.WriteDoneAsync(response, ct);
                return Results.Empty;
            }

            return Results.Json(new ChatCompletionResponse(
                Id: $"chatcmpl-{Guid.NewGuid():N}",
                Object: "chat.completion",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model: payload.Model,
                Choices: [new ChatChoice(0, new ChatResponseMessage("assistant", content), "stop")],
                Billing: null,
                Metadata: new ChatMetadata(
                    Witnesses: rows.Sum(r => r.Witnesses),
                    ReplyRows: rows.Count,
                    Laplace: new LaplaceChatMetadata(
                        rows.Select(r => new ProvenanceLine(r.Reply, r.EffectiveMu, r.Witnesses)).ToArray()))));
        })
        .WithTags("openai")
        .Accepts<ChatCompletionsRequest>("application/json")
        .Produces<ChatCompletionResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired);

        app.MapPost("/v1/completions", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, TurnWitness turnWitness, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<CompletionsRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Model))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'model' is required.");
            if (string.IsNullOrWhiteSpace(payload.Prompt))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'prompt' is required.");

            var quoteId = AppComposition.ResolveQuoteId(request) ?? "";
            var gate = await billing.EnsureExecutableAsync(quoteId, "completions", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("completions")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            int steps = payload.MaxTokens ?? 64;
            double temp = payload.Temperature ?? 0.7;
            string[]? stop = payload.Stop is { } s ? ReadStopSequences(s) : null;

            if (payload.Stream)
            {
                var completionId = $"cmpl-{Guid.NewGuid():N}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var response = request.HttpContext.Response;
                ServerSentEvents.Begin(response);

                var streamText = new StringBuilder();
                await foreach (var token in substrate.WalkTextStreamAsync(
                    payload.Prompt.Trim(), steps: steps, temperature: temp, ct: ct))
                {
                    streamText.Append(token.Token);
                    await ServerSentEvents.WriteJsonAsync(response, new CompletionChunk(
                        completionId, "text_completion", created, payload.Model,
                        [new CompletionChoice(token.Token, 0, null,
                            payload.Logprobs.HasValue ? new CompletionLogprobs([(double)token.Mu]) : null)]), ct);
                }
                turnWitness.Enqueue(payload.Prompt.Trim(), "prompt");
                turnWitness.Enqueue(streamText.ToString().TrimStart(), "reply");
                await ServerSentEvents.WriteDoneAsync(response, ct);
                return Results.Empty;
            }

            
            var tokens = new List<GenerateToken>(steps);
            await foreach (var token in substrate.WalkTextStreamAsync(
                payload.Prompt.Trim(), steps: steps, temperature: temp, ct: ct))
                tokens.Add(token);

            var text = string.Concat(tokens.Select(t => t.Token)).TrimStart();
            turnWitness.Enqueue(payload.Prompt.Trim(), "prompt");
            turnWitness.Enqueue(text, "reply");
            return Results.Json(new CompletionResponse(
                Id: $"cmpl-{Guid.NewGuid():N}",
                Object: "text_completion",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model: payload.Model,
                Choices:
                [
                    new CompletionChoice(text, 0, "stop",
                        payload.Logprobs.HasValue
                            ? new CompletionLogprobs(tokens.Select(t => (double)t.Mu).ToArray())
                            : null)
                ],
                Billing: gate.Quote is null
                    ? null
                    : new CompletionsReceipt(gate.Quote.QuoteId, gate.Quote.AmountCents, gate.Quote.Currency, gate.Quote.Tenant)));
        })
        .WithTags("openai")
        .Accepts<CompletionsRequest>("application/json")
        .Produces<CompletionResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired);

        app.MapPost("/v1/embeddings", async (HttpRequest request, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<EmbeddingsRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Model))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'model' is required.");
            if (!EmbeddingsInputPresent(payload.Input))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'input' must be a non-empty string or array of strings.");
            return EndpointJson.NotImplemented("embeddings", "Pending Stream E physicality lookup path.");
        })
        .WithTags("openai")
        .Accepts<EmbeddingsRequest>("application/json")
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<NotImplementedResponse>(StatusCodes.Status501NotImplemented);

        app.MapReportEndpoints();
    }

    
    
    
    
    
    
    private static byte[]? DeriveSessionId(IReadOnlyList<ChatMessage>? messages)
    {
        var anchor = messages?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Content))?.Content;
        if (string.IsNullOrWhiteSpace(anchor)) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(anchor));
        return hash[..16];
    }

    private static string[]? ReadStopSequences(JsonElement stop) =>
        stop.ValueKind switch
        {
            JsonValueKind.String => stop.GetString() is { Length: > 0 } s ? [s] : null,
            JsonValueKind.Array  => stop.EnumerateArray()
                                        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                                        .Where(s => !string.IsNullOrEmpty(s))
                                        .Select(s => s!)
                                        .ToArray() is { Length: > 0 } arr ? arr : null,
            _                    => null
        };

    private static bool EmbeddingsInputPresent(JsonElement? input)
    {
        if (input is not { } element)
            return false;
        return element.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
            JsonValueKind.Array => element.GetArrayLength() > 0,
            _ => false
        };
    }
}
