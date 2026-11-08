using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Laplace.Api.Contracts;
using Laplace.Decomposers.Abstractions;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal static partial class InferenceEndpoints
{
    public const string SessionHeader = "X-Laplace-Session";

    public static void MapOpenAiCompatEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/chat/completions", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, IConversationWitness turnWitness, ITenantResolver tenantResolver, IIdentityStore identityStore, CancellationToken ct) =>
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

            // Only the newest user turn is the observation; earlier turns are already
            // witnessed under the session, so resent history is not read.
            var prompt = payload.Messages
                .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => m.Content!)
                .LastOrDefault();
            if (string.IsNullOrWhiteSpace(prompt))
                return EndpointJson.BadRequest("invalid_request_error", "At least one user message must include non-empty 'content'.");

            bool converseModel = ModelCatalog.IsConverse(payload.Model);
            bool hasBands = payload.Bands is { Length: > 0 };
            if (payload.WebSearch || payload.WebSearchResults is not null)
                return EndpointJson.BadRequest("unsupported_parameter",
                    "Web search is not implemented on this endpoint; no request will silently ignore it.");
            if ((payload.TopP is { } topP && topP != 1.0)
                 || payload.TopicBoost is not null || payload.Stop is not null)
                return EndpointJson.BadRequest("unsupported_parameter",
                    "The walk lane does not implement top_p, topic_boost, or stop; the endpoint rejects them instead of ignoring them.");
            int steps = payload.MaxCompletionTokens ?? payload.MaxTokens ?? 128;
            int stride = payload.Window ?? 5;
            double temperature = payload.Temperature ?? 0.6;
            int topK = payload.TopK ?? 10;
            if (steps < 1 || stride < 1 || topK < 1
                || !double.IsFinite(temperature) || temperature < 0)
                return EndpointJson.BadRequest("invalid_request_error",
                    "Output length, window, and top_k must be positive; temperature must be finite and nonnegative.");
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
            var (scope, scopeError) = await ResolveTurnScopeAsync(
                request, tenantResolver, payload.Session, payload.User, ct);
            if (scopeError is not null) return scopeError;

            var operatorLanguage = OperatorLanguage.Resolve(request, payload.Language);

            bool tenantScoped = string.Equals(payload.Scope, "tenant", StringComparison.Ordinal);
            if (payload.Scope is not null && !tenantScoped)
                return EndpointJson.BadRequest("invalid_scope",
                    "Field 'scope' accepts only \"tenant\" (isolated read over this tenant's own witnessed world).");
            if (tenantScoped && !ModelCatalog.IsConverse(payload.Model))
                return EndpointJson.BadRequest("invalid_scope",
                    "Tenant-scoped reads are only available on the converse model lane.");

            // Input and turn-witness availability are checked before the billing gate,
            // because plan-backed authorization can debit an included credit.
            if (converseModel && !string.IsNullOrWhiteSpace(payload.Shape))
            {
                var shapes = await substrate.QueryShapesAsync(ct);
                if (!shapes.Any(s => string.Equals(s.Shape, payload.Shape, StringComparison.Ordinal)))
                    return EndpointJson.BadRequest("invalid_shape",
                        $"Unknown converse shape '{payload.Shape}'. See GET /v1/query/shapes for the installed catalog.");
            }

            if (RequireTurnWitness(turnWitness) is { } chatWitnessErr) return chatWitnessErr;

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "chat.completions", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("chat.completions")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            await CatalogConversationAsync(identityStore, scope, prompt, ct);

            // The session key returns on every response shape; the client continues the
            // conversation by sending it back.
            request.HttpContext.Response.Headers[SessionHeader] = scope.SessionKey;


            // By default the pass reads global consensus. scope:"tenant" folds consensus
            // over only this tenant's prompt and response sources and reads that.
            var tenantScope = ConversationContent.Resolve(scope.Tenant);
            var converseOptions = new ConverseOptions(
                payload.Shape, payload.Bands, payload.Elaborate,
                operatorLanguage?.Code, operatorLanguage?.Id,
                operatorLanguage?.Source, steps, stride, temperature, topK);
            string occurrenceKey = await turnWitness.RecordPromptAsync(
                scope.Tenant, scope.UserKey, scope.SessionId, prompt, ct);
            bool inspection = !string.IsNullOrWhiteSpace(payload.Shape) || hasBands || payload.Elaborate;
            if (payload.Stream && !inspection && !tenantScoped)
            {
                var response = request.HttpContext.Response;
                var completionId = $"chatcmpl-{Guid.NewGuid():N}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var text = new StringBuilder();
                var substrateClock = new Stopwatch();
                double? firstResultMs = null;
                int emitted = 0;
                ServerSentEvents.Begin(response);
                try
                {
                    await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                        completionId, "chat.completion.chunk", created, payload.Model,
                        [new ChatChunkChoice(0, new ChatDelta(Role: "assistant"), null)]), ct);
                    var forwardEvents = new List<ForwardObservedEvent>();
                    await using (var stream = substrate.ForwardTurnObservedStreamAsync(
                        prompt, scope.SessionId.ToBytes(), converseOptions, ct).GetAsyncEnumerator(ct))
                    {
                        while (true)
                        {
                            bool hasNext;
                            substrateClock.Start();
                            try { hasNext = await stream.MoveNextAsync(); }
                            finally { substrateClock.Stop(); }
                            if (!hasNext) break;
                            var observed = stream.Current;
                            forwardEvents.Add(observed);
                            if (!string.Equals(observed.Event, "emit", StringComparison.Ordinal)
                                || observed.Surface is null)
                                continue;
                            firstResultMs ??= totalClock.Elapsed.TotalMilliseconds;
                            ++emitted;
                            text.Append(observed.Surface);
                            await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                                completionId, "chat.completion.chunk", created, payload.Model,
                                [new ChatChunkChoice(0, new ChatDelta(Content: observed.Surface), null)],
                                Laplace: new ChunkProvenance(OrdUsed: observed.StrideUsed)), ct);
                        }
                    }
                    bool responseWitnessed = await turnWitness.RecordResponseAsync(scope.Tenant, scope.UserKey, scope.SessionId,
                        prompt, text.ToString(), occurrenceKey, ct);
                    var forwardProof = BuildForwardProof(
                        forwardEvents, scope.SessionKey, occurrenceKey, responseWitnessed);
                    await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                        completionId, "chat.completion.chunk", created, payload.Model,
                        [new ChatChunkChoice(0, new ChatDelta(), "stop")],
                        Laplace: new ChunkProvenance(
                            Performance: BuildPerformance(
                                text.ToString(), substrateClock, totalClock, firstResultMs, emitted),
                            ForwardProof: forwardProof)), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Serilog.Log.ForContext("SourceContext", "Laplace.Inference").Error(ex, "Inference stream failed after the HTTP response started.");
                    await ServerSentEvents.WriteErrorAsync(response, "stream_failed", ex.Message, ct);
                }
                await ServerSentEvents.WriteDoneAsync(response, ct);
                return Results.Empty;
            }
            // The non-streamed forward turn consumes the same observed event stream, so
            // its forward proof comes from the invocation that produced the reply.
            if (!payload.Stream && !inspection && !tenantScoped)
            {
                var forwardEvents = new List<ForwardObservedEvent>();
                var text = new StringBuilder();
                var substrateClock = new Stopwatch();
                double? firstResultMs = null;
                int emitted = 0;

                await using (var stream = substrate.ForwardTurnObservedStreamAsync(
                    prompt, scope.SessionId.ToBytes(), converseOptions, ct).GetAsyncEnumerator(ct))
                {
                    while (true)
                    {
                        bool hasNext;
                        substrateClock.Start();
                        try { hasNext = await stream.MoveNextAsync(); }
                        finally { substrateClock.Stop(); }
                        if (!hasNext) break;

                        var observed = stream.Current;
                        forwardEvents.Add(observed);
                        if (!string.Equals(observed.Event, "emit", StringComparison.Ordinal)
                            || observed.Surface is null)
                            continue;

                        firstResultMs ??= totalClock.Elapsed.TotalMilliseconds;
                        emitted++;
                        text.Append(observed.Surface);
                    }
                }

                var observedContent = text.ToString();
                bool responseWitnessed = await turnWitness.RecordResponseAsync(
                    scope.Tenant, scope.UserKey, scope.SessionId,
                    prompt, observedContent.Length == 0 ? null : observedContent, occurrenceKey, ct);

                var forwardProof = BuildForwardProof(
                    forwardEvents, scope.SessionKey, occurrenceKey, responseWitnessed);
                var performance = BuildPerformance(
                    observedContent, substrateClock, totalClock, firstResultMs, emitted);

                return Results.Json(new ChatCompletionResponse(
                    Id: $"chatcmpl-{Guid.NewGuid():N}",
                    Object: "chat.completion",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model: payload.Model,
                    Choices: [new ChatChoice(0, new ChatResponseMessage("assistant", observedContent), "stop")],
                    Billing: null,
                    Metadata: new ChatMetadata(
                        Witnesses: null,
                        ReplyRows: emitted,
                        Session: scope.SessionKey,
                        Laplace: new LaplaceChatMetadata(
                            Array.Empty<ProvenanceLine>(), forwardProof),
                        Performance: performance)));
            }

            var converseSubstrateClock = Stopwatch.StartNew();
            var rows = tenantScoped
                ? await substrate.ConverseTenantScopedAsync(prompt, scope.SessionId.ToBytes(),
                    [tenantScope.PromptSource.ToBytes(), tenantScope.ResponseSource.ToBytes()],
                    converseOptions, ct)
                : await substrate.ConverseAsync(
                    prompt, scope.SessionId.ToBytes(), converseOptions, ct);
            converseSubstrateClock.Stop();
            // No rows means empty content and reply_rows 0; nothing substitutes a reply.
            var content = string.Join("\n", rows.Select(r => r.Reply));
            var conversePerformance = BuildPerformance(
                content, converseSubstrateClock, totalClock,
                rows.Count > 0 ? totalClock.Elapsed.TotalMilliseconds : null);

            await turnWitness.RecordResponseAsync(scope.Tenant, scope.UserKey, scope.SessionId,
                prompt, rows.Count > 0 ? content : null, occurrenceKey, ct);

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
                    // Null when no row carries a count: absent counts do not sum to 0.
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

        app.MapPost("/v1/completions", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, IConversationWitness turnWitness, ITenantResolver tenantResolver, IIdentityStore identityStore, CancellationToken ct) =>
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

            var (scope, scopeError) = await ResolveTurnScopeAsync(
                request, tenantResolver, payload.Session, payload.User, ct);
            if (scopeError is not null) return scopeError;

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

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "completions", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("completions")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            await CatalogConversationAsync(identityStore, scope, payload.Prompt, ct);

            string occurrenceKey = await turnWitness.RecordPromptAsync(
                scope.Tenant, scope.UserKey, scope.SessionId, payload.Prompt, ct);

            if (payload.Stream)
            {
                var completionId = $"cmpl-{Guid.NewGuid():N}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var response = request.HttpContext.Response;
                ServerSentEvents.Begin(response);
                try
                {
                    var streamText = new StringBuilder();
                    await foreach (var token in substrate.ForwardTurnStreamAsync(
                        payload.Prompt, scope.SessionId.ToBytes(),
                        new ConverseOptions(MaxTokens: steps, Window: order, Temperature: temp, TopK: topK), ct))
                    {
                        streamText.Append(token.Token);
                        await ServerSentEvents.WriteJsonAsync(response, new CompletionChunk(
                            completionId, "text_completion", created, payload.Model,
                            [new CompletionChoice(token.Token, 0, null,
                                payload.Logprobs.HasValue ? new CompletionLogprobs([(double)token.Mu]) : null)]), ct);
                    }
                    await turnWitness.RecordResponseAsync(scope.Tenant, scope.UserKey, scope.SessionId,
                        payload.Prompt, streamText.ToString(), occurrenceKey, ct);
                    await ServerSentEvents.WriteJsonAsync(response, new CompletionChunk(
                        completionId, "text_completion", created, payload.Model,
                        [new CompletionChoice("", 0, "stop", null)]), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Serilog.Log.ForContext("SourceContext", "Laplace.Inference").Error(ex, "Inference stream failed after the HTTP response started.");
                    await ServerSentEvents.WriteErrorAsync(response, "stream_failed", ex.Message, ct);
                }
                await ServerSentEvents.WriteDoneAsync(response, ct);
                return Results.Empty;
            }

            var tokens = new List<GenerateToken>(steps);
            await foreach (var token in substrate.ForwardTurnStreamAsync(
                payload.Prompt, scope.SessionId.ToBytes(),
                new ConverseOptions(MaxTokens: steps, Window: order, Temperature: temp, TopK: topK), ct))
                tokens.Add(token);

            var text = string.Concat(tokens.Select(t => t.Token));
            await turnWitness.RecordResponseAsync(scope.Tenant, scope.UserKey, scope.SessionId,
                payload.Prompt, text, occurrenceKey, ct);
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

        MapEmbeddingAndSearchEndpoints(app);
        app.MapReportEndpoints();
        app.MapExploreEndpoints();
    }
}
