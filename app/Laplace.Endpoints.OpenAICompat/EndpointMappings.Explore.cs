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
