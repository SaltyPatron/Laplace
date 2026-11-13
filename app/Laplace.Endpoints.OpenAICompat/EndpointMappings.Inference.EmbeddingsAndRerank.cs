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
    private static void MapEmbeddingAndSearchEndpoints(WebApplication app)
    {
        app.MapPost("/v1/embeddings", async (HttpRequest request, ISubstrateClient substrate, ExploreDecomposeService decompose, IBillingOrchestrator billing, CancellationToken ct) =>
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

            // Inputs resolve concurrently in groups of maxParallel on the pooled
            // datasource; result order matches input order.
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
                var entityId = result.EntityIdHex;
                var form = result.Form;
                if (form is null)
                {
                    try
                    {
                        var trace = decompose.ComputeTrace(inputs[i]);
                        entityId ??= trace.RootIdHex;
                        form = new EmbeddingForm(
                            trace.X, trace.Y, trace.Z, trace.M,
                            Radius: trace.Radius, Constituents: trace.Constituents);
                    }
                    catch (InvalidOperationException)
                    {
                        // Leave unresolved when T0 perfcache is unavailable.
                    }
                }
                var vector = form is { } f
                    ? new double[] { f.X, f.Y, f.Z, f.M, f.Radius }
                    : Array.Empty<double>();
                data.Add(new EmbeddingData("embedding", i, vector, new EmbeddingProvenance(
                    Input: inputs[i],
                    Resolved: form is not null,
                    EntityId: entityId,
                    Form: form is { } ff
                        ? new EmbeddingFormView(ff.X, ff.Y, ff.Z, ff.M, ff.Radius, ff.Constituents)
                        : null,
                    Meaning: includeMeaning
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

        app.MapPost("/v1/rerank", async (
            HttpRequest request,
            ISubstrateClient substrate,
            ExploreDecomposeService decompose,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<RerankRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            var model = string.IsNullOrWhiteSpace(payload.Model) ? ModelCatalog.Rerank : payload.Model;
            if (!ModelCatalog.IsRerankModel(model))
                return EndpointJson.BadRequest("unknown_model",
                    $"Unknown model '{model}'. Supported rerank models: {ModelCatalog.Rerank}, {ModelCatalog.EmbedForm}, {ModelCatalog.EmbedMeaning}.");
            if (string.IsNullOrWhiteSpace(payload.Query))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'query' must be a non-empty string.");
            var docs = ReadRerankDocuments(payload.Documents);
            if (docs.Count == 0)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'documents' must be a non-empty array of strings or document objects.");

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "embeddings", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("embeddings")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));
            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            Laplace.Endpoints.Mcp.WorkspaceCodeIndex.TraceResult queryTrace;
            try
            {
                queryTrace = Laplace.Endpoints.Mcp.WorkspaceCodeIndex.ComputeTrace(payload.Query);
            }
            catch (InvalidOperationException ex)
            {
                return EndpointJson.ServiceUnavailable("rerank_unavailable", ex.Message);
            }

            var semanticPeers = await substrate.ResolveSemanticPeersAsync(queryTrace.WordIds, ct);

            var ranked = new List<RerankResultItem>(docs.Count);
            for (var i = 0; i < docs.Count; i++)
            {
                var docText = docs[i];
                var docTrace = Laplace.Endpoints.Mcp.WorkspaceCodeIndex.ComputeTrace(docText);
                var s = Laplace.Endpoints.Mcp.WorkspaceCodeIndex.ScoreCandidate(queryTrace, docTrace, payload.Query, docText, semanticPeers);

                ranked.Add(new RerankResultItem(
                    Index: i,
                    RelevanceScore: Math.Round(s.RelevanceScore, 6),
                    Document: payload.ReturnDocuments ? new RerankDocumentView(docText) : null,
                    Laplace: new RerankProvenance(
                        EntityId: docTrace.RootIdHex,
                        FrechetDistance: Math.Round(s.FrechetDistance, 6),
                        CentroidDistance: Math.Round(s.CentroidDistance, 6),
                        WordOverlap: Math.Round(s.WordOverlap, 6),
                        SharedWords: s.SharedWords,
                        QueryWords: queryTrace.WordIds.Count,
                        DocumentWords: docTrace.WordIds.Count)));
            }

            ranked.Sort(static (a, b) =>
            {
                var cmp = b.RelevanceScore.CompareTo(a.RelevanceScore);
                return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
            });

            var topN = payload.TopN is > 0 ? Math.Min(payload.TopN.Value, ranked.Count) : ranked.Count;
            if (topN < ranked.Count)
                ranked.RemoveRange(topN, ranked.Count - topN);

            var tokens = Math.Max(1, payload.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)
                + docs.Sum(static s => Math.Max(1, s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length));
            return Results.Json(new RerankResponse("list", model, queryTrace.RootIdHex, ranked, new EmbeddingsUsage(tokens, tokens)));
        })
        .WithTags("openai")
        .Accepts<RerankRequest>("application/json")
        .Produces<RerankResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/code/search", async (
            HttpRequest request,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<CodeSearchRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Query))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'query' must be a non-empty string.");

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "embeddings", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("embeddings")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));
            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            Laplace.Endpoints.Mcp.WorkspaceCodeIndex.TraceResult queryTrace;
            try
            {
                queryTrace = Laplace.Endpoints.Mcp.WorkspaceCodeIndex.ComputeTrace(payload.Query);
            }
            catch (InvalidOperationException ex)
            {
                return EndpointJson.ServiceUnavailable("code_search_unavailable", ex.Message);
            }

            var limit = Math.Clamp(payload.Limit ?? 20, 1, 200);
            var offset = Math.Max(0, payload.Offset ?? 0);
            var semanticPeers = await substrate.ResolveSemanticPeersAsync(queryTrace.WordIds, ct);
            var rows = Laplace.Endpoints.Mcp.WorkspaceCodeIndex.Search(
                payload.Query,
                payload.Path,
                limit,
                offset,
                payload.Reindex,
                out var indexedChunks,
                out var indexedFiles,
                semanticPeers);

            return Results.Json(new
            {
                @object = "list",
                query_entity_id = queryTrace.RootIdHex,
                indexed_files = indexedFiles,
                indexed_chunks = indexedChunks,
                rows,
            });
        })
        .WithTags("openai", "code")
        .Accepts<CodeSearchRequest>("application/json")
        .Produces(StatusCodes.Status200OK)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

    }

    /// <summary>
    /// The turn's provenance scope: the resolved tenant, the participant within it, and the session. The
    /// participant is the witness of the turn (Wiki Sequence 21.1): the authenticated user, else the
    /// 'user' the caller names, else the tenant itself. The session is [participant, session key]; without
    /// a client key the server mints one and returns it. The tenant is authorization and isolation, never
    /// part of what is recorded.
    /// </summary>
    internal readonly record struct TurnScope(
        string Tenant, string Participant, Guid? UserId, string SessionKey);

    private static async ValueTask<(TurnScope Scope, IResult? Error)> ResolveTurnScopeAsync(
        HttpRequest request, ITenantResolver tenantResolver,
        string? bodySessionKey, string? userKey, CancellationToken ct)
    {
        var tenantContext = await tenantResolver.ResolveAsync(request.HttpContext, ct);
        var tenant = tenantContext.TenantId;
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

        Guid? authenticatedUser = tenantContext.Claims.TryGetValue("user_id", out var claimedUser)
            && Guid.TryParse(claimedUser, out var parsedUser)
                ? parsedUser
                : null;
        if (authenticatedUser is null && userKey is not null && !ConversationContent.IsValidIdentifier(userKey))
            return (default, EndpointJson.BadRequest("invalid_user",
                "Field 'user' must match [A-Za-z0-9._@-]{1,128}."));
        string participant = authenticatedUser?.ToString("D") ?? userKey ?? tenant;

        return (new TurnScope(tenant, participant, authenticatedUser, sessionKey), null);
    }

    private static Task CatalogConversationAsync(
        IIdentityStore identityStore, TurnScope scope, string title, CancellationToken ct)
    {
        if (scope.UserId is null) return Task.CompletedTask;
        var compactTitle = string.Join(' ', title.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compactTitle.Length > 160) compactTitle = compactTitle[..160];
        return identityStore.UpsertConversationAsync(
            scope.Tenant, scope.UserId.Value, scope.SessionKey, compactTitle, ct);
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

    private static List<string> ReadRerankDocuments(JsonElement? documents)
    {
        var list = new List<string>();
        if (documents is not { } element || element.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
            {
                list.Add(s);
            }
            else if (item.ValueKind == JsonValueKind.Object
                     && item.TryGetProperty("text", out var textProp)
                     && textProp.ValueKind == JsonValueKind.String
                     && textProp.GetString() is { } t
                     && !string.IsNullOrWhiteSpace(t))
            {
                list.Add(t);
            }
        }
        return list;
    }

    private static IResult? RequireTurns(IConversationTurns turns) =>
        turns.Unavailable is { } why
            ? EndpointJson.ServiceUnavailable("turn_unavailable", "No turn can run here: " + why)
            : null;

    private static ForwardPassProof BuildForwardProof(NativeTurnReceipt turn, string sessionKey)
        => new(
            Session: sessionKey,
            Participant: turn.Participant,
            PromptId: turn.PromptId,
            ResponseId: turn.ResponseId,
            TurnId: turn.TurnId,
            Ordinal: turn.Ordinal,
            TurnsBefore: turn.TurnsBefore,
            Witnessed: turn.Witnessed,
            Reason: turn.Reason,
            Disposition: turn.Disposition ?? "",
            Steps: turn.Steps.Select(Step).ToArray(),
            Trace: turn.Trace);

    private static ChatPerformance BuildPerformance(
        string output, Stopwatch totalClock, double? firstResultMs, int? generatedTokens = null)
    {
        double elapsedMs = totalClock.Elapsed.TotalMilliseconds;
        double? tokensPerSecond = generatedTokens is { } count && elapsedMs > 0
            ? count * 1000.0 / elapsedMs
            : null;
        return new ChatPerformance(
            SubstrateMs: elapsedMs,
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
