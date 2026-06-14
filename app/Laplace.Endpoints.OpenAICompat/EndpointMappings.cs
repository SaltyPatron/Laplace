using System.Text;
using System.Text.Json;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;

namespace Laplace.Endpoints.OpenAICompat;

internal static class EndpointMappings
{
    public static void MapCoreEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Json(new HealthResponse("ok", "F-scaffold")))
            .WithTags("core").Produces<HealthResponse>();

        app.MapGet("/v1/models", () => Results.Json(new ModelList("list",
        [
            new ModelInfo("laplace-converse-001", "model", 0, "laplace"),
            new ModelInfo("laplace-completions-001", "model", 0, "laplace"),
            new ModelInfo("laplace-code-001", "model", 0, "laplace"),
            new ModelInfo("laplace-embeddings-pending", "model", 0, "laplace", Status: "pending")
        ]))).WithTags("core").Produces<ModelList>();

        app.MapGet("/v1/capabilities", () => Results.Json(new CapabilitiesResponse("F-scaffold", new CapabilityEndpoints(
            ChatCompletions: new CapabilityStatus("live", Backend: "laplace.recall_session", Billing: "preflight_quote_required"),
            Completions: new CapabilityStatus("live", Backend: "laplace.completions", Billing: "preflight_quote_required"),
            Embeddings: new CapabilityStatus("pending", Reason: "requires Stream E physicality lookup path"),
            AuditReports: new CapabilityStatus("live", Backend: "laplace.substrate_counts + laplace.consensus_stats + laplace.top_relations", Billing: "audit.deep_report"),
            Visualizations: new CapabilityStatus("live", Backend: "laplace.top_relations + laplace.entity_physicalities", Billing: "visualization.deep_export"),
            ExplainabilityReports: new CapabilityStatus("live", Backend: "laplace.walk_branches + laplace.attestations_out", Billing: "explain.trace"),
            Billing: new CapabilityStatus("live", Provider: "stripe_or_manual"),
            Models: new CapabilityStatus("live")))))
            .WithTags("core").Produces<CapabilitiesResponse>();
    }

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

            // Chat IS a trajectory walk: the native stride-continuation surface over
            // witnessed content trajectories is modality-blind — prose and code are both
            // entity sequences over the same floor — so every chat model walks, and the
            // intent-routed consensus-lookup path (laplace.recall_session) is the opt-in
            // grounded path selected by a "converse" model id.
            if (!payload.Model.Contains("converse", StringComparison.OrdinalIgnoreCase))
            {
                int genSteps = payload.MaxTokens ?? payload.MaxCompletionTokens ?? 128;
                double genTemp = payload.Temperature ?? 0.6;

                if (payload.Stream)
                {
                    var genId = $"chatcmpl-{Guid.NewGuid():N}";
                    var genCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    request.HttpContext.Response.ContentType = "text/event-stream";
                    request.HttpContext.Response.Headers["Cache-Control"] = "no-cache";
                    request.HttpContext.Response.Headers["X-Accel-Buffering"] = "no";

                    var genRole = JsonSerializer.Serialize(new ChatCompletionChunk(
                        genId, "chat.completion.chunk", genCreated, payload.Model,
                        [new ChatChunkChoice(0, new ChatDelta(Role: "assistant"), null)]));
                    await request.HttpContext.Response.WriteAsync($"data: {genRole}\n\n", ct);

                    var genStreamText = new StringBuilder();
                    await foreach (var token in substrate.WalkTextStreamAsync(
                        prompt, steps: genSteps, temperature: genTemp, ct: ct))
                    {
                        genStreamText.Append(token.Token);
                        var chunk = JsonSerializer.Serialize(new ChatCompletionChunk(
                            genId, "chat.completion.chunk", genCreated, payload.Model,
                            [new ChatChunkChoice(0, new ChatDelta(Content: token.Token), null)],
                            Laplace: new ChunkProvenance(OrdUsed: (int)token.Mu)));
                        await request.HttpContext.Response.WriteAsync($"data: {chunk}\n\n", ct);
                    }
                    turnWitness.Enqueue(prompt, "prompt");
                    turnWitness.Enqueue(genStreamText.ToString().TrimStart(), "reply");
                    var genStop = JsonSerializer.Serialize(new ChatCompletionChunk(
                        genId, "chat.completion.chunk", genCreated, payload.Model,
                        [new ChatChunkChoice(0, new ChatDelta(Content: ""), "stop")]));
                    await request.HttpContext.Response.WriteAsync($"data: {genStop}\n\n", ct);
                    await request.HttpContext.Response.WriteAsync("data: [DONE]\n\n", ct);
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

            // Serve the substrate's intent-routed consensus lookup (laplace.recall_session): parse_ask
            // classifies the ask, recall() grounds every reply line in witnessed consensus.
            // Session id is derived from the conversation's earlier turns so recall_session keeps
            // topic/pronoun continuity ("…and its synonyms?") across calls via its last-topic pointer.
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

            // Only the FINAL user turn is new testimony: stateless clients resend the
            // full history every call, and prior turns were each final once already.
            turnWitness.Enqueue(userTurns[^1], "prompt");
            if (rows.Count > 0) turnWitness.Enqueue(content, "reply");

            if (payload.Stream)
            {
                var completionId = $"chatcmpl-{Guid.NewGuid():N}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                request.HttpContext.Response.ContentType = "text/event-stream";
                request.HttpContext.Response.Headers["Cache-Control"] = "no-cache";
                request.HttpContext.Response.Headers["X-Accel-Buffering"] = "no";

                var roleChunk = JsonSerializer.Serialize(new ChatCompletionChunk(
                    completionId, "chat.completion.chunk", created, payload.Model,
                    [new ChatChunkChoice(0, new ChatDelta(Role: "assistant"), null)]));
                await request.HttpContext.Response.WriteAsync($"data: {roleChunk}\n\n", ct);

                // One consensus-grounded reply line per chunk, each carrying its receipt
                for (int i = 0; i < rows.Count; i++)
                {
                    var line = rows[i].Reply + (i + 1 < rows.Count ? "\n" : "");
                    var chunk = JsonSerializer.Serialize(new ChatCompletionChunk(
                        completionId, "chat.completion.chunk", created, payload.Model,
                        [new ChatChunkChoice(0, new ChatDelta(Content: line), null)],
                        Laplace: new ChunkProvenance(EffMu: rows[i].EffectiveMu, Witnesses: rows[i].Witnesses)));
                    await request.HttpContext.Response.WriteAsync($"data: {chunk}\n\n", ct);
                }

                var stopChunk = JsonSerializer.Serialize(new ChatCompletionChunk(
                    completionId, "chat.completion.chunk", created, payload.Model,
                    [new ChatChunkChoice(0, new ChatDelta(Content: ""), "stop")]));
                await request.HttpContext.Response.WriteAsync($"data: {stopChunk}\n\n", ct);
                await request.HttpContext.Response.WriteAsync("data: [DONE]\n\n", ct);
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
                request.HttpContext.Response.ContentType = "text/event-stream";
                request.HttpContext.Response.Headers["Cache-Control"] = "no-cache";
                request.HttpContext.Response.Headers["X-Accel-Buffering"] = "no";

                var streamText = new StringBuilder();
                await foreach (var token in substrate.WalkTextStreamAsync(
                    payload.Prompt.Trim(), steps: steps, temperature: temp, ct: ct))
                {
                    streamText.Append(token.Token);
                    var chunk = JsonSerializer.Serialize(new CompletionChunk(
                        completionId, "text_completion", created, payload.Model,
                        [new CompletionChoice(token.Token, 0, null,
                            payload.Logprobs.HasValue ? new CompletionLogprobs([(double)token.Mu]) : null)]));
                    await request.HttpContext.Response.WriteAsync($"data: {chunk}\n\n", ct);
                }
                turnWitness.Enqueue(payload.Prompt.Trim(), "prompt");
                turnWitness.Enqueue(streamText.ToString().TrimStart(), "reply");
                await request.HttpContext.Response.WriteAsync("data: [DONE]\n\n", ct);
                return Results.Empty;
            }

            // Non-streaming: collect full sequence then return
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

        // The trust surface: quote-free receipt drill-down. Target = entity id (32-hex) or word.
        app.MapGet("/v1/evidence/{target}", async (string target, int? limit, ISubstrateClient substrate, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(target))
                return EndpointJson.BadRequest("invalid_request_error", "Route parameter 'target' is required.");

            try
            {
                var evidence = await substrate.EvidenceAsync(target.Trim(), Math.Clamp(limit ?? 10, 1, 50), ct);
                if (evidence is null)
                    return EndpointJson.NotFound("entity_not_found", $"No entity for target '{target.Trim()}'.");

                return Results.Json(new EvidenceResponse(
                    EntityId: evidence.EntityIdHex,
                    EntityLabel: evidence.EntityLabel,
                    Evidence: evidence.Items));
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("openai")
        .Produces<EvidenceResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/audit/report", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<AuditReportRequest>(request, ct) ?? new AuditReportRequest();
            var gate = await RequireQuoteAsync(request, billing, "audit.deep_report", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null ? null : new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            try
            {
                var report = await substrate.AuditReportAsync(
                    includeConsensus: payload.IncludeConsensus,
                    includeConvergence: payload.IncludeConvergence,
                    topRelationLimit: payload.Academic ? 50 : 20,
                    ct);
                if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

                return Results.Json(new AuditReportResponse(
                    Id: $"audit-{Guid.NewGuid():N}",
                    Object: "laplace.audit.report",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Scope: string.IsNullOrWhiteSpace(payload.Scope) ? "summary" : payload.Scope.Trim(),
                    Academic: payload.Academic,
                    IncludeEvidence: payload.IncludeEvidence,
                    IncludeConsensus: payload.IncludeConsensus,
                    IncludeConvergence: payload.IncludeConvergence,
                    Report: report,
                    Billing: gate.Quote is null ? null : MakeReceipt(gate.Quote)));
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("reports")
        .Accepts<AuditReportRequest>("application/json")
        .Produces<AuditReportResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/visualizations/substrate", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<VisualizationExecuteRequest>(request, ct) ?? new VisualizationExecuteRequest();
            var gate = await RequireQuoteAsync(request, billing, "visualization.deep_export", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null ? null : new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            try
            {
                var graph = await substrate.VisualizationGraphAsync(
                    limit: Math.Clamp(payload.Limit ?? 100, 1, 500),
                    includeGeometry: payload.IncludeGeometry,
                    includeEvidence: payload.IncludeEvidence,
                    ct);
                if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

                return Results.Json(new VisualizationGraphResponse(
                    Id: $"viz-{Guid.NewGuid():N}",
                    Object: "laplace.visualization.graph",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Format: string.IsNullOrWhiteSpace(payload.Format) ? "json" : payload.Format.Trim(),
                    IncludeGeometry: payload.IncludeGeometry,
                    IncludeEvidence: payload.IncludeEvidence,
                    Graph: graph,
                    Billing: gate.Quote is null ? null : MakeReceipt(gate.Quote)));
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("reports")
        .Accepts<VisualizationExecuteRequest>("application/json")
        .Produces<VisualizationGraphResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/explain/report", async (HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ExplainReportRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Prompt))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'prompt' is required.");
            if (payload.Depth < 1 || payload.Beam < 1)
                return EndpointJson.BadRequest("invalid_request_error", "Fields 'depth' and 'beam' must each be >= 1.");

            var gate = await RequireQuoteAsync(request, billing, "explain.trace", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null ? null : new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

            try
            {
                var trace = await substrate.ExplainTraceAsync(
                    payload.Prompt.Trim(),
                    payload.Depth,
                    payload.Beam,
                    includeEvidence: payload.Academic,
                    ct);
                if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

                return Results.Json(new ExplainReportResponse(
                    Id: $"explain-{Guid.NewGuid():N}",
                    Object: "laplace.explainability.report",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Prompt: payload.Prompt.Trim(),
                    Depth: payload.Depth,
                    Beam: payload.Beam,
                    Academic: payload.Academic,
                    Trace: trace,
                    Billing: gate.Quote is null ? null : MakeReceipt(gate.Quote)));
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("reports")
        .Accepts<ExplainReportRequest>("application/json")
        .Produces<ExplainReportResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Derive a stable 16-byte session id from the conversation's earlier turns (everything but the
    /// final user message). The same multi-turn conversation reuses the same session, so the
    /// substrate's recall_session() keeps topic/pronoun continuity ("…and its synonyms?"). A single-message
    /// request yields null → recall_session falls back to its per-backend session.
    /// </summary>
    private static byte[]? DeriveSessionId(IReadOnlyList<ChatMessage>? messages)
    {
        var anchor = messages?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Content))?.Content;
        if (string.IsNullOrWhiteSpace(anchor)) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(anchor));
        return hash[..16];
    }

    private static async Task<QuoteExecutionGate> RequireQuoteAsync(HttpRequest request, IBillingOrchestrator billing, string serviceId, CancellationToken ct)
    {
        var quoteId = AppComposition.ResolveQuoteId(request) ?? "";
        return await billing.EnsureExecutableAsync(quoteId, serviceId, ct);
    }

    private static BillingReceipt MakeReceipt(BillingQuote quote) =>
        new(quote.QuoteId, quote.AmountCents, quote.Currency, quote.Tenant, quote.ServiceId);

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

    public static void MapBillingEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/billing/catalog", async (IBillingCatalog catalog, IStripePriceMap priceMap, CancellationToken ct) =>
        {
            var services = new List<CatalogServiceView>();
            foreach (var s in catalog.List())
            {
                services.Add(new CatalogServiceView(
                    ServiceId: s.ServiceId,
                    ProductId: s.ProductId,
                    DisplayName: s.DisplayName,
                    Unit: s.UnitName,
                    UnitPriceCents: s.UnitPriceCents,
                    BaseFeeCents: s.BaseFeeCents,
                    Currency: s.Currency,
                    LookupKey: s.LookupKey,
                    Active: s.Active,
                    Metered: s.Metered,
                    RecurringInterval: s.RecurringInterval,
                    StripePriceId: await priceMap.TryGetAsync(s.LookupKey, ct)));
            }
            return Results.Json(new BillingCatalogResponse("list", services));
        })
            .WithTags("billing").Produces<BillingCatalogResponse>();

        app.MapGet("/v1/billing/products", (IBillingCatalog catalog) =>
            Results.Json(new BillingProductsResponse("list",
                catalog.ListProducts().Select(p => new ProductView(
                    ProductId: p.ProductId,
                    Name: p.Name,
                    Description: p.Description,
                    Category: p.Category,
                    Prices: catalog.List()
                        .Where(s => string.Equals(s.ProductId, p.ProductId, StringComparison.OrdinalIgnoreCase))
                        .Select(s => new ProductPriceView(
                            ServiceId: s.ServiceId,
                            Unit: s.UnitName,
                            UnitPriceCents: s.UnitPriceCents,
                            BaseFeeCents: s.BaseFeeCents,
                            Currency: s.Currency,
                            LookupKey: s.LookupKey,
                            Metered: s.Metered,
                            RecurringInterval: s.RecurringInterval)).ToArray())).ToArray())))
            .WithTags("billing").Produces<BillingProductsResponse>();

        app.MapGet("/v1/billing/plans", (IBillingCatalog catalog) =>
            Results.Json(new BillingPlansResponse("list",
                catalog.ListPlans().Select(p => new PlanView(
                    PlanId: p.PlanId,
                    ServiceId: p.ServiceId,
                    Name: p.Name,
                    Description: p.Description,
                    MonthlyPriceCents: p.MonthlyPriceCents,
                    Currency: p.Currency,
                    MonthlyCredits: p.MonthlyCredits,
                    IncludedProductIds: p.IncludedProductIds,
                    SupportTier: p.SupportTier,
                    Active: p.Active)).ToArray())))
            .WithTags("billing").Produces<BillingPlansResponse>();

        app.MapPost("/v1/billing/plans/{planId}/subscribe", async (string planId, HttpRequest request, IBillingCatalog catalog, IBillingOrchestrator billing, ITenantResolver resolver, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<PlanSubscribeRequest>(request, ct) ?? new PlanSubscribeRequest(null);
            var plan = catalog.ListPlans()
                .FirstOrDefault(p => string.Equals(p.PlanId, planId, StringComparison.OrdinalIgnoreCase));
            if (plan is null)
                return EndpointJson.BadRequest("invalid_request_error", $"Unknown plan '{planId}'.");

            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                quote = await billing.CreatePreflightQuoteAsync(tenant, plan.ServiceId, 1, ct);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }

            return Results.Json(new PlanSubscribeResponse(
                QuoteId: quote.QuoteId,
                Tenant: quote.Tenant,
                PlanId: plan.PlanId,
                ServiceId: quote.ServiceId,
                MonthlyPriceCents: plan.MonthlyPriceCents,
                AmountCents: quote.AmountCents,
                Currency: quote.Currency,
                Status: quote.Status,
                StripeCheckoutUrl: quote.StripeCheckoutUrl,
                MonthlyCredits: plan.MonthlyCredits,
                Next: new PlanNextStep(
                    quote.StripeCheckoutUrl,
                    "Plan checkout activates monthly credits when Stripe sends checkout.session.completed.")));
        })
        .WithTags("billing")
        .Accepts<PlanSubscribeRequest>("application/json")
        .Produces<PlanSubscribeResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapGet("/v1/billing/entitlements", async (HttpRequest request, IBillingEntitlementStore entitlements, ITenantResolver resolver, CancellationToken ct) =>
        {
            var tenant = (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId;
            return Results.Json(new EntitlementsResponse(tenant,
                (await entitlements.GetByTenantAsync(tenant, ct)).Select(e => new EntitlementView(
                    Tenant: e.Tenant,
                    PlanId: e.PlanId,
                    Status: e.Status,
                    PeriodStart: e.PeriodStart,
                    PeriodEnd: e.PeriodEnd,
                    MonthlyCredits: e.MonthlyCredits,
                    UsedCredits: e.UsedCredits,
                    StripeCustomerId: e.StripeCustomerId,
                    StripeSubscriptionId: e.StripeSubscriptionId,
                    UpdatedAt: e.UpdatedAt)).ToArray()));
        })
        .WithTags("billing").Produces<EntitlementsResponse>();

        app.MapPost("/v1/billing/entitlements/consume", async (HttpRequest request, IBillingEntitlementStore entitlements, ITenantResolver resolver, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<CreditConsumeRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.ServiceId))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'service_id' is required.");
            if (payload.Units < 1)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'units' must be >= 1.");

            var tenant = (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId;
            var (consumed, debit) = await entitlements.TryConsumeCreditAsync(tenant, payload.ServiceId.Trim(), payload.Units, ct);

            return Results.Json(new CreditConsumeResponse(
                Accepted: consumed,
                Tenant: debit.Tenant,
                PlanId: debit.PlanId,
                ServiceId: debit.ServiceId,
                Units: debit.Units,
                Remaining: debit.Remaining,
                PeriodEnd: debit.PeriodEnd,
                Status: debit.Status),
                statusCode: consumed ? StatusCodes.Status200OK : StatusCodes.Status402PaymentRequired);
        })
        .WithTags("billing")
        .Accepts<CreditConsumeRequest>("application/json")
        .Produces<CreditConsumeResponse>()
        .Produces<CreditConsumeResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapPost("/v1/billing/webhooks/stripe", async (HttpRequest request, IBillingWebhookHandler handler, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var payload = await reader.ReadToEndAsync(ct);
            var signature = request.Headers["Stripe-Signature"].ToString();
            var result = await handler.HandleStripeAsync(payload, signature, ct);
            return Results.Json(new WebhookResponse(
                Accepted: result.Accepted,
                Verified: result.Verified,
                Duplicate: result.Duplicate,
                EventId: result.EventId,
                EventType: result.EventType,
                Status: result.Status,
                Tenant: result.Tenant,
                ServiceId: result.ServiceId,
                QuoteId: result.QuoteId,
                PlanId: result.PlanId),
                statusCode: result.Accepted ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        })
        .WithTags("billing")
        .Produces<WebhookResponse>()
        .Produces<WebhookResponse>(StatusCodes.Status400BadRequest);

        app.MapPost("/v1/billing/catalog/sync", async (IStripeCatalogSync sync, CancellationToken ct) =>
        {
            var result = await sync.EnsureAllAsync(ct);
            return Results.Json(new CatalogSyncResponse(
                result.StripeConfigured,
                result.Entries.Select(e => new CatalogSyncEntryView(
                    ServiceId: e.ServiceId,
                    LookupKey: e.LookupKey,
                    StripePriceId: e.StripePriceId,
                    StripeProductId: e.StripeProductId,
                    Status: e.Status)).ToArray()));
        })
        .WithTags("billing").Produces<CatalogSyncResponse>();

        app.MapPost("/v1/billing/preflight", async (HttpRequest request, IBillingOrchestrator billing, ITenantResolver resolver, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<BillingPreflightRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.ServiceId))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'service_id' is required.");
            if (payload.Units < 1)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'units' must be >= 1.");

            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                quote = await billing.CreatePreflightQuoteAsync(tenant, payload.ServiceId.Trim(), payload.Units, ct);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }

            return Results.Json(new PreflightQuoteResponse(
                QuoteId: quote.QuoteId,
                Tenant: quote.Tenant,
                ServiceId: quote.ServiceId,
                Units: quote.Units,
                AmountCents: quote.AmountCents,
                Currency: quote.Currency,
                Status: quote.Status,
                ExpiresAt: quote.ExpiresAt,
                StripeCheckoutUrl: quote.StripeCheckoutUrl,
                Next: NextStep(quote, "Execution endpoints require an approved quote before execution.")));
        })
        .WithTags("billing")
        .Accepts<BillingPreflightRequest>("application/json")
        .Produces<PreflightQuoteResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapPost("/v1/billing/synthesis/quote", async (HttpRequest request, IBillingOrchestrator billing, ISynthesisQuoteCalculator calc, ITenantResolver resolver, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<SynthesisQuoteRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (payload.VocabSize < 1 || payload.HiddenSize < 1 || payload.NumLayers < 1 || payload.NumHeads < 1)
                return EndpointJson.BadRequest(
                    "invalid_request_error",
                    "Fields 'vocab_size', 'hidden_size', 'num_layers', and 'num_heads' must each be >= 1.");

            var dims = new SynthesisRecipeDimensions(
                VocabSize: payload.VocabSize,
                HiddenSize: payload.HiddenSize,
                NumLayers: payload.NumLayers,
                NumHeads: payload.NumHeads,
                NumKvHeads: payload.NumKvHeads is > 0 ? payload.NumKvHeads.Value : payload.NumHeads,
                IntermediateSize: payload.IntermediateSize > 0 ? payload.IntermediateSize : payload.HiddenSize * 4,
                TiedEmbeddings: payload.TiedEmbeddings);

            var estimate = calc.Estimate(dims);
            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                var units = (int)Math.Min(int.MaxValue, estimate.BillableUnits);
                quote = await billing.CreatePreflightQuoteAsync(tenant, "synthesis", units, ct);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }

            return Results.Json(new SynthesisQuoteResponse(
                QuoteId: quote.QuoteId,
                Tenant: quote.Tenant,
                ServiceId: quote.ServiceId,
                EstimatedParameters: estimate.Parameters,
                BillableUnits: quote.Units,
                Unit: "param_million",
                AmountCents: quote.AmountCents,
                Currency: quote.Currency,
                Status: quote.Status,
                ExpiresAt: quote.ExpiresAt,
                StripeCheckoutUrl: quote.StripeCheckoutUrl,
                Format: string.IsNullOrWhiteSpace(payload.Format) ? "gguf" : payload.Format.Trim(),
                Next: NextStep(quote, "Synthesis is dimensionality-metered: amount = base job fee + per-million-parameter rate.")));
        })
        .WithTags("billing")
        .Accepts<SynthesisQuoteRequest>("application/json")
        .Produces<SynthesisQuoteResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapPost("/v1/billing/explain/quote", async (HttpRequest request, IBillingOrchestrator billing, ITraceQuoteCalculator calc, ITenantResolver resolver, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ExplainQuoteRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (payload.Depth < 1 || payload.Beam < 1)
                return EndpointJson.BadRequest(
                    "invalid_request_error",
                    "Fields 'depth' and 'beam' must each be >= 1.");

            var estimate = calc.Estimate(new TraceReportRequest(payload.Depth, payload.Beam, payload.Academic));
            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                var units = (int)Math.Min(int.MaxValue, estimate.BillableUnits);
                quote = await billing.CreatePreflightQuoteAsync(tenant, "explain.trace", units, ct);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }

            return Results.Json(new ExplainQuoteResponse(
                QuoteId: quote.QuoteId,
                Tenant: quote.Tenant,
                ServiceId: quote.ServiceId,
                Depth: payload.Depth,
                Beam: payload.Beam,
                Academic: payload.Academic,
                EstimatedTraceNodes: estimate.TraceNodes,
                BillableUnits: quote.Units,
                Unit: "trace_unit",
                AmountCents: quote.AmountCents,
                Currency: quote.Currency,
                Status: quote.Status,
                ExpiresAt: quote.ExpiresAt,
                StripeCheckoutUrl: quote.StripeCheckoutUrl,
                Next: NextStep(quote, "Step-by-step explainability is metered by trace size (depth x beam); the academic tier expands each node with evidence provenance / citations.")));
        })
        .WithTags("billing")
        .Accepts<ExplainQuoteRequest>("application/json")
        .Produces<ExplainQuoteResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapPost("/v1/billing/audit/quote", async (HttpRequest request, IBillingOrchestrator billing, IReportQuoteCalculator calc, ITenantResolver resolver, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<AuditQuoteRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");

            var estimate = calc.EstimateAudit(new AuditReportSpec(
                Scope: string.IsNullOrWhiteSpace(payload.Scope) ? "summary" : payload.Scope.Trim(),
                IncludeEvidence: payload.IncludeEvidence,
                IncludeConsensus: payload.IncludeConsensus,
                IncludeConvergence: payload.IncludeConvergence,
                Academic: payload.Academic));
            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                var units = (int)Math.Min(int.MaxValue, estimate.BillableUnits);
                quote = await billing.CreatePreflightQuoteAsync(tenant, estimate.ServiceId, units, ct);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }

            return Results.Json(new AuditQuoteResponse(
                QuoteId: quote.QuoteId,
                Tenant: quote.Tenant,
                ServiceId: quote.ServiceId,
                Scope: string.IsNullOrWhiteSpace(payload.Scope) ? "summary" : payload.Scope.Trim(),
                Academic: payload.Academic,
                MeteredItems: estimate.MeteredItems,
                BillableUnits: quote.Units,
                Unit: estimate.UnitName,
                ItemsPerUnit: estimate.ItemsPerUnit,
                AmountCents: quote.AmountCents,
                Currency: quote.Currency,
                Status: quote.Status,
                ExpiresAt: quote.ExpiresAt,
                StripeCheckoutUrl: quote.StripeCheckoutUrl,
                Next: NextStep(quote, "Audit reports are metered by selected sections, scope breadth, and academic provenance expansion.")));
        })
        .WithTags("billing")
        .Accepts<AuditQuoteRequest>("application/json")
        .Produces<AuditQuoteResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapPost("/v1/billing/visualization/quote", async (HttpRequest request, IBillingOrchestrator billing, IReportQuoteCalculator calc, ITenantResolver resolver, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<VisualizationQuoteRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (payload.Nodes < 1 || payload.Edges < 0)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'nodes' must be >= 1 and 'edges' must be >= 0.");

            var estimate = calc.EstimateVisualization(new VisualizationExportSpec(
                Nodes: payload.Nodes,
                Edges: payload.Edges,
                IncludeGeometry: payload.IncludeGeometry,
                IncludeEvidence: payload.IncludeEvidence,
                Interactive: payload.Interactive));
            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                var units = (int)Math.Min(int.MaxValue, estimate.BillableUnits);
                quote = await billing.CreatePreflightQuoteAsync(tenant, estimate.ServiceId, units, ct);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }

            return Results.Json(new VisualizationQuoteResponse(
                QuoteId: quote.QuoteId,
                Tenant: quote.Tenant,
                ServiceId: quote.ServiceId,
                Nodes: payload.Nodes,
                Edges: payload.Edges,
                IncludeGeometry: payload.IncludeGeometry,
                IncludeEvidence: payload.IncludeEvidence,
                Interactive: payload.Interactive,
                Format: string.IsNullOrWhiteSpace(payload.Format) ? "json" : payload.Format.Trim(),
                MeteredItems: estimate.MeteredItems,
                BillableUnits: quote.Units,
                Unit: estimate.UnitName,
                ItemsPerUnit: estimate.ItemsPerUnit,
                AmountCents: quote.AmountCents,
                Currency: quote.Currency,
                Status: quote.Status,
                ExpiresAt: quote.ExpiresAt,
                StripeCheckoutUrl: quote.StripeCheckoutUrl,
                Next: NextStep(quote, "Visualization exports are metered by graph size, geometry inclusion, evidence overlays, and interactive output.")));
        })
        .WithTags("billing")
        .Accepts<VisualizationQuoteRequest>("application/json")
        .Produces<VisualizationQuoteResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapPost("/v1/billing/recipe/quote", async (HttpRequest request, IBillingOrchestrator billing, IReportQuoteCalculator calc, ITenantResolver resolver, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<RecipeQuoteRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Action))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'action' is required.");
            if (payload.ContentItems < 0)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'content_items' must be >= 0.");

            var estimate = calc.EstimateRecipe(new RecipeWorkSpec(
                Action: payload.Action,
                ContentItems: payload.ContentItems,
                Commercial: payload.Commercial,
                PrivateExport: payload.PrivateExport));
            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                var units = (int)Math.Min(int.MaxValue, estimate.BillableUnits);
                quote = await billing.CreatePreflightQuoteAsync(tenant, estimate.ServiceId, units, ct);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }

            return Results.Json(new RecipeQuoteResponse(
                QuoteId: quote.QuoteId,
                Tenant: quote.Tenant,
                ServiceId: quote.ServiceId,
                Action: payload.Action.Trim(),
                ContentItems: payload.ContentItems,
                Commercial: payload.Commercial,
                PrivateExport: payload.PrivateExport,
                MeteredItems: estimate.MeteredItems,
                BillableUnits: quote.Units,
                Unit: estimate.UnitName,
                ItemsPerUnit: estimate.ItemsPerUnit,
                AmountCents: quote.AmountCents,
                Currency: quote.Currency,
                Status: quote.Status,
                ExpiresAt: quote.ExpiresAt,
                StripeCheckoutUrl: quote.StripeCheckoutUrl,
                Next: NextStep(quote, "Recipe quotes cover publishing, access, compilation, commercial use, and private content export.")));
        })
        .WithTags("billing")
        .Accepts<RecipeQuoteRequest>("application/json")
        .Produces<RecipeQuoteResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapGet("/v1/billing/quotes/{quoteId}", async (string quoteId, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            var quote = await billing.TryGetQuoteAsync(quoteId, ct);
            if (quote is null)
                return EndpointJson.BadRequest("quote_not_found", "Quote does not exist.");

            return Results.Json(new QuoteStatusResponse(
                QuoteId: quote.QuoteId,
                Tenant: quote.Tenant,
                ServiceId: quote.ServiceId,
                Units: quote.Units,
                AmountCents: quote.AmountCents,
                Currency: quote.Currency,
                Status: quote.Status,
                Consumed: quote.Consumed,
                StripeCheckoutUrl: quote.StripeCheckoutUrl,
                ExpiresAt: quote.ExpiresAt));
        })
        .WithTags("billing")
        .Produces<QuoteStatusResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapGet("/v1/billing/usage", async (HttpRequest request, IBillingOrchestrator billing, ITenantResolver resolver, CancellationToken ct) =>
        {
            var tenant = (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId;
            var usage = await billing.GetUsageAsync(tenant, ct);
            return Results.Json(new UsageResponse(
                Tenant: tenant,
                TotalAmountCents: usage.Sum(x => x.AmountCents),
                Entries: usage.Select(u => new UsageEntry(
                    u.QuoteId, u.Tenant, u.ServiceId, u.Units, u.AmountCents, u.ExecutedAt)).ToArray()));
        })
        .WithTags("billing").Produces<UsageResponse>();
    }

    private static QuoteNextStep NextStep(BillingQuote quote, string note) =>
        new(new ExecuteHeader("X-Laplace-Quote-Id", quote.QuoteId), note);
}
