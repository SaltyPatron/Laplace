using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

internal static class ExploreEndpoints
{
    public static void MapExploreEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/explore/catalog", async (ISubstrateClient substrate, CancellationToken ct) =>
        {
            try
            {
                var catalog = await substrate.ExploreCatalogAsync(ct);
                return Results.Json(catalog);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Produces<ExploreCatalogResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/resolve", async (string? reference, ISubstrateClient substrate, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(reference))
                return EndpointJson.BadRequest("invalid_request_error", "Query parameter 'reference' is required.");

            try
            {
                var resolved = await substrate.ExploreResolveAsync(reference.Trim(), ct);
                if (resolved is null)
                    return EndpointJson.NotFound("entity_not_found", $"No entity for reference '{reference.Trim()}'.");

                return Results.Json(resolved);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Produces<ExploreResolveResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/preview", async (
            string idHex, ISubstrateClient substrate, CancellationToken ct) =>
        {
            try
            {
                var preview = await substrate.ExploreEntityPreviewAsync(idHex, ct);
                if (preview is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                return Results.Json(preview);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Produces<ExploreEntityPreviewResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // Not-found explorer: keyed by the SURFACE (a content hash can't be
        // reversed to recover "conflagurate"), so the caller passes the original
        // reference. The anchor is computed in-process; neighbours come from the
        // bound-anchor KNN. Returns 200 with navigable neighbours, never a 503,
        // for a valid-but-unwitnessed word.
        app.MapGet("/v1/explore/notfound", async (
            string? reference,
            int? geodesic_k,
            int? frechet_k,
            double? frechet_max,
            ExploreDecomposeService decompose,
            ISubstrateClient substrate,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(reference))
                return EndpointJson.BadRequest("invalid_request_error", "Query parameter 'reference' is required.");

            var surface = reference.Trim();
            try
            {
                var anchor = decompose.ComputeAnchor(surface);
                var neighbors = await substrate.ExploreAnchorNeighborsAsync(
                    anchor,
                    Math.Clamp(geodesic_k ?? 12, 1, 48),
                    Math.Clamp(frechet_k ?? 12, 1, 48),
                    Math.Clamp(frechet_max ?? 0.08, 0.0, 2.0),
                    ct);

                return Results.Json(new ExploreNotFoundResponse(
                    Reference: surface,
                    WordIdHex: anchor.WordIdHex,
                    Exists: false,
                    Coord: new[] { anchor.Cx, anchor.Cy, anchor.Cz, anchor.Cm },
                    Decomposition: anchor.Decomposition,
                    Neighbors: neighbors,
                    DidYouMean: BestSurfaceSuggestion(surface, neighbors)));
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
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
                return EndpointJson.ServiceUnavailable("decompose_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Accepts<DecomposeRequest>("application/json")
        .Produces<DecomposeResponse>()
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
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "visualization.deep_export", ct, async gateQuote =>
            {
                var graph = await substrate.ExploreConsensusGraphAsync(
                    idHex, hops ?? 2, fanout ?? 10, ct);
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

    // "Did you mean X?": the closest shape peer by surface edit distance. The
    // Frechet peers are already the shape neighbourhood; Levenshtein only
    // re-ranks that small pool, so a one-letter typo (conflagurate ->
    // conflagrate) surfaces even when it isn't the geometrically nearest curve.
    // Threshold scales with length; the reference itself never suggests itself.
    private static string? BestSurfaceSuggestion(
        string reference, IReadOnlyList<ExploreAnchorNeighborRow> neighbors)
    {
        var refLower = reference.ToLowerInvariant();
        var budget = Math.Max(2, refLower.Length / 3);
        string? best = null;
        var bestDist = int.MaxValue;

        foreach (var n in neighbors)
        {
            if (!string.Equals(n.Axis, "shape", StringComparison.Ordinal)) continue;
            var label = n.Label?.Trim();
            if (string.IsNullOrEmpty(label)) continue;
            var cand = label.ToLowerInvariant();
            if (cand == refLower) continue;

            var d = Levenshtein(refLower, cand, budget);
            if (d >= 0 && d < bestDist)
            {
                bestDist = d;
                best = label;
            }
        }
        return bestDist <= budget ? best : null;
    }

    // Bounded Levenshtein: returns -1 once every cell in a row exceeds `max`
    // (early-out for the re-rank), the true distance otherwise.
    private static int Levenshtein(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return -1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (cur[j] < rowMin) rowMin = cur[j];
            }
            if (rowMin > max) return -1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static async Task<IResult> RunGatedExploreAsync(
        HttpRequest request,
        IBillingOrchestrator billing,
        string serviceId,
        CancellationToken ct,
        Func<BillingQuote?, Task<IResult>> produce)
    {
        var gate = await QuoteGate.RequireQuoteAsync(request, billing, serviceId, ct);
        if (!gate.Allowed)
            return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null ? null : new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

        try
        {
            return await produce(gate.Quote);
        }
        catch (SubstrateUnavailableException ex)
        {
            return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
        }
    }
}
