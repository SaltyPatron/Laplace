using System.Text.Json.Serialization;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat;

internal static class BillingEndpoints
{
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

        app.MapGet("/v1/billing/plans", (IBillingCatalog catalog, IOptions<StripeBillingOptions> stripe) =>
            Results.Json(new BillingPlansResponse("list", !stripe.Value.Bypass,
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

            var tenant = await ResolveTenantAsync(payload.Tenant, request, resolver, ct);

            var (quote, error) = await CreateMeteredQuoteAsync(billing, tenant, plan.ServiceId, 1, ct);
            if (error is not null) return error;

            return Results.Json(new PlanSubscribeResponse(
                QuoteId: quote!.QuoteId,
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
                    "Stripe Checkout starts a monthly subscription. It becomes active only after a signed Stripe event confirms the subscription; manage renewal, payment methods, invoices, and cancellation from the customer portal.")));
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

            var tenant = await ResolveTenantAsync(payload.Tenant, request, resolver, ct);

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
            var tenant = await ResolveTenantAsync(payload.Tenant, request, resolver, ct);

            var (quote, error) = await CreateMeteredQuoteAsync(billing, tenant, "synthesis", estimate.BillableUnits, ct);
            if (error is not null) return error;

            return Results.Json(new SynthesisQuoteResponse(
                QuoteId: quote!.QuoteId,
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
            var tenant = await ResolveTenantAsync(payload.Tenant, request, resolver, ct);

            var (quote, error) = await CreateMeteredQuoteAsync(billing, tenant, "explain.trace", estimate.BillableUnits, ct);
            if (error is not null) return error;

            return Results.Json(new ExplainQuoteResponse(
                QuoteId: quote!.QuoteId,
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
            var tenant = await ResolveTenantAsync(payload.Tenant, request, resolver, ct);

            var (quote, error) = await CreateMeteredQuoteAsync(billing, tenant, estimate.ServiceId, estimate.BillableUnits, ct);
            if (error is not null) return error;

            return Results.Json(new AuditQuoteResponse(
                QuoteId: quote!.QuoteId,
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
            var tenant = await ResolveTenantAsync(payload.Tenant, request, resolver, ct);

            var (quote, error) = await CreateMeteredQuoteAsync(billing, tenant, estimate.ServiceId, estimate.BillableUnits, ct);
            if (error is not null) return error;

            return Results.Json(new VisualizationQuoteResponse(
                QuoteId: quote!.QuoteId,
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
            var tenant = await ResolveTenantAsync(payload.Tenant, request, resolver, ct);

            var (quote, error) = await CreateMeteredQuoteAsync(billing, tenant, estimate.ServiceId, estimate.BillableUnits, ct);
            if (error is not null) return error;

            return Results.Json(new RecipeQuoteResponse(
                QuoteId: quote!.QuoteId,
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

        app.MapGet("/v1/billing/royalties", async (string? source_id, HttpRequest request, IBillingOrchestrator billing, ITenantResolver resolver, CancellationToken ct) =>
        {
            var tenant = (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId;
            var rows = !string.IsNullOrWhiteSpace(source_id)
                ? await billing.GetRoyaltiesBySourceAsync(source_id.Trim(), ct)
                : await billing.GetRoyaltiesByTenantAsync(tenant, ct);
            return Results.Json(new RoyaltiesResponse(
                Tenant: tenant,
                SourceId: string.IsNullOrWhiteSpace(source_id) ? null : source_id.Trim().ToLowerInvariant(),
                TotalRoyaltyCents: rows.Sum(r => r.RoyaltyCents),
                Entries: rows.Select(r => new RoyaltyEntryView(
                    QuoteId: r.QuoteId,
                    PayerTenant: r.PayerTenant,
                    ServiceId: r.ServiceId,
                    SourceId: r.SourceIdHex,
                    SourceOwnerTenant: r.SourceOwnerTenant,
                    ShareBp: r.ShareBp,
                    RoyaltyCents: r.RoyaltyCents,
                    HitCount: r.HitCount,
                    TotalWeight: r.TotalWeight,
                    ExecutedAt: r.ExecutedAt)).ToArray()));
        })
        .WithTags("billing").Produces<RoyaltiesResponse>();

        MapPatreonEndpoints(app);
        MapWitnessPayeeEndpoints(app);
        MapAuthorityPackageEndpoints(app);
    }

    private static void MapPatreonEndpoints(WebApplication app)
    {
        app.MapGet("/v1/billing/patreon/status", async (
            HttpRequest request, ITenantResolver resolver, CancellationToken ct) =>
        {
            var tenant = (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId;
            var patreon = BillingServiceFallback.GetPatreon(request.HttpContext.RequestServices)!;
            return Results.Json(await patreon.GetStatusAsync(tenant, ct));
        })
        .WithTags("billing")
        .Produces<PatreonStatusView>();

        app.MapGet("/v1/billing/patreon/connect", async (
            HttpRequest request, ITenantResolver resolver, CancellationToken ct) =>
        {
            var tenant = (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId;
            var patreon = BillingServiceFallback.GetPatreon(request.HttpContext.RequestServices)!;
            var result = patreon.CreateConnectUrl(tenant, $"{request.Scheme}://{request.Host}");
            if (!result.Configured)
            {
                return EndpointJson.ServiceUnavailable(
                    "patreon_not_configured",
                    "Patreon OAuth is not configured on this host (set LAPLACE_PATREON_CLIENT_ID and LAPLACE_PATREON_CLIENT_SECRET or deploy/secrets/patreon.env).");
            }
            return Results.Json(result);
        })
        .WithTags("billing")
        .Produces<PatreonConnectResult>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/billing/patreon/callback", async (HttpRequest request, CancellationToken ct) =>
        {
            var oauthError = request.Query["error"].ToString();
            if (!string.IsNullOrWhiteSpace(oauthError))
                return EndpointJson.BadRequest("patreon_oauth_denied", $"Patreon OAuth returned error '{oauthError}'.");

            var code = request.Query["code"].ToString();
            var state = request.Query["state"].ToString();
            var patreon = BillingServiceFallback.GetPatreon(request.HttpContext.RequestServices)!;
            var result = await patreon.HandleCallbackAsync(code, state, $"{request.Scheme}://{request.Host}", ct);
            if (!result.Success)
            {
                var errorType = result.StatusCode >= 500 ? "service_unavailable" : "invalid_request_error";
                return Results.Json(
                    new ErrorResponse(new ErrorBody(errorType, result.ErrorCode!, result.ErrorMessage!)),
                    statusCode: result.StatusCode);
            }
            return Results.Json(result);
        })
        .WithTags("billing")
        .Produces<PatreonCallbackResult>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/billing/webhooks/patreon", async (HttpRequest request, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var payload = await reader.ReadToEndAsync(ct);
            var signature = request.Headers["X-Patreon-Signature"].ToString();
            var eventType = request.Headers["X-Patreon-Event"].ToString();
            var deliveryId = request.Headers["X-Patreon-Delivery"].ToString();
            var patreon = BillingServiceFallback.GetPatreon(request.HttpContext.RequestServices)!;
            var result = await patreon.HandleWebhookAsync(payload, signature, eventType, deliveryId, ct);
            return Results.Json(result, statusCode: result.Accepted ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        })
        .WithTags("billing")
        .Produces<PatreonWebhookResult>()
        .Produces<PatreonWebhookResult>(StatusCodes.Status400BadRequest);
    }

    private static void MapWitnessPayeeEndpoints(WebApplication app)
    {
        app.MapGet("/v1/billing/payees", async (
            string? witness_id,
            string? owner_tenant,
            HttpRequest request,
            ITenantResolver resolver,
            IOptions<LaplaceAuthOptions> auth,
            CancellationToken ct) =>
        {
            var tenant = await resolver.ResolveAsync(request.HttpContext, ct);
            bool isOperator = await IsOperatorOrPlatformAdminAsync(request, auth.Value, ct);
            if (!isOperator && !string.IsNullOrWhiteSpace(owner_tenant)
                && !string.Equals(owner_tenant.Trim(), tenant.TenantId, StringComparison.Ordinal))
            {
                return AuthorizationError(StatusCodes.Status403Forbidden, "tenant_mismatch",
                    "Workspace callers can only query witness payees for their own workspace.");
            }

            var payeesStore = BillingServiceFallback.GetPayees(request.HttpContext.RequestServices)!;
            string? effectiveOwner = isOperator ? owner_tenant : tenant.TenantId;
            try
            {
                var rows = await payeesStore.ListAsync(effectiveOwner, witness_id, ct);
                return Results.Json(new WitnessPayeeListResponse(tenant.TenantId, rows));
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }
        })
        .WithTags("billing")
        .Produces<WitnessPayeeListResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);

        app.MapPost("/v1/billing/payees", async (
            HttpRequest request,
            ITenantResolver resolver,
            IOptions<LaplaceAuthOptions> auth,
            CancellationToken ct) =>
        {
            var tenant = await resolver.ResolveAsync(request.HttpContext, ct);
            bool isOperator = await IsOperatorOrPlatformAdminAsync(request, auth.Value, ct);
            if (tenant.AuthKind == "invalid_key")
                return AuthenticationError("invalid_api_key", "The provided API key is unknown, malformed, or revoked.");
            if (tenant.AuthKind is not ("browser" or "api_key" or "dev") && !isOperator)
            {
                return AuthenticationError("authentication_required",
                    "Workspace authentication or operator token is required to register witness payees.");
            }

            var payload = await EndpointJson.ReadJsonAsync<WitnessPayeeRegisterRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");

            var requestedOwner = !string.IsNullOrWhiteSpace(payload.OwnerTenant)
                ? payload.OwnerTenant.Trim()
                : payload.Tenant?.Trim();
            if (!isOperator && !string.IsNullOrWhiteSpace(requestedOwner)
                && !string.Equals(requestedOwner, tenant.TenantId, StringComparison.Ordinal))
            {
                return AuthorizationError(StatusCodes.Status403Forbidden, "tenant_mismatch",
                    "Workspace callers can only register witness payees owned by their own workspace.");
            }

            string? effectiveOwner = isOperator && !string.IsNullOrWhiteSpace(requestedOwner)
                ? requestedOwner
                : tenant.TenantId;
            var payeesStore = BillingServiceFallback.GetPayees(request.HttpContext.RequestServices)!;
            try
            {
                var saved = await payeesStore.UpsertAsync(
                    payload.WitnessId ?? string.Empty,
                    effectiveOwner,
                    payload.LicenseKind ?? "royalty_bearing",
                    payload.DisplayName,
                    ct);
                return Results.Json(saved);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }
        })
        .WithTags("billing")
        .Accepts<WitnessPayeeRegisterRequest>("application/json")
        .Produces<WitnessPayeeRecord>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
    }

    private static void MapAuthorityPackageEndpoints(WebApplication app)
    {
        app.MapGet("/v1/authority/packages", async (
            HttpRequest request,
            ITenantResolver resolver,
            IOptions<LaplaceAuthOptions> auth,
            CancellationToken ct) =>
        {
            var tenant = await resolver.ResolveAsync(request.HttpContext, ct);
            bool isOperator = await IsOperatorOrPlatformAdminAsync(request, auth.Value, ct);
            if (tenant.AuthKind == "invalid_key")
                return AuthenticationError("invalid_api_key", "The provided API key is unknown, malformed, or revoked.");
            if (auth.Value.RequiresIdentity && tenant.AuthKind is not ("api_key" or "browser" or "dev") && !isOperator)
            {
                return AuthenticationError("authentication_required",
                    "Sign in or provide a Laplace API key to inspect authority packages.");
            }

            var store = BillingServiceFallback.GetPackages(request.HttpContext.RequestServices)!;
            var items = await store.ListForTenantAsync(tenant.TenantId, includeAll: isOperator, ct);
            var views = items.Select(v => new AuthorityPackageView(
                v.Package.PackageId,
                v.Package.DisplayName,
                v.Package.Visibility,
                v.Package.WitnessIds,
                v.Package.MinTrust,
                v.Granted,
                v.Package.UpdatedAt)).ToArray();
            return Results.Json(new AuthorityPackageListResponse(tenant.TenantId, isOperator, views));
        })
        .WithTags("authority")
        .Produces<AuthorityPackageListResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);

        app.MapPost("/v1/authority/packages", async (
            HttpRequest request,
            IOptions<LaplaceAuthOptions> auth,
            CancellationToken ct) =>
        {
            if (!await IsOperatorOrPlatformAdminAsync(request, auth.Value, ct))
            {
                return AuthorizationError(StatusCodes.Status403Forbidden, "operator_token_required",
                    "Set LAPLACE_OPERATOR_TOKEN on the server and send X-Laplace-Operator-Token or sign in as a platform administrator.");
            }

            var payload = await EndpointJson.ReadJsonAsync<AuthorityPackageUpsertRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");

            var store = BillingServiceFallback.GetPackages(request.HttpContext.RequestServices)!;
            try
            {
                var pkg = await store.UpsertPackageAsync(
                    payload.PackageId ?? string.Empty,
                    payload.DisplayName ?? string.Empty,
                    payload.Visibility ?? "private",
                    payload.WitnessIds ?? Array.Empty<string>(),
                    payload.MinTrust ?? 0.0,
                    ct);
                return Results.Json(new AuthorityPackageView(
                    pkg.PackageId, pkg.DisplayName, pkg.Visibility, pkg.WitnessIds, pkg.MinTrust, true, pkg.UpdatedAt));
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }
        })
        .WithTags("authority")
        .Accepts<AuthorityPackageUpsertRequest>("application/json")
        .Produces<AuthorityPackageView>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);

        app.MapPost("/v1/authority/packages/{packageId}/grant", async (
            string packageId,
            HttpRequest request,
            IOptions<LaplaceAuthOptions> auth,
            CancellationToken ct) =>
        {
            if (!await IsOperatorOrPlatformAdminAsync(request, auth.Value, ct))
            {
                return AuthorizationError(StatusCodes.Status403Forbidden, "operator_token_required",
                    "Set LAPLACE_OPERATOR_TOKEN on the server and send X-Laplace-Operator-Token or sign in as a platform administrator.");
            }

            var payload = await EndpointJson.ReadJsonAsync<AuthorityPackageGrantRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");

            var targetTenant = !string.IsNullOrWhiteSpace(payload.TenantId)
                ? payload.TenantId
                : payload.Tenant;
            if (string.IsNullOrWhiteSpace(targetTenant))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'tenant_id' is required.");

            var store = BillingServiceFallback.GetPackages(request.HttpContext.RequestServices)!;
            var actor = PlatformPolicy.UserId(request.HttpContext)?.ToString("D") ?? "operator";
            try
            {
                var grant = await store.GrantPackageAsync(packageId, targetTenant, actor, ct);
                return Results.Json(new AuthorityPackageGrantResponse(
                    true, grant.PackageId, grant.TenantId, grant.GrantedBy, grant.GrantedAt));
            }
            catch (KeyNotFoundException ex)
            {
                return EndpointJson.NotFound("package_not_found", ex.Message);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }
        })
        .WithTags("authority")
        .Accepts<AuthorityPackageGrantRequest>("application/json")
        .Produces<AuthorityPackageGrantResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
    }

    private static async Task<bool> IsOperatorOrPlatformAdminAsync(
        HttpRequest request, LaplaceAuthOptions authOptions, CancellationToken ct)
    {
        if (OperatorAuth.IsAuthorized(request, authOptions))
            return true;
        var platformAuth = request.HttpContext.RequestServices.GetService<PlatformAuthorization>();
        if (platformAuth is null)
            return false;
        if (PlatformPolicy.UserId(request.HttpContext) is not null
            && !HttpMethods.IsGet(request.Method)
            && !ApiKeyEnforcementMiddleware.SameOrigin(request))
            return false;
        return await platformAuth.AllowsAsync(request.HttpContext, PlatformPolicy.Maintain, ct);
    }

    private static IResult AuthenticationError(string code, string message) =>
        Results.Json(new ErrorResponse(new ErrorBody("authentication_error", code, message)),
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult AuthorizationError(int statusCode, string code, string message) =>
        Results.Json(new ErrorResponse(new ErrorBody("authorization_error", code, message)),
            statusCode: statusCode);

    internal sealed record WitnessPayeeListResponse(
        [property: JsonPropertyName("tenant")] string Tenant,
        [property: JsonPropertyName("payees")] IReadOnlyList<WitnessPayeeRecord> Payees);

    internal sealed record WitnessPayeeRegisterRequest(
        [property: JsonPropertyName("witness_id")] string? WitnessId,
        [property: JsonPropertyName("owner_tenant")] string? OwnerTenant = null,
        [property: JsonPropertyName("license_kind")] string? LicenseKind = null,
        [property: JsonPropertyName("display_name")] string? DisplayName = null,
        [property: JsonPropertyName("tenant")] string? Tenant = null);

    internal sealed record AuthorityPackageUpsertRequest(
        [property: JsonPropertyName("package_id")] string? PackageId,
        [property: JsonPropertyName("display_name")] string? DisplayName = null,
        [property: JsonPropertyName("visibility")] string? Visibility = null,
        [property: JsonPropertyName("witness_ids")] IReadOnlyList<string>? WitnessIds = null,
        [property: JsonPropertyName("min_trust")] double? MinTrust = null);

    internal sealed record AuthorityPackageView(
        [property: JsonPropertyName("package_id")] string PackageId,
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("visibility")] string Visibility,
        [property: JsonPropertyName("witness_ids")] IReadOnlyList<string> WitnessIds,
        [property: JsonPropertyName("min_trust")] double MinTrust,
        [property: JsonPropertyName("granted")] bool Granted,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

    internal sealed record AuthorityPackageListResponse(
        [property: JsonPropertyName("tenant")] string Tenant,
        [property: JsonPropertyName("include_all")] bool IncludeAll,
        [property: JsonPropertyName("packages")] IReadOnlyList<AuthorityPackageView> Packages);

    internal sealed record AuthorityPackageGrantRequest(
        [property: JsonPropertyName("tenant_id")] string? TenantId = null,
        [property: JsonPropertyName("tenant")] string? Tenant = null);

    internal sealed record AuthorityPackageGrantResponse(
        [property: JsonPropertyName("granted")] bool Granted,
        [property: JsonPropertyName("package_id")] string PackageId,
        [property: JsonPropertyName("tenant_id")] string TenantId,
        [property: JsonPropertyName("granted_by")] string? GrantedBy,
        [property: JsonPropertyName("granted_at")] DateTimeOffset GrantedAt);

    private static QuoteNextStep NextStep(BillingQuote quote, string note) =>
        new(new ExecuteHeader("X-Laplace-Quote-Id", quote.QuoteId), note);






    private static async Task<string> ResolveTenantAsync(string? payloadTenant, HttpRequest request, ITenantResolver resolver, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(payloadTenant)
            ? (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId
            : payloadTenant.Trim();






    private static async Task<(BillingQuote? quote, IResult? error)> CreateMeteredQuoteAsync(
        IBillingOrchestrator billing, string tenant, string serviceId, long billableUnits, CancellationToken ct)
    {
        try
        {
            var units = (int)Math.Min(int.MaxValue, billableUnits);
            var quote = await billing.CreatePreflightQuoteAsync(tenant, serviceId, units, ct);
            return (quote, null);
        }
        catch (ArgumentException ex)
        {
            return (null, EndpointJson.BadRequest("invalid_request_error", ex.Message));
        }
    }
}
