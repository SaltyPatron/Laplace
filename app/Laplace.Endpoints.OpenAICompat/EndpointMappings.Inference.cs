using System.Diagnostics;
using System.Threading.Channels;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Laplace.Native;

namespace Laplace.Endpoints.OpenAICompat;

internal static partial class InferenceEndpoints
{
    public const string SessionHeader = "X-Laplace-Session";

    private const string UnsupportedDialMessage =
        "Top-p/top-k/window/topic_boost/shape/bands/elaborate/language sampling dials are governed by the firmware; use temperature, max_tokens, stop, seed, scope, witnesses, and envelope.";

    public static void MapOpenAiCompatEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/chat/completions", async (
            HttpRequest request,
            IBillingOrchestrator billing,
            IConversationTurns turns,
            ITenantResolver tenantResolver,
            IIdentityStore identityStore,
            CancellationToken ct) =>
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

            var (prompt, systemPrompt, history) = ForwardProofHelper.ExtractChatInputs(payload.System, payload.Messages);
            if (string.IsNullOrWhiteSpace(prompt))
                return EndpointJson.BadRequest("invalid_request_error", "At least one user message must include non-empty 'content'.");

            if (payload.WebSearch || payload.WebSearchResults is not null)
                return EndpointJson.BadRequest("unsupported_parameter",
                    "Web search is not implemented on this endpoint; no request will silently ignore it.");
            if (payload.TopP is not null || payload.TopK is not null
                || payload.Window is not null || payload.TopicBoost is not null
                || payload.Shape is not null || payload.Bands is not null
                || payload.Elaborate || payload.Language is not null)
                return EndpointJson.BadRequest("unsupported_parameter", UnsupportedDialMessage);

            if (payload.Temperature is { } temp && (!double.IsFinite(temp) || temp < 0.0 || temp > 2.0))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'temperature' must be a finite number between 0 and 2.");
            if (!ForwardProofHelper.TryParseStopSequences(payload.Stop, out var stopSequences, out var stopErr))
                return EndpointJson.BadRequest("invalid_request_error", stopErr!);

            int? requestedMaxTokens = payload.MaxCompletionTokens ?? payload.MaxTokens;
            var envRes = EnvelopeBudgetPolicy.Resolve(
                request,
                payload.Envelope,
                payload.MaxWallUs,
                payload.MaxDbProbes,
                payload.MaxRowsScanned,
                payload.MaxTokensOut,
                openaiMaxTokens: requestedMaxTokens);
            if (!envRes.Valid)
                return EndpointJson.BadRequest(envRes.ErrorCode!, envRes.ErrorMessage!);

            var tenantContext = await tenantResolver.ResolveAsync(request.HttpContext, ct);
            var auth = AuthorityScopePolicy.Resolve(
                request,
                tenantContext,
                (uint)(LaplaceNative.LP_CAP_READ | LaplaceNative.LP_CAP_ATTEST),
                requestWrite: true,
                explicitScope: payload.Scope,
                explicitWitnesses: payload.Witnesses);
            if (!auth.Allowed)
                return Results.Json(
                    new ErrorResponse(new ErrorBody("authorization_error", auth.ErrorCode!, auth.ErrorMessage!)),
                    statusCode: auth.StatusCode);

            var (scope, scopeError) = ResolveTurnScope(tenantContext, request, payload.Session, payload.User);
            if (scopeError is not null) return scopeError;

            if (RequireTurns(turns) is { } unavailable) return unavailable;

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "chat.completions", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("chat.completions")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            await CatalogConversationAsync(identityStore, scope, prompt, ct);

            request.HttpContext.Response.Headers[SessionHeader] = scope.SessionKey;

            var turnRequest = new EngineTurnRequest(
                FirmwarePath: null,
                ModelName: ModelCatalog.ResolveFirmwareModel(payload.Model),
                Prompt: prompt,
                Messages: history,
                SystemPrompt: systemPrompt,
                User: scope.Participant,
                Session: scope.SessionKey,
                ReadOnly: false,
                MaxTokens: requestedMaxTokens ?? 0,
                Temperature: payload.Temperature ?? 0.0,
                StopSequences: stopSequences,
                Seed: payload.Seed ?? 0L,
                Authority: auth.Scope,
                Envelope: envRes.Envelope);

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
                    var receipt = await StreamTurnAsync(turns, turnRequest, envRes.PresetName, envRes.EstimatedUs, async step =>
                    {
                        firstResultMs ??= totalClock.Elapsed.TotalMilliseconds;
                        await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                            completionId, "chat.completion.chunk", created, payload.Model,
                            [new ChatChunkChoice(0, new ChatDelta(Content: step.Text), null)],
                            Laplace: new ChunkProvenance(Step: Step(step))), ct);
                    }, ct);

                    if (gate.Quote is not null && receipt.AbiResult?.Shares is { Count: > 0 } streamShares)
                        await billing.RecordRoyaltiesAsync(gate.Quote, BillingOrchestrator.FromWitnessShares(streamShares), ct);

                    await ServerSentEvents.WriteJsonAsync(response, new ChatCompletionChunk(
                        completionId, "chat.completion.chunk", created, payload.Model,
                        [new ChatChunkChoice(0, new ChatDelta(), "stop")],
                        Laplace: new ChunkProvenance(
                            Witnesses: receipt.AbiResult?.Shares.Count,
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
            try
            {
                turn = await turns.TurnAsync(turnRequest, envRes.PresetName, envRes.EstimatedUs, null, ct);
            }
            catch (InvalidOperationException failure)
            {
                return EndpointJson.ServiceUnavailable("turn_failed", failure.Message);
            }

            IReadOnlyList<BillingRoyaltyRecord>? settledRoyalties = null;
            if (gate.Quote is not null && turn.AbiResult?.Shares is { Count: > 0 } shares)
                settledRoyalties = await billing.RecordRoyaltiesAsync(gate.Quote, BillingOrchestrator.FromWitnessShares(shares), ct);

            var firstMs = turn.Steps.Count > 0 ? totalClock.Elapsed.TotalMilliseconds : (double?)null;
            return Results.Json(new ChatCompletionResponse(
                Id: $"chatcmpl-{Guid.NewGuid():N}",
                Object: "chat.completion",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model: payload.Model,
                Choices: [new ChatChoice(0, new ChatResponseMessage("assistant", turn.Response), "stop")],
                Billing: gate.Quote is null ? null : QuoteGate.MakeReceipt(gate.Quote, settledRoyalties),
                Metadata: new ChatMetadata(
                    Witnesses: turn.AbiResult?.Shares.Count,
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
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/completions", async (
            HttpRequest request,
            IBillingOrchestrator billing,
            IConversationTurns turns,
            ITenantResolver tenantResolver,
            IIdentityStore identityStore,
            CancellationToken ct) =>
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
            if (payload.TopP is not null || payload.TopK is not null
                || payload.Window is not null || payload.TopicBoost is not null
                || payload.Echo || payload.Logprobs is not null)
                return EndpointJson.BadRequest("unsupported_parameter", UnsupportedDialMessage);

            if (payload.Temperature is { } temp && (!double.IsFinite(temp) || temp < 0.0 || temp > 2.0))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'temperature' must be a finite number between 0 and 2.");
            if (!ForwardProofHelper.TryParseStopSequences(payload.Stop, out var stopSequences, out var stopErr))
                return EndpointJson.BadRequest("invalid_request_error", stopErr!);

            var envRes = EnvelopeBudgetPolicy.Resolve(
                request,
                payload.Envelope,
                payload.MaxWallUs,
                payload.MaxDbProbes,
                payload.MaxRowsScanned,
                payload.MaxTokensOut,
                openaiMaxTokens: payload.MaxTokens);
            if (!envRes.Valid)
                return EndpointJson.BadRequest(envRes.ErrorCode!, envRes.ErrorMessage!);

            var tenantContext = await tenantResolver.ResolveAsync(request.HttpContext, ct);
            var auth = AuthorityScopePolicy.Resolve(
                request,
                tenantContext,
                (uint)(LaplaceNative.LP_CAP_READ | LaplaceNative.LP_CAP_ATTEST),
                requestWrite: true,
                explicitScope: payload.Scope,
                explicitWitnesses: payload.Witnesses);
            if (!auth.Allowed)
                return Results.Json(
                    new ErrorResponse(new ErrorBody("authorization_error", auth.ErrorCode!, auth.ErrorMessage!)),
                    statusCode: auth.StatusCode);

            var (scope, scopeError) = ResolveTurnScope(tenantContext, request, payload.Session, payload.User);
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

            var turnRequest = new EngineTurnRequest(
                FirmwarePath: null,
                ModelName: ModelCatalog.ResolveFirmwareModel(payload.Model),
                Prompt: payload.Prompt,
                Messages: null,
                SystemPrompt: null,
                User: scope.Participant,
                Session: scope.SessionKey,
                ReadOnly: false,
                MaxTokens: payload.MaxTokens ?? 0,
                Temperature: payload.Temperature ?? 0.0,
                StopSequences: stopSequences,
                Seed: payload.Seed ?? 0L,
                Authority: auth.Scope,
                Envelope: envRes.Envelope);

            if (payload.Stream)
            {
                var completionId = $"cmpl-{Guid.NewGuid():N}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var response = request.HttpContext.Response;
                ServerSentEvents.Begin(response);
                try
                {
                    var receipt = await StreamTurnAsync(turns, turnRequest, envRes.PresetName, envRes.EstimatedUs, step =>
                        ServerSentEvents.WriteJsonAsync(response, new CompletionChunk(
                            completionId, "text_completion", created, payload.Model,
                            [new CompletionChoice(step.Text, 0, null, null)]), ct), ct);
                    if (gate.Quote is not null && receipt.AbiResult?.Shares is { Count: > 0 } streamShares)
                        await billing.RecordRoyaltiesAsync(gate.Quote, BillingOrchestrator.FromWitnessShares(streamShares), ct);
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
            try
            {
                turn = await turns.TurnAsync(turnRequest, envRes.PresetName, envRes.EstimatedUs, null, ct);
            }
            catch (InvalidOperationException failure)
            {
                return EndpointJson.ServiceUnavailable("turn_failed", failure.Message);
            }

            if (gate.Quote is not null && turn.AbiResult?.Shares is { Count: > 0 } shares)
                await billing.RecordRoyaltiesAsync(gate.Quote, BillingOrchestrator.FromWitnessShares(shares), ct);

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
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        MapEmbeddingAndSearchEndpoints(app);
        app.MapReportEndpoints();
        app.MapExploreEndpoints();
    }

    private static async Task<NativeTurnReceipt> StreamTurnAsync(
        IConversationTurns turns,
        EngineTurnRequest turnRequest,
        string envelopePreset,
        ulong envelopeEstimatedUs,
        Func<NativeTurnStep, Task> write,
        CancellationToken ct)
    {
        var steps = Channel.CreateUnbounded<NativeTurnStep>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var running = Task.Run(async () =>
        {
            try
            {
                return await turns.TurnAsync(
                    turnRequest,
                    envelopePreset,
                    envelopeEstimatedUs,
                    step => steps.Writer.TryWrite(step),
                    ct);
            }
            finally
            {
                steps.Writer.TryComplete();
            }
        }, ct);
        await foreach (var step in steps.Reader.ReadAllAsync(ct)) await write(step);
        return await running;
    }

    private static ForwardTurnStep Step(NativeTurnStep step)
        => new(step.Step, step.Kind, step.Id, step.Text, step.Grounds, step.Continuity, step.Confidence);
}
