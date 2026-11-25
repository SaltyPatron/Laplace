using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Laplace.Api.Contracts;
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
                    catch (InvalidOperationException ex)
                    {
                        // Without the tier-0 floor no form exists; an empty vector is not an embedding.
                        return EndpointJson.ServiceUnavailable("embedding_unavailable", ex.Message);
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
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

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

            NativeTextDecomposition.Trace queryTrace;
            try
            {
                queryTrace = NativeTextDecomposition.ComputeTrace(payload.Query);
            }
            catch (InvalidOperationException ex)
            {
                return EndpointJson.ServiceUnavailable("rerank_unavailable", ex.Message);
            }

            var semanticPeers = await substrate.ResolveSemanticPeersAsync(queryTrace.WordIds, ct);
            var salience = await substrate.ResolveWordSalienceAsync(queryTrace.WordIds, ct);

            var ranked = new List<RerankResultItem>(docs.Count);
            for (var i = 0; i < docs.Count; i++)
            {
                var docText = docs[i];
                var docTrace = NativeTextDecomposition.ComputeTrace(docText);
                var planes = ScoreCandidatePlanes(
                    payload.Query, docText, queryTrace, docTrace, semanticPeers, salience);

                ranked.Add(new RerankResultItem(
                    Index: i,
                    RelevanceScore: Math.Round(planes.WordOverlap, 6),
                    Document: payload.ReturnDocuments ? new RerankDocumentView(docText) : null,
                    Laplace: new RerankProvenance(
                        EntityId: docTrace.RootIdHex,
                        FrechetDistance: Math.Round(planes.FrechetDistance, 6),
                        CentroidDistance: Math.Round(planes.CentroidDistance, 6),
                        WordOverlap: Math.Round(planes.WordOverlap, 6),
                        SharedWords: planes.SharedWords,
                        QueryWords: queryTrace.WordIds.Length,
                        DocumentWords: docTrace.WordIds.Length)));
            }

            ranked.Sort(static (a, b) =>
            {
                var cmp = b.RelevanceScore.CompareTo(a.RelevanceScore);
                if (cmp != 0) return cmp;
                cmp = a.Laplace.FrechetDistance.CompareTo(b.Laplace.FrechetDistance);
                if (cmp != 0) return cmp;
                cmp = a.Laplace.CentroidDistance.CompareTo(b.Laplace.CentroidDistance);
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
            if (payload.Reindex)
                return EndpointJson.BadRequest("unsupported_parameter",
                    "Field 'reindex=true' is not supported over HTTP; run 'laplace ingest' on the substrate.");

            var gate = await QuoteGate.RequireQuoteAsync(request, billing, "embeddings", ct);
            if (!gate.Allowed)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                    ? new QuoteServiceDetail("embeddings")
                    : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));
            if (gate.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);

            NativeTextDecomposition.Trace queryTrace;
            try
            {
                queryTrace = NativeTextDecomposition.ComputeTrace(payload.Query);
            }
            catch (InvalidOperationException ex)
            {
                return EndpointJson.ServiceUnavailable("code_search_unavailable", ex.Message);
            }

            var limit = Math.Clamp(payload.Limit ?? 20, 1, 200);
            var offset = Math.Max(0, payload.Offset ?? 0);
            var symbols = await substrate.SearchCodeSymbolsAsync(payload.Query, payload.Path, limit, offset, ct);
            var semanticPeers = await substrate.ResolveSemanticPeersAsync(queryTrace.WordIds, ct);
            var salience = await substrate.ResolveWordSalienceAsync(queryTrace.WordIds, ct);

            var rows = symbols.Select(sym =>
            {
                var docText = $"{sym.Kind} {sym.Name}\n{sym.Snippet}";
                var docTrace = NativeTextDecomposition.ComputeTrace(docText);
                var planes = ScoreCandidatePlanes(
                    payload.Query, docText, queryTrace, docTrace, semanticPeers, salience);
                return new
                {
                    file_id = sym.FileIdHex,
                    path = sym.FilePath,
                    kind = sym.Kind,
                    name = sym.Name,
                    ordinal = sym.Ordinal,
                    language = sym.Language,
                    snippet = sym.Snippet,
                    relevance_score = Math.Round(planes.WordOverlap, 6),
                    frechet_distance = Math.Round(planes.FrechetDistance, 6),
                    centroid_distance = Math.Round(planes.CentroidDistance, 6),
                    word_overlap = Math.Round(planes.WordOverlap, 6),
                    shared_words = planes.SharedWords,
                };
            })
            .OrderByDescending(r => r.relevance_score)
            .ThenBy(r => r.frechet_distance)
            .ThenBy(r => r.centroid_distance)
            .ThenBy(r => r.path, StringComparer.Ordinal)
            .ToArray();

            return Results.Json(new
            {
                @object = "list",
                query_entity_id = queryTrace.RootIdHex,
                indexed_files = rows.Select(r => r.file_id).Distinct(StringComparer.Ordinal).Count(),
                indexed_chunks = rows.Length,
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

    private static (TurnScope Scope, IResult? Error) ResolveTurnScope(
        TenantContext tenantContext,
        HttpRequest request,
        string? bodySessionKey,
        string? userKey)
    {
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

    private static ForwardPassProof BuildForwardProof(NativeTurnReceipt turn, string sessionKey) =>
        ForwardProofHelper.Build(turn, sessionKey);

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

    private readonly record struct CandidatePlanes(
        double WordOverlap,
        double FrechetDistance,
        double CentroidDistance,
        int SharedWords);

    private static CandidatePlanes ScoreCandidatePlanes(
        string queryText,
        string docText,
        NativeTextDecomposition.Trace queryTrace,
        NativeTextDecomposition.Trace docTrace,
        IReadOnlyDictionary<Hash128, Hash128[]>? semanticPeers,
        IReadOnlyDictionary<Hash128, double>? salience)
    {
        if (string.Equals(queryText, docText, StringComparison.Ordinal))
            return new CandidatePlanes(1.0, 0.0, 0.0, queryTrace.WordCounts.Count);

        var docWordSet = docTrace.WordIds.ToHashSet();
        int directShared = 0;
        double matchedWeight = 0.0;
        double totalWeight = 0.0;

        foreach (var (qid, count) in queryTrace.WordCounts)
        {
            double info = salience is not null && salience.TryGetValue(qid, out var s) && s > 0.0 ? s : 1.0;
            double w = count * info;
            totalWeight += w;

            if (docWordSet.Contains(qid))
            {
                directShared++;
                matchedWeight += w;
                continue;
            }
            if (semanticPeers is not null && semanticPeers.TryGetValue(qid, out var peers))
            {
                foreach (var peer in peers)
                {
                    if (docWordSet.Contains(peer))
                    {
                        matchedWeight += w * 0.65;
                        break;
                    }
                }
            }
        }

        double wordOverlap = totalWeight > 0.0 ? Math.Clamp(matchedWeight / totalWeight, 0.0, 1.0) : 0.0;
        double frechetDist = NativeTextDecomposition.NativeFrechet4d(queryTrace.Trajectory, docTrace.Trajectory);
        double centroidDist = NativeTextDecomposition.NativeDistance4d(queryTrace, docTrace);
        return new CandidatePlanes(wordOverlap, frechetDist, centroidDist, directShared);
    }
}
