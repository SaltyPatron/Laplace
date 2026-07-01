using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;

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
    }

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
