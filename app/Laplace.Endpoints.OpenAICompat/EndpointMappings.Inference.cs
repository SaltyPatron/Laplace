using System.Text;
using System.Text.Json;
using Laplace.Api.Contracts;
using Laplace.Decomposers.Abstractions;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal static class InferenceEndpoints
{
    public const string SessionHeader = "X-Laplace-Session";

    public static void MapOpenAiCompatEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/chat/completions", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, TurnWitness turnWitness, ITenantResolver tenantResolver, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ChatCompletionsRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Model))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'model' is required.");
            if (!ModelCatalog.IsChatModel(payload.Model))
                return EndpointJson.BadRequest("unknown_model",
                    $"Unknown model '{payload.Model}'. See GET /v1/models for the served catalog.");
            if (payload.Messages is null || payload.Messages.Count == 0)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'messages' must contain at least one message.");

            // Conversation state is substrate-resident: only the newest user turn is
            // consumed; any resent history is ignored by construction (spec 34).
            var prompt = payload.Messages
                .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => m.Content!.Trim())
                .LastOrDefault();
            if (string.IsNullOrWhiteSpace(prompt))
                return EndpointJson.BadRequest("invalid_request_error", "At least one message must include non-empty 'content'.");

            var (scope, scopeError) = await ResolveTurnScopeAsync(request, tenantResolver, payload.Session, payload.User, ct);
            if (scopeError is not null) return scopeError;

            bool tenantScoped = string.Equals(payload.Scope, "tenant", StringComparison.Ordinal);
            if (payload.Scope is not null && !tenantScoped)
                return EndpointJson.BadRequest("invalid_scope",
                    "Field 'scope' accepts only \"tenant\" (isolated read over this tenant's own witnessed world).");
            if (tenantScoped && !ModelCatalog.IsConverse(payload.Model))
                return EndpointJson.BadRequest("invalid_scope",
                    "Tenant-scoped reads are only available on the converse model lane.");

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "chat.completions", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("chat.completions")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            if (RequireTurnWitness(turnWitness) is { } chatWitnessErr) return chatWitnessErr;

            // The session key travels back on every response shape so the client can
            // continue the conversation without resending history.
            request.HttpContext.Response.Headers[SessionHeader] = scope.SessionKey;

            if (!ModelCatalog.IsConverse(payload.Model))
            {
                int genSteps = payload.MaxTokens ?? payload.MaxCompletionTokens ?? 128;
                double genTemp = payload.Temperature ?? 0.6;

                if (payload.Stream)
                {
                    var genId = $"chatcmpl-{Guid.NewGuid():N}";
                    var genCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var response = request.HttpContext.Response;
                    ServerSentEvents.Begin(response);
                    try
                    {
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
                        turnWitness.EnqueueTurn(scope.Tenant, scope.UserKey, scope.SessionId,
                            prompt, genStreamText.ToString().TrimStart());
                        await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                            genId, "chat.completion.chunk", genCreated, payload.Model,
                            [new ChatChunkChoice(0, new ChatDelta(Content: ""), "stop")]), ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await ServerSentEvents.WriteErrorAsync(response, "stream_failed", ex.Message, ct);
                    }
                    await ServerSentEvents.WriteDoneAsync(response, ct);
                    return Results.Empty;
                }

                var genTokens = new List<GenerateToken>(genSteps);
                await foreach (var token in substrate.WalkTextStreamAsync(
                    prompt, steps: genSteps, temperature: genTemp, ct: ct))
                    genTokens.Add(token);

                var genContent = string.Concat(genTokens.Select(t => t.Token)).TrimStart();
                turnWitness.EnqueueTurn(scope.Tenant, scope.UserKey, scope.SessionId, prompt, genContent);

                return Results.Json(new ChatCompletionResponse(
                    Id: $"chatcmpl-{Guid.NewGuid():N}",
                    Object: "chat.completion",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model: payload.Model,
                    Choices: [new ChatChoice(0, new ChatResponseMessage("assistant", genContent), "stop")],
                    Billing: null,
                    Metadata: new ChatMetadata(GeneratedTokens: genTokens.Count, Session: scope.SessionKey)));
            }

            // Default = act as a whole (global consensus). scope:"tenant" re-folds the
            // tenant's own witnessed world and reads inside it (spec 34 isolation).
            var tenantScope = ConversationContent.Resolve(scope.Tenant);
            var rows = tenantScoped
                ? await substrate.ConverseTenantScopedAsync(prompt, scope.SessionId.ToBytes(),
                    [tenantScope.PromptSource.ToBytes(), tenantScope.ResponseSource.ToBytes()], ct)
                : await substrate.ConverseAsync(prompt, scope.SessionId.ToBytes(), ct);
            // Empty consensus is reported truthfully: empty content + reply_rows 0.
            // The client renders the absence; the substrate never fakes prose.
            var content = string.Join("\n", rows.Select(r => r.Reply));

            turnWitness.EnqueueTurn(scope.Tenant, scope.UserKey, scope.SessionId,
                prompt, rows.Count > 0 ? content : null);

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
                    Session: scope.SessionKey,
                    Laplace: new LaplaceChatMetadata(
                        rows.Select(r => new ProvenanceLine(r.Reply, r.EffectiveMu, r.Witnesses)).ToArray()))));
        })
        .WithTags("openai")
        .Accepts<ChatCompletionsRequest>("application/json")
        .Produces<ChatCompletionResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/completions", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, TurnWitness turnWitness, ITenantResolver tenantResolver, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<CompletionsRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Model))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'model' is required.");
            if (!ModelCatalog.IsCompletionsModel(payload.Model))
                return EndpointJson.BadRequest("unknown_model",
                    $"Unknown model '{payload.Model}'. See GET /v1/models for the served catalog.");
            if (string.IsNullOrWhiteSpace(payload.Prompt))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'prompt' is required.");

            var (scope, scopeError) = await ResolveTurnScopeAsync(request, tenantResolver, payload.Session, payload.User, ct);
            if (scopeError is not null) return scopeError;

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "completions", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("completions")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            if (RequireTurnWitness(turnWitness) is { } witnessErr) return witnessErr;

            request.HttpContext.Response.Headers[SessionHeader] = scope.SessionKey;

            int steps = payload.MaxTokens ?? 64;
            double temp = payload.Temperature ?? 0.7;
            string[]? stop = payload.Stop is { } s ? ReadStopSequences(s) : null;

            if (payload.Stream)
            {
                var completionId = $"cmpl-{Guid.NewGuid():N}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var response = request.HttpContext.Response;
                ServerSentEvents.Begin(response);
                try
                {
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
                    turnWitness.EnqueueTurn(scope.Tenant, scope.UserKey, scope.SessionId,
                        payload.Prompt.Trim(), streamText.ToString().TrimStart());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await ServerSentEvents.WriteErrorAsync(response, "stream_failed", ex.Message, ct);
                }
                await ServerSentEvents.WriteDoneAsync(response, ct);
                return Results.Empty;
            }

            var tokens = new List<GenerateToken>(steps);
            await foreach (var token in substrate.WalkTextStreamAsync(
                payload.Prompt.Trim(), steps: steps, temperature: temp, ct: ct))
                tokens.Add(token);

            var text = string.Concat(tokens.Select(t => t.Token)).TrimStart();
            turnWitness.EnqueueTurn(scope.Tenant, scope.UserKey, scope.SessionId, payload.Prompt.Trim(), text);
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

        app.MapPost("/v1/embeddings", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<EmbeddingsRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Model))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'model' is required.");
            if (!ModelCatalog.TryEmbeddingModel(payload.Model, out bool includeMeaning))
                return EndpointJson.BadRequest("unknown_model",
                    $"Unknown model '{payload.Model}'. See GET /v1/models for the served catalog.");
            var inputs = ReadEmbeddingInputs(payload.Input);
            if (inputs.Count == 0)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'input' must be a non-empty string or array of strings.");

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "embeddings", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("embeddings")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));
            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            // Resolve the batch with bounded fan-out instead of one serial
            // round trip per input — an OpenAI-style batch array's latency
            // scaled linearly with its size. Order is preserved; the pooled
            // NpgsqlDataSource absorbs the concurrency.
            var results = new EmbeddingResult[inputs.Count];
            const int maxParallel = 8;
            for (int start = 0; start < inputs.Count; start += maxParallel)
            {
                int end = Math.Min(start + maxParallel, inputs.Count);
                var tasks = new Task<EmbeddingResult>[end - start];
                for (int i = start; i < end; i++)
                    tasks[i - start] = substrate.EmbeddingAsync(inputs[i], includeMeaning, meaningLimit: 10, ct);
                for (int i = start; i < end; i++)
                    results[i] = await tasks[i - start];
            }

            var data = new List<EmbeddingData>(inputs.Count);
            for (int i = 0; i < inputs.Count; i++)
            {
                var result = results[i];
                var vector = result.Form is { } f
                    ? new double[] { f.X, f.Y, f.Z, f.M, f.Radius }
                    : Array.Empty<double>();
                data.Add(new EmbeddingData("embedding", i, vector, new EmbeddingProvenance(
                    Input: inputs[i],


                    Resolved: result.Form is not null,
                    EntityId: result.EntityIdHex,
                    Form: result.Form is { } ff
                        ? new EmbeddingFormView(ff.X, ff.Y, ff.Z, ff.M, ff.Radius, ff.Constituents)
                        : null,
                    Meaning: includeMeaning && result.Meaning.Count > 0
                        ? result.Meaning.Select(m => new MeaningNeighborView(m.Relation, m.ObjectLabel, m.EffMu, m.Witnesses)).ToArray()
                        : null)));
            }

            var tokens = inputs.Sum(s => Math.Max(1, s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length));
            return Results.Json(new EmbeddingsResponse("list", data, payload.Model, new EmbeddingsUsage(tokens, tokens)));
        })
        .WithTags("openai")
        .Accepts<EmbeddingsRequest>("application/json")
        .Produces<EmbeddingsResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired);

        app.MapReportEndpoints();
        app.MapExploreEndpoints();
    }

    /// <summary>
    /// The turn's full provenance scope: tenant (resolved, validated), user-within-
    /// tenant (OpenAI-standard 'user' field), and the session — client-supplied KEY
    /// re-minted server-side into the canonical session id (never raw id bytes;
    /// tenant-in-the-key makes cross-tenant session forgery structurally impossible).
    /// Absent a client key the server mints a fresh one and returns it.
    /// </summary>
    internal readonly record struct TurnScope(
        string Tenant, string? UserKey, string SessionKey, Hash128 SessionId);

    private static async ValueTask<(TurnScope Scope, IResult? Error)> ResolveTurnScopeAsync(
        HttpRequest request, ITenantResolver tenantResolver,
        string? bodySessionKey, string? userKey, CancellationToken ct)
    {
        var tenant = (await tenantResolver.ResolveAsync(request.HttpContext, ct)).TenantId;
        if (!ConversationContent.IsValidIdentifier(tenant))
            return (default, EndpointJson.BadRequest("invalid_tenant",
                "Tenant id must match [A-Za-z0-9._@-]{1,128}."));

        var sessionKey = bodySessionKey;
        if (string.IsNullOrWhiteSpace(sessionKey))
            sessionKey = request.Headers[SessionHeader].ToString();
        if (string.IsNullOrWhiteSpace(sessionKey))
            sessionKey = $"s-{Guid.NewGuid():N}";
        if (!ConversationContent.IsValidIdentifier(sessionKey))
            return (default, EndpointJson.BadRequest("invalid_session",
                "Session key must match [A-Za-z0-9._@-]{1,128}."));

        if (userKey is not null && !ConversationContent.IsValidIdentifier(userKey))
            return (default, EndpointJson.BadRequest("invalid_user",
                "Field 'user' must match [A-Za-z0-9._@-]{1,128}."));

        return (new TurnScope(tenant, userKey, sessionKey,
            ConversationContent.SessionId(tenant, sessionKey)), null);
    }

    private static string[]? ReadStopSequences(JsonElement stop) =>
        stop.ValueKind switch
        {
            JsonValueKind.String => stop.GetString() is { Length: > 0 } s ? [s] : null,
            JsonValueKind.Array => stop.EnumerateArray()
                                        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                                        .Where(s => !string.IsNullOrEmpty(s))
                                        .Select(s => s!)
                                        .ToArray() is { Length: > 0 } arr ? arr : null,
            _ => null
        };


    private static List<string> ReadEmbeddingInputs(JsonElement? input)
    {
        var list = new List<string>();
        if (input is not { } element)
            return list;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (element.GetString() is { } s && !string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } v && !string.IsNullOrWhiteSpace(v))
                        list.Add(v.Trim());
                break;
        }
        return list;
    }

    private static IResult? RequireTurnWitness(TurnWitness turnWitness) =>
        turnWitness.IsAvailable
            ? null
            : EndpointJson.ServiceUnavailable(
                "witness_unavailable", "Turn witness is unavailable; prompt turns cannot be recorded.");
}
