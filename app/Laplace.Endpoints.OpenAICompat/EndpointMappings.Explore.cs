using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD;

namespace Laplace.Endpoints.OpenAICompat;

internal static partial class ExploreEndpoints
{
    private static IResult Unavailable(WebApplication app, string code, InvalidOperationException exception)
    {
        app.Logger.LogError(exception, "Explore operation unavailable: {FailureCode}", code);
        return EndpointJson.ServiceUnavailable(code, exception.Message);
    }

    public static void MapExploreEndpoints(this WebApplication app)
    {
        MapMatchupEndpoints(app);

        app.MapGet("/v1/explore/browse", async (
            string? q,
            int? offset,
            int? limit,
            int? capacity,
            ExploreDecomposeService decompose,
            ISubstrateClient substrate,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return EndpointJson.BadRequest("invalid_request_error", "Query parameter 'q' is required.");

            var query = q.Trim();
            DecomposeResponse decomposition;
            try
            {
                decomposition = decompose.Decompose(query);
            }
            catch (InvalidOperationException ex)
            {
                return Unavailable(app, "decompose_unavailable", ex);
            }

            // Browse members are the word-tier ids the native text spine emitted for
            // the query, not a tokenization or substring scan of rendered text.
            var memberIds = decomposition.Nodes
                .Where(static n => n.Tier == EntityTier.Word)
                .Select(static n => n.IdHex)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var result = await substrate.ExploreBrowseAsync(
                query,
                decomposition.RootIdHex,
                memberIds,
                Math.Max(0, offset ?? 0),
                Math.Clamp(limit ?? 50, 0, 200),
                Math.Clamp(capacity ?? 2048, 0, 32768),
                ct);
            return Results.Json(result);
        })
        .RequireRateLimiting("public-query")
        .WithTags("explore")
        .Produces<ExploreBrowseResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/catalog", async (ISubstrateClient substrate, CancellationToken ct) =>
        {
            var catalog = await substrate.ExploreCatalogAsync(ct);
            return Results.Json(catalog);
        })
        .WithTags("explore")
        .Produces<ExploreCatalogResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/resolve", async (string? reference, ISubstrateClient substrate, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(reference))
                return EndpointJson.BadRequest("invalid_request_error", "Query parameter 'reference' is required.");

            var resolved = await substrate.ExploreResolveAsync(reference.Trim(), ct);
            if (resolved is null)
                return EndpointJson.NotFound("entity_not_found", $"No entity for reference '{reference.Trim()}'.");

            return Results.Json(resolved);
        })
        .WithTags("explore")
        .Produces<ExploreResolveResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/preview", async (
            string idHex, ISubstrateClient substrate, CancellationToken ct) =>
        {
            var preview = await substrate.ExploreEntityPreviewAsync(idHex, ct);
            if (preview is null)
                return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

            return Results.Json(preview);
        })
        .WithTags("explore")
        .Produces<ExploreEntityPreviewResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // An unwitnessed reference, keyed by its surface because a content id does
        // not reverse to text. Its anchor is computed in-process from content and its
        // neighbours come from the bound-anchor KNN; absence of the entity is answered
        // with navigable structure, not an error.
        app.MapGet("/v1/explore/notfound", async (
            string? reference,
            int? geodesic_k,
            int? frechet_k,
            double? frechet_max,
            int? suggestion_limit,
            ExploreDecomposeService decompose,
            ISubstrateClient substrate,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(reference))
                return EndpointJson.BadRequest("invalid_request_error", "Query parameter 'reference' is required.");

            var surface = reference.Trim();
            var anchor = decompose.ComputeAnchor(surface);
            var neighbors = await substrate.ExploreAnchorNeighborsAsync(
                anchor,
                Math.Max(0, geodesic_k ?? 12),
                Math.Max(0, frechet_k ?? 12),
                Math.Clamp(frechet_max ?? 0.5, 0.0, 2.0),
                ct);

            // Did-you-mean: the edit-distance-1 surfaces whose content id exists,
            // ranked by distance then witness count. Exact index probes, no scan.
            var refLower = surface.ToLowerInvariant();
            var witnessed = await substrate.WitnessedWordsAsync(
                EditDistance1Candidates(refLower), ct);
            var suggestionRows = witnessed
                .Select(w => (w, dist: Levenshtein(refLower, w.Surface.ToLowerInvariant(), 3)))
                .Where(x => x.dist > 0)
                .OrderBy(x => x.dist).ThenByDescending(x => x.w.Witnesses)
                .Take(Math.Max(0, suggestion_limit ?? 8))
                .Select(x => new ExploreSuggestion(x.w.Surface, x.w.IdHex, x.dist))
                .ToList();

            return Results.Json(new ExploreNotFoundResponse(
                Reference: surface,
                WordIdHex: anchor.WordIdHex,
                Exists: false,
                Coord: new[] { anchor.Cx, anchor.Cy, anchor.Cz, anchor.Cm },
                Decomposition: anchor.Decomposition,
                Neighbors: neighbors,
                Suggestions: suggestionRows,
                DidYouMean: suggestionRows.Count > 0 ? suggestionRows[0].Surface : null));
        })
        .WithTags("explore")
        .Produces<ExploreNotFoundResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}", async (
            HttpRequest request,
            string idHex,
            int? consensus_limit,
            int? evidence_limit,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "inspect", ct, async gateQuote =>
            {
                var entity = await substrate.ExploreEntityAsync(
                    idHex,
                    consensus_limit ?? 80,
                    evidence_limit ?? 40,
                    ct);
                if (entity is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreEntityDetailResponse(
                    Id: $"entity-{Guid.NewGuid():N}",
                    Object: "laplace.explore.entity",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Entity: entity,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExploreEntityDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/explore/decompose", async (
            HttpRequest request, ExploreDecomposeService decompose, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<DecomposeRequest>(request, ct);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'text' is required.");

            try
            {
                return Results.Json(decompose.Decompose(payload.Text));
            }
            catch (InvalidOperationException ex)
            {
                return Unavailable(app, "decompose_unavailable", ex);
            }
        })
        .WithTags("explore")
        .Accepts<DecomposeRequest>("application/json")
        .Produces<DecomposeResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/unicode/cloud", (ExploreDecomposeService decompose) =>
        {
            try { return Results.Json(decompose.UnicodeCloud()); }
            catch (InvalidOperationException ex) { return Unavailable(app, "unicode_cloud_unavailable", ex); }
        }).WithTags("explore").Produces<UnicodeCloudResponse>();

        app.MapGet("/v1/explore/unicode/positions/{receipt}.bin", (
            HttpContext http,
            string receipt,
            ExploreDecomposeService decompose) =>
        {
            try
            {
                UnicodeCloudResponse meta = decompose.UnicodeCloud();
                if (!string.Equals(receipt, meta.PerfcacheReceiptHex, StringComparison.OrdinalIgnoreCase))
                    return EndpointJson.NotFound("unicode_generation_not_found", "The requested T0 generation is not active.");
                http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                http.Response.Headers.ETag = $"\"{meta.PerfcacheReceiptHex}\"";
                return Results.Bytes(decompose.UnicodeCloudPositions(), "application/octet-stream");
            }
            catch (InvalidOperationException ex)
            {
                return Unavailable(app, "unicode_cloud_unavailable", ex);
            }
        }).WithTags("explore").Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");

        app.MapGet("/v1/explore/unicode/{codepoint:min(0):max(1114111)}", (uint codepoint, ExploreDecomposeService decompose) =>
        {
            try { return Results.Json(decompose.UnicodePoint(codepoint)); }
            catch (ArgumentOutOfRangeException) { return EndpointJson.NotFound("unicode_codepoint_not_found", $"U+{codepoint:X} is outside the selected Unicode window."); }
            catch (InvalidOperationException ex) { return Unavailable(app, "unicode_point_unavailable", ex); }
        }).WithTags("explore").Produces<UnicodePointResponse>();

        app.MapPost("/v1/explore/storage-proof", async (
            HttpRequest request,
            ExploreDecomposeService decompose,
            ISubstrateClient substrate,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<DecomposeRequest>(request, ct);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'text' is required.");

            try
            {
                StorageProofResponse proof = decompose.StorageProof(payload.Text);
                string? databaseReceipt = null;
                string? databaseReceiptError = null;
                try
                {
                    databaseReceipt = await substrate.PerfcacheReceiptHexAsync(ct);
                    if (string.IsNullOrWhiteSpace(databaseReceipt))
                        databaseReceiptError = "PostgreSQL returned no T0 perfcache receipt.";
                }
                catch (SubstrateUnavailableException ex)
                {
                    app.Logger.LogWarning(ex, "Storage proof could not obtain the database perfcache receipt.");
                    databaseReceiptError = ex.Message;
                }

                return Results.Json(proof with
                {
                    DatabasePerfcacheReceiptHex = databaseReceipt,
                    DatabasePerfcacheError = databaseReceiptError,
                    PerfcacheAligned = databaseReceipt is null
                        ? null
                        : string.Equals(
                            proof.PerfcacheReceiptHex,
                            databaseReceipt,
                            StringComparison.OrdinalIgnoreCase),
                });
            }
            catch (InvalidOperationException ex)
            {
                return Unavailable(app, "storage_proof_unavailable", ex);
            }
        })
        .WithTags("explore")
        .Accepts<DecomposeRequest>("application/json")
        .Produces<StorageProofResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/explore/entities/{idHex}/export", async (
            HttpRequest request,
            string idHex,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            IReportQuoteCalculator quotes,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ExploreTrainingExportRequest>(request, ct)
                ?? new ExploreTrainingExportRequest(null, null, true, true);

            return await RunGatedExploreAsync(request, billing, "recipe.export", ct, async gateQuote =>
            {
                var export = await substrate.ExploreTrainingExportAsync(
                    idHex,
                    payload.ConsensusLimit ?? 120,
                    payload.EvidenceLimit ?? 80,
                    payload.IncludeMembers,
                    payload.IncludePeers,
                    ct);
                if (export is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreTrainingExportDetailResponse(
                    Id: $"export-{Guid.NewGuid():N}",
                    Object: "laplace.explore.training_export",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Export: export,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Accepts<ExploreTrainingExportRequest>("application/json")
        .Produces<ExploreTrainingExportDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/neighbors", async (
            HttpRequest request,
            string idHex,
            int? k,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "nn", ct, async gateQuote =>
            {
                var neighbors = await substrate.ExploreNeighborsAsync(idHex, k ?? 10, ct);
                if (neighbors is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreNeighborsDetailResponse(
                    Id: $"neighbors-{Guid.NewGuid():N}",
                    Object: "laplace.explore.neighbors",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Neighbors: neighbors,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExploreNeighborsDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/members", async (
            HttpRequest request,
            string idHex,
            int? limit,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "visualization.deep_export", ct, async gateQuote =>
            {
                var members = await substrate.ExploreMembersAsync(idHex, limit ?? 100, ct);
                if (members is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreMembersDetailResponse(
                    Id: $"members-{Guid.NewGuid():N}",
                    Object: "laplace.explore.members",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Members: members,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExploreMembersDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/peers", async (
            HttpRequest request,
            string idHex,
            int? limit,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "visualization.deep_export", ct, async gateQuote =>
            {
                var peers = await substrate.ExplorePeersAsync(idHex, limit ?? 48, ct);
                if (peers is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExplorePeersDetailResponse(
                    Id: $"peers-{Guid.NewGuid():N}",
                    Object: "laplace.explore.peers",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Peers: peers,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExplorePeersDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/containers", async (
            HttpRequest request,
            string idHex,
            int? max_hops,
            int? limit,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "visualization.deep_export", ct, async gateQuote =>
            {
                var containers = await substrate.ExploreContainersAsync(
                    idHex, max_hops ?? 2, limit ?? 200, ct);
                if (containers is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreContainersDetailResponse(
                    Id: $"containers-{Guid.NewGuid():N}",
                    Object: "laplace.explore.containers",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Containers: containers,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExploreContainersDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/graph", async (
            HttpRequest request,
            string idHex,
            int? hops,
            int? fanout,
            int? max_nodes,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "visualization.deep_export", ct, async gateQuote =>
            {
                var normalizedHops = Math.Max(0, hops ?? 2);
                var normalizedFanout = Math.Max(0, fanout ?? 10);
                var derivedNodes = DeriveGraphNodeCapacity(normalizedHops, normalizedFanout);
                if (max_nodes is null && derivedNodes is null)
                    return EndpointJson.BadRequest(
                        "invalid_request_error",
                        "The derived graph node capacity exceeds the supported integer coordinate range; provide max_nodes explicitly.");

                var graph = await substrate.ExploreConsensusGraphAsync(
                    idHex, normalizedHops, normalizedFanout,
                    Math.Max(0, max_nodes ?? derivedNodes!.Value), ct);
                if (graph is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreGraphDetailResponse(
                    Id: $"graph-{Guid.NewGuid():N}",
                    Object: "laplace.explore.consensus_graph",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Graph: graph,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExploreGraphDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }
}
