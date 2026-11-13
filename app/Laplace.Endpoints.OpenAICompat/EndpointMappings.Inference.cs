using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Laplace.Api.Contracts;
using Laplace.Decomposers.Abstractions;
using Laplace.Endpoints.OpenAICompat.Auth;

namespace Laplace.Endpoints.OpenAICompat;

internal static partial class InferenceEndpoints
{
    public const string SessionHeader = "X-Laplace-Session";

    // The one forward program answers under its firmware (Wiki Sequence 20, 21.4): its hops, fan, k and
    // emission budget are the firmware's, and it reads the whole world the caller is authorized for. A
    // request that asks for another budget, another sampler or another read is told so, never ignored.
    private const string FirmwareGoverns =
        "The forward program answers under its firmware; the request cannot set its emission budget, sampling, read shape, bands, scope or language.";

    public static void MapOpenAiCompatEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/chat/completions", async (HttpRequest request, IBillingOrchestrator billing, IConversationTurns turns, ITenantResolver tenantResolver, IIdentityStore identityStore, CancellationToken ct) =>
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

            // Only the newest user turn is the observation; earlier turns are already
            // witnessed under the session, so resent history is not read.
            var prompt = payload.Messages
                .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => m.Content!)
                .LastOrDefault();
            if (string.IsNullOrWhiteSpace(prompt))
                return EndpointJson.BadRequest("invalid_request_error", "At least one user message must include non-empty 'content'.");

            if (payload.WebSearch || payload.WebSearchResults is not null)
                return EndpointJson.BadRequest("unsupported_parameter",
                    "Web search is not implemented on this endpoint; no request will silently ignore it.");
            if (payload.MaxTokens is not null || payload.MaxCompletionTokens is not null
                || payload.Temperature is not null || payload.TopP is not null || payload.TopK is not null
                || payload.Window is not null || payload.TopicBoost is not null || payload.Stop is not null
                || payload.Scope is not null || payload.Shape is not null || payload.Bands is not null
                || payload.Elaborate || payload.Language is not null)
                return EndpointJson.BadRequest("unsupported_parameter", FirmwareGoverns);

            var (scope, scopeError) = await ResolveTurnScopeAsync(
                request, tenantResolver, payload.Session, payload.User, ct);
            if (scopeError is not null) return scopeError;

            // The Engine is checked before the billing gate, because plan-backed
            // authorization can debit an included credit.
            if (RequireTurns(turns) is { } unavailable) return unavailable;

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

            var totalClock = Stopwatch.StartNew();
            if (payload.Stream)
            {
                var response = request.HttpContext.Response;
                var completionId = $"chatcmpl-{Guid.NewGuid():N}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ServerSentEvents.Begin(response);
                try
                {
                    await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                        completionId, "chat.completion.chunk", created, payload.Model,
                        [new ChatChunkChoice(0, new ChatDelta(Role: "assistant"), null)]), ct);
                    double? firstResultMs = null;
                    var receipt = await StreamTurnAsync(turns, scope, prompt, async step =>
                    {
                        firstResultMs ??= totalClock.Elapsed.TotalMilliseconds;
                        await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                            completionId, "chat.completion.chunk", created, payload.Model,
                            [new ChatChunkChoice(0, new ChatDelta(Content: step.Text), null)],
                            Laplace: new ChunkProvenance(Step: Step(step))), ct);
                    }, ct);
                    await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                        completionId, "chat.completion.chunk", created, payload.Model,
                        [new ChatChunkChoice(0, new ChatDelta(), "stop")],
                        Laplace: new ChunkProvenance(
                            Performance: BuildPerformance(receipt.Response, totalClock, firstResultMs, receipt.Steps.Count),
                            ForwardProof: BuildForwardProof(receipt, scope.SessionKey))), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Serilog.Log.ForContext("SourceContext", "Laplace.Inference").Error(ex, "Inference stream failed after the HTTP response started.");
                    await ServerSentEvents.WriteErrorAsync(response, "stream_failed", ex.Message, ct);
                }
                await ServerSentEvents.WriteDoneAsync(response, ct);
                return Results.Empty;
            }

            NativeTurnReceipt turn;
            try { turn = await turns.TurnAsync(scope.Participant, scope.SessionKey, prompt, null, ct); }
            catch (InvalidOperationException failure)
            {
                return EndpointJson.ServiceUnavailable("turn_failed", failure.Message);
            }
            var firstMs = turn.Steps.Count > 0 ? totalClock.Elapsed.TotalMilliseconds : (double?)null;
            return Results.Json(new ChatCompletionResponse(
                Id: $"chatcmpl-{Guid.NewGuid():N}",
                Object: "chat.completion",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model: payload.Model,
                Choices: [new ChatChoice(0, new ChatResponseMessage("assistant", turn.Response), "stop")],
                Billing: null,
                Metadata: new ChatMetadata(
                    ReplyRows: turn.Response.Length == 0 ? 0 : 1,
                    GeneratedTokens: turn.Steps.Count,
                    Session: scope.SessionKey,
                    Laplace: new LaplaceChatMetadata(
                        Array.Empty<ProvenanceLine>(), BuildForwardProof(turn, scope.SessionKey)),
                    Performance: BuildPerformance(turn.Response, totalClock, firstMs, turn.Steps.Count))));
        })
        .WithTags("openai")
        .Accepts<ChatCompletionsRequest>("application/json")
        .Produces<ChatCompletionResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/completions", async (HttpRequest request, IBillingOrchestrator billing, IConversationTurns turns, ITenantResolver tenantResolver, IIdentityStore identityStore, CancellationToken ct) =>
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
            if (payload.MaxTokens is not null || payload.Temperature is not null || payload.TopP is not null
                || payload.TopK is not null || payload.Window is not null || payload.TopicBoost is not null
                || payload.Stop is not null || payload.Echo || payload.Logprobs is not null)
                return EndpointJson.BadRequest("unsupported_parameter", FirmwareGoverns);

            var (scope, scopeError) = await ResolveTurnScopeAsync(
                request, tenantResolver, payload.Session, payload.User, ct);
            if (scopeError is not null) return scopeError;

            if (RequireTurns(turns) is { } unavailable) return unavailable;

            request.HttpContext.Response.Headers[SessionHeader] = scope.SessionKey;

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "completions", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("completions")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            await CatalogConversationAsync(identityStore, scope, payload.Prompt, ct);

            if (payload.Stream)
            {
                var completionId = $"cmpl-{Guid.NewGuid():N}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var response = request.HttpContext.Response;
                ServerSentEvents.Begin(response);
                try
                {
                    await StreamTurnAsync(turns, scope, payload.Prompt, step =>
                        ServerSentEvents.WriteJsonAsync(response, new CompletionChunk(
                            completionId, "text_completion", created, payload.Model,
                            [new CompletionChoice(step.Text, 0, null, null)]), ct), ct);
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

            NativeTurnReceipt turn;
            try { turn = await turns.TurnAsync(scope.Participant, scope.SessionKey, payload.Prompt, null, ct); }
            catch (InvalidOperationException failure)
            {
                return EndpointJson.ServiceUnavailable("turn_failed", failure.Message);
            }
            return Results.Json(new CompletionResponse(
                Id: $"cmpl-{Guid.NewGuid():N}",
                Object: "text_completion",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model: payload.Model,
                Choices: [new CompletionChoice(turn.Response, 0, "stop", null)],
                Billing: gate.Quote is null
                    ? null
                    : new CompletionsReceipt(gate.Quote.QuoteId, gate.Quote.AmountCents, gate.Quote.Currency, gate.Quote.Tenant)));
        })
        .WithTags("openai")
        .Accepts<CompletionsRequest>("application/json")
        .Produces<CompletionResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        MapEmbeddingAndSearchEndpoints(app);
        app.MapReportEndpoints();
        app.MapExploreEndpoints();
    }

    /// <summary>
    /// Runs the turn and hands each step to <paramref name="write"/> as the Engine takes it, in order, on
    /// the request's own task: the Engine reports a step on the thread that reads its receipt, and the
    /// response is written here.
    /// </summary>
    private static async Task<NativeTurnReceipt> StreamTurnAsync(IConversationTurns turns, TurnScope scope,
        string prompt, Func<NativeTurnStep, Task> write, CancellationToken ct)
    {
        var steps = Channel.CreateUnbounded<NativeTurnStep>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var running = Task.Run(async () =>
        {
            try { return await turns.TurnAsync(scope.Participant, scope.SessionKey, prompt, step => steps.Writer.TryWrite(step), ct); }
            finally { steps.Writer.TryComplete(); }
        }, ct);
        await foreach (var step in steps.Reader.ReadAllAsync(ct)) await write(step);
        return await running;
    }

    private static ForwardTurnStep Step(NativeTurnStep step)
        => new(step.Step, step.Kind, step.Id, step.Text, step.Grounds, step.Continuity, step.Confidence);
}
