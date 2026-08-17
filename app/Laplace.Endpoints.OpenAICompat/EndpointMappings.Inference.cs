using System.Diagnostics;
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
            var totalClock = Stopwatch.StartNew();
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
                .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => m.Content!.Trim())
                .LastOrDefault();
            if (string.IsNullOrWhiteSpace(prompt))
                return EndpointJson.BadRequest("invalid_request_error", "At least one user message must include non-empty 'content'.");

            bool converseModel = ModelCatalog.IsConverse(payload.Model);
            bool hasBands = payload.Bands is { Length: > 0 };
            if (payload.WebSearch || payload.WebSearchResults is not null)
                return EndpointJson.BadRequest("unsupported_parameter",
                    "Web search is not implemented on this endpoint; no request will silently ignore it.");
            if (converseModel &&
                (payload.MaxTokens is not null || payload.MaxCompletionTokens is not null
                 || payload.Temperature is not null || payload.TopP is not null
                 || payload.TopK is not null || payload.Window is not null
                 || payload.TopicBoost is not null || payload.Stop is not null))
                return EndpointJson.BadRequest("unsupported_parameter",
                    "Generation controls do not apply to the converse read lane. Use shape/bands/elaborate, or select the completions model.");
            if (!converseModel &&
                ((payload.TopP is { } topP && topP != 1.0)
                 || payload.TopicBoost is not null || payload.Stop is not null))
                return EndpointJson.BadRequest("unsupported_parameter",
                    "The walk lane does not implement top_p, topic_boost, or stop; the endpoint rejects them instead of ignoring them.");
            if (!converseModel &&
                (!string.IsNullOrWhiteSpace(payload.Shape) || hasBands || payload.Elaborate))
                return EndpointJson.BadRequest("invalid_request_error",
                    "Fields 'shape', 'bands', and 'elaborate' are available only on the converse model lane.");
            if (!converseModel && !string.IsNullOrWhiteSpace(payload.Language))
                return EndpointJson.BadRequest("unsupported_parameter",
                    "Field 'language' is not implemented by the completions walk lane.");
            if (payload.Bands is { } suppliedBands &&
                (suppliedBands.Length == 0 || suppliedBands.Any(b => b is < 1 or > 13)))
                return EndpointJson.BadRequest("invalid_request_error",
                    "Field 'bands' must contain one or more salience-band numbers in the range 1..13.");
            if (payload.Elaborate && (!string.IsNullOrWhiteSpace(payload.Shape) || hasBands))
                return EndpointJson.BadRequest("invalid_request_error",
                    "Field 'elaborate' cannot be combined with 'shape' or 'bands'; it advances the session's fact layers.");
            if (hasBands && !string.IsNullOrWhiteSpace(payload.Shape))
                return EndpointJson.BadRequest("invalid_request_error",
                    "Fields 'shape' and 'bands' select different read paths and cannot be combined.");
            var (scope, scopeError) = await ResolveTurnScopeAsync(request, tenantResolver, payload.Session, payload.User, ct);
            if (scopeError is not null) return scopeError;

            if (!OperatorLanguage.TryResolve(request, payload.Language,
                    out var operatorLanguage, out var invalidLanguage))
                return EndpointJson.BadRequest("invalid_language",
                    $"Field 'language' does not resolve to an ISO 639 language: '{invalidLanguage}'.");

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

            // Installed shape validation is a substrate read. Keep it behind the
            // quote gate so an unquoted request cannot spend database work.
            if (converseModel && !string.IsNullOrWhiteSpace(payload.Shape))
            {
                var shapes = await substrate.QueryShapesAsync(ct);
                if (!shapes.Any(s => string.Equals(s.Shape, payload.Shape, StringComparison.Ordinal)))
                    return EndpointJson.BadRequest("invalid_shape",
                        $"Unknown converse shape '{payload.Shape}'. See GET /v1/query/shapes for the installed catalog.");
            }

            if (RequireTurnWitness(turnWitness) is { } chatWitnessErr) return chatWitnessErr;

            // The session key travels back on every response shape so the client can
            // continue the conversation without resending history.
            request.HttpContext.Response.Headers[SessionHeader] = scope.SessionKey;

            if (!converseModel)
            {
                int genSteps = payload.MaxTokens ?? payload.MaxCompletionTokens ?? 128;
                double genTemp = payload.Temperature ?? 0.6;
                int genOrder = payload.Window ?? 5;
                int genTopK = payload.TopK ?? 10;

                if (genSteps is < 1 or > 4096)
                    return EndpointJson.BadRequest("invalid_request_error",
                        "Generation steps must be in the range 1..4096.");
                if (!double.IsFinite(genTemp) || genTemp <= 0)
                    return EndpointJson.BadRequest("invalid_request_error",
                        "Field 'temperature' must be a finite number greater than zero.");
                if (genOrder is < 1 or > 64)
                    return EndpointJson.BadRequest("invalid_request_error",
                        "Field 'window' must be in the range 1..64.");
                if (genTopK is < 1 or > 4096)
                    return EndpointJson.BadRequest("invalid_request_error",
                        "Field 'top_k' must be in the range 1..4096.");

                if (payload.Stream)
                {
                    var genId = $"chatcmpl-{Guid.NewGuid():N}";
                    var genCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var response = request.HttpContext.Response;
                    ServerSentEvents.Begin(response);
                    try
                    {
                        var substrateClock = new Stopwatch();
                        double? firstResultMs = null;
                        int genStreamTokens = 0;
                        await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                            genId, "chat.completion.chunk", genCreated, payload.Model,
                            [new ChatChunkChoice(0, new ChatDelta(Role: "assistant"), null)]), ct);

                        var genStreamText = new StringBuilder();
                        await using var tokenStream = substrate.WalkTextStreamAsync(
                            prompt, steps: genSteps, maxOrder: genOrder,
                            temperature: genTemp, topK: genTopK, ct: ct)
                            .GetAsyncEnumerator(ct);
                        while (true)
                        {
                            bool hasToken;
                            substrateClock.Start();
                            try { hasToken = await tokenStream.MoveNextAsync(); }
                            finally { substrateClock.Stop(); }
                            if (!hasToken) break;

                            var token = tokenStream.Current;
                            firstResultMs ??= totalClock.Elapsed.TotalMilliseconds;
                            genStreamTokens++;
                            genStreamText.Append(token.Token);
                            await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                                genId, "chat.completion.chunk", genCreated, payload.Model,
                                [new ChatChunkChoice(0, new ChatDelta(Content: token.Token), null)],
                                Laplace: new ChunkProvenance(OrdUsed: (int)token.Mu)), ct);
                        }
                        var generatedText = genStreamText.ToString().TrimStart();
                        turnWitness.EnqueueTurn(scope.Tenant, scope.UserKey, scope.SessionId,
                            prompt, generatedText);
                        var genPerformance = BuildPerformance(
                            generatedText, substrateClock, totalClock,
                            firstResultMs, generatedTokens: genStreamTokens);
                        await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                            genId, "chat.completion.chunk", genCreated, payload.Model,
                            [new ChatChunkChoice(0, new ChatDelta(Content: ""), "stop")],
                            Laplace: new ChunkProvenance(Performance: genPerformance)), ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await ServerSentEvents.WriteErrorAsync(response, "stream_failed", ex.Message, ct);
                    }
                    await ServerSentEvents.WriteDoneAsync(response, ct);
                    return Results.Empty;
                }

                var genTokens = new List<GenerateToken>(genSteps);
                var genSubstrateClock = Stopwatch.StartNew();
                double? genFirstResultMs = null;
                await foreach (var token in substrate.WalkTextStreamAsync(
                    prompt, steps: genSteps, maxOrder: genOrder,
                    temperature: genTemp, topK: genTopK, ct: ct))
                {
                    genFirstResultMs ??= totalClock.Elapsed.TotalMilliseconds;
                    genTokens.Add(token);
                }
                genSubstrateClock.Stop();

                var genContent = string.Concat(genTokens.Select(t => t.Token)).TrimStart();
                turnWitness.EnqueueTurn(scope.Tenant, scope.UserKey, scope.SessionId, prompt, genContent);

                return Results.Json(new ChatCompletionResponse(
                    Id: $"chatcmpl-{Guid.NewGuid():N}",
                    Object: "chat.completion",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model: payload.Model,
                    Choices: [new ChatChoice(0, new ChatResponseMessage("assistant", genContent), "stop")],
                    Billing: null,
                    Metadata: new ChatMetadata(
                        GeneratedTokens: genTokens.Count,
                        Session: scope.SessionKey,
                        Performance: BuildPerformance(
                            genContent, genSubstrateClock, totalClock, genFirstResultMs, genTokens.Count))));
            }

            // Default = act as a whole (global consensus). scope:"tenant" re-folds the
            // tenant's own witnessed world and reads inside it (spec 34 isolation).
            var tenantScope = ConversationContent.Resolve(scope.Tenant);
            var converseOptions = new ConverseOptions(
                payload.Shape, payload.Bands, payload.Elaborate,
                operatorLanguage?.Code, operatorLanguage?.Id,
                operatorLanguage?.Source);
            var converseSubstrateClock = Stopwatch.StartNew();
            var rows = tenantScoped
                ? await substrate.ConverseTenantScopedAsync(prompt, scope.SessionId.ToBytes(),
                    [tenantScope.PromptSource.ToBytes(), tenantScope.ResponseSource.ToBytes()],
                    converseOptions, ct)
                : await substrate.ConverseAsync(
                    prompt, scope.SessionId.ToBytes(), converseOptions, ct);
            converseSubstrateClock.Stop();
            // Empty consensus is reported truthfully: empty content + reply_rows 0.
            // The client renders the absence; the substrate never fakes prose.
            var content = string.Join("\n", rows.Select(r => r.Reply));
            var conversePerformance = BuildPerformance(
                content, converseSubstrateClock, totalClock,
                rows.Count > 0 ? totalClock.Elapsed.TotalMilliseconds : null);

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
                    [new ChatChunkChoice(0, new ChatDelta(Content: ""), "stop")],
                    Laplace: new ChunkProvenance(Performance: conversePerformance)), ct);
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
                    // Null when NO row carries a count (the converse.chat() lane) — a sum of
                    // absences is not 0, it is absence (same rule as bool_or over
                    // zero rows in the read path).
                    Witnesses: rows.Any(r => r.Witnesses is not null)
                        ? rows.Sum(r => r.Witnesses ?? 0L) : null,
                    ReplyRows: rows.Count,
                    Session: scope.SessionKey,
                    Laplace: new LaplaceChatMetadata(
                        rows.Select(r => new ProvenanceLine(r.Reply, r.EffectiveMu, r.Witnesses)).ToArray()),
                    Performance: conversePerformance)));
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
            int order = payload.Window ?? 5;
            int topK = payload.TopK ?? 10;

            if ((payload.TopP is { } topP && topP != 1.0)
                || payload.TopicBoost is not null || payload.Stop is not null)
                return EndpointJson.BadRequest("unsupported_parameter",
                    "The walk lane does not implement top_p, topic_boost, or stop; the endpoint rejects them instead of ignoring them.");
            if (steps is < 1 or > 4096 || order is < 1 or > 64 || topK is < 1 or > 4096
                || !double.IsFinite(temp) || temp <= 0)
                return EndpointJson.BadRequest("invalid_request_error",
                    "Generation requires max_tokens 1..4096, window 1..64, top_k 1..4096, and a finite positive temperature.");

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
                        payload.Prompt.Trim(), steps: steps, maxOrder: order,
                        temperature: temp, topK: topK, ct: ct))
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
                payload.Prompt.Trim(), steps: steps, maxOrder: order,
                temperature: temp, topK: topK, ct: ct))
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

    private static ChatPerformance BuildPerformance(
        string output, Stopwatch substrateClock, Stopwatch totalClock,
        double? firstResultMs, int? generatedTokens = null)
    {
        double substrateMs = substrateClock.Elapsed.TotalMilliseconds;
        double elapsedMs = totalClock.Elapsed.TotalMilliseconds;
        double? tokensPerSecond = generatedTokens is { } count && substrateMs > 0
            ? count * 1000.0 / substrateMs
            : null;
        return new ChatPerformance(
            SubstrateMs: substrateMs,
            ElapsedMs: elapsedMs,
            FirstResultMs: firstResultMs,
            OutputUtf8Bytes: Encoding.UTF8.GetByteCount(output),
            OutputCodepoints: output.EnumerateRunes().Count(),
            OutputWords: CountWords(output),
            GeneratedTokens: generatedTokens,
            GeneratedTokensPerSecond: tokensPerSecond);
    }

    private static int CountWords(string value)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                words++;
                inWord = true;
            }
        }
        return words;
    }
}
