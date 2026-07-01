using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class StripeBillingOptions
{
    public string? ApiKey { get; set; }
    public string? WebhookSecret { get; set; }
    public string Currency { get; set; } = "usd";
    public string SuccessUrl { get; set; } = "http://localhost:5187/billing/success";
    public string CancelUrl { get; set; } = "http://localhost:5187/billing/cancel";
    public bool Bypass { get; set; }


    public bool SkipSignatureVerification { get; set; }
}

internal sealed record BillingProduct(
    string ProductId,
    string Name,
    string Description,
    string Category);

internal sealed record BillingPlan(
    string PlanId,
    string ServiceId,
    string Name,
    string Description,
    long MonthlyPriceCents,
    string Currency,
    IReadOnlyDictionary<string, int> MonthlyCredits,
    IReadOnlyList<string> IncludedProductIds,
    string SupportTier,
    bool Active);

internal sealed record ServicePrice(
    string ServiceId,
    string ProductId,
    string UnitName,
    long UnitPriceCents,
    string Currency,
    string LookupKey,
    string DisplayName,
    bool Active,
    long BaseFeeCents = 0,
    bool Metered = false,
    string? RecurringInterval = null);

internal sealed record BillingQuote(
    string QuoteId,
    string Tenant,
    string ServiceId,
    int Units,
    long AmountCents,
    string Currency,
    string Status,
    string? StripeSessionId,
    string? StripeCheckoutUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool Consumed);

internal sealed record BillingUsageRecord(
    string QuoteId,
    string Tenant,
    string ServiceId,
    int Units,
    long AmountCents,
    DateTimeOffset ExecutedAt);

internal interface IBillingCatalog
{
    IReadOnlyList<BillingProduct> ListProducts();
    IReadOnlyList<BillingPlan> ListPlans();
    IReadOnlyList<ServicePrice> List();
    bool TryGet(string serviceId, out ServicePrice price);
    bool TryGetProduct(string productId, out BillingProduct product);
}

internal sealed class StaticBillingCatalog : IBillingCatalog
{
    private readonly Dictionary<string, BillingProduct> _products;
    private readonly Dictionary<string, ServicePrice> _prices;
    private readonly IReadOnlyList<BillingPlan> _plans;

    public StaticBillingCatalog(IOptions<StripeBillingOptions> options)
    {
        var currency = string.IsNullOrWhiteSpace(options.Value.Currency)
            ? "usd"
            : options.Value.Currency.Trim().ToLowerInvariant();

        _products = new(StringComparer.OrdinalIgnoreCase)
        {
            ["laplace.inference.chat"] = new(
                "laplace.inference.chat", "Laplace Chat Inference",
                "Single-shot conversational answer synthesized from consensus reads over the substrate.",
                "inference"),
            ["laplace.inference.completion"] = new(
                "laplace.inference.completion", "Laplace Text Completion",
                "Ranked-consensus continuation for a prompt.", "inference"),
            ["laplace.generation"] = new(
                "laplace.generation", "Laplace Token Generation",
                "Token-by-token generative traversal of the consensus output tree.", "generation"),
            ["laplace.embedding"] = new(
                "laplace.embedding", "Laplace Structural Embedding",
                "Native 4-D S^3 structural coordinate for content (structural, not a learned semantic vector).",
                "embedding"),
            ["laplace.search"] = new(
                "laplace.search", "Laplace Web Search",
                "Server-side web retrieval used for ephemeral prompt augmentation.", "search"),
            ["laplace.synthesis"] = new(
                "laplace.synthesis", "Laplace Substrate Synthesis",
                "Build-a-model export: render the consensus substrate into a recipe's tensor layout (your choice of vocabulary, dimensionality, heads, layers) and download the model. Metered by parameter count.",
                "synthesis"),
            ["laplace.recipe"] = new(
                "laplace.recipe", "Laplace Synthesis Recipes",
                "Author, publish, and access reusable build-a-model recipes (the architecture template that defines content scope, shape, and output format).",
                "recipe"),
            ["laplace.audit"] = new(
                "laplace.audit", "Laplace Substrate Audit",
                "Substrate accounting and dedup-health reports: entity/consensus/evidence counts, omni-source convergence, witness fan-in.",
                "audit"),
            ["laplace.inspection"] = new(
                "laplace.inspection", "Laplace Glass-Box Inspection",
                "Per-entity interpretability read: identity tier/type, S^3 physicalities, ranked-consensus neighborhood, and evidence provenance.",
                "inspection"),
            ["laplace.visualization"] = new(
                "laplace.visualization", "Laplace Substrate Visualization",
                "Rendered structural exports of a substrate neighborhood (geometry + ranked-consensus graph) for review and reporting.",
                "visualization"),
            ["laplace.neighbors"] = new(
                "laplace.neighbors", "Laplace Nearest Neighbors",
                "Plural nearest-neighbor query across the three independent axes: ranked-consensus relatedness, structural Fréchet shape, and PostGIS proximity.",
                "inference"),
            ["laplace.explainability"] = new(
                "laplace.explainability", "Laplace Explainability Trace",
                "Step-by-step 'how the answer formed' report: the token-by-token, path-by-path generation traversal with per-path consensus μ and witness fan-in. The academic tier expands every node with its evidence provenance and citations. Metered by trace size.",
                "explainability"),
            ["laplace.platform"] = new(
                "laplace.platform", "Laplace Platform Plans",
                "Monthly access bundles for teams building on Laplace: included credits across inference, synthesis, audit, visualization, recipe, and explainability services.",
                "plan"),
        };

        ServicePrice Price(string serviceId, string productId, long cents, string unit, string display,
            long baseFeeCents = 0, bool metered = false, string? recurringInterval = null) =>
            new(
                ServiceId: serviceId,
                ProductId: productId,
                UnitName: unit,
                UnitPriceCents: cents,
                Currency: currency,
                LookupKey: $"laplace_{serviceId.Replace('.', '_')}_{unit}_{currency}",
                DisplayName: display,
                Active: true,
                BaseFeeCents: baseFeeCents,
                Metered: metered,
                RecurringInterval: recurringInterval);

        _prices = new(StringComparer.OrdinalIgnoreCase)
        {
            ["chat.completions"] = Price("chat.completions", "laplace.inference.chat", 3, "request", "Laplace Chat Completion"),
            ["completions"] = Price("completions", "laplace.inference.completion", 2, "request", "Laplace Text Completion"),
            ["generate"] = Price("generate", "laplace.generation", 4, "request", "Laplace Token Generation"),
            ["embeddings"] = Price("embeddings", "laplace.embedding", 1, "request", "Laplace Embeddings"),
            ["search"] = Price("search", "laplace.search", 2, "request", "Laplace Web Search"),
            ["synthesis"] = Price("synthesis", "laplace.synthesis", 5, "param_million", "Laplace Substrate Synthesis", baseFeeCents: 250, metered: true),
            ["recipe.publish"] = Price("recipe.publish", "laplace.recipe", 100, "recipe", "Publish Synthesis Recipe"),
            ["recipe.access"] = Price("recipe.access", "laplace.recipe", 25, "use", "Access Synthesis Recipe"),
            ["audit.report"] = Price("audit.report", "laplace.audit", 50, "report", "Substrate Audit Report"),
            ["audit.deep_report"] = Price("audit.deep_report", "laplace.audit", 20, "audit_unit", "Deep Substrate Audit Report", baseFeeCents: 150, metered: true),
            ["inspect"] = Price("inspect", "laplace.inspection", 1, "entity", "Glass-Box Entity Inspection"),
            ["visualization.export"] = Price("visualization.export", "laplace.visualization", 10, "export", "Substrate Visualization Export"),
            ["visualization.deep_export"] = Price("visualization.deep_export", "laplace.visualization", 5, "visual_unit", "Deep Substrate Visualization Export", baseFeeCents: 75, metered: true),
            ["nn"] = Price("nn", "laplace.neighbors", 1, "query", "Nearest-Neighbor Query"),
            ["explain.trace"] = Price("explain.trace", "laplace.explainability", 8, "trace_unit", "Explainability Trace Report", baseFeeCents: 100, metered: true),
            ["explain.report"] = Price("explain.report", "laplace.explainability", 200, "report", "Academic Explainability Report"),
            ["recipe.compile"] = Price("recipe.compile", "laplace.recipe", 5, "recipe_unit", "Compile Synthesis Recipe", baseFeeCents: 25, metered: true),
            ["recipe.export"] = Price("recipe.export", "laplace.recipe", 10, "content_thousand", "Private Recipe Content Export", baseFeeCents: 100, metered: true),
            ["plan.developer"] = Price("plan.developer", "laplace.platform", 2900, "month", "Developer Plan", recurringInterval: "month"),
            ["plan.studio"] = Price("plan.studio", "laplace.platform", 9900, "month", "Studio Plan", recurringInterval: "month"),
            ["plan.enterprise"] = Price("plan.enterprise", "laplace.platform", 49900, "month", "Enterprise Plan", recurringInterval: "month"),
        };

        _plans = new[]
        {
            Plan(
                "developer", "plan.developer", "Developer", "Individual builder access with enough credits to prototype paid quote-gated workflows.", 2900,
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["chat.completions"] = 1000,
                    ["completions"] = 1000,
                    ["generate"] = 500,
                    ["embeddings"] = 5000,
                    ["inspect"] = 250,
                    ["explain.trace"] = 25
                },
                "community"),
            Plan(
                "studio", "plan.studio", "Studio", "Team bundle for heavier research, reporting, visualization, and model-building workloads.", 9900,
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["chat.completions"] = 10000,
                    ["completions"] = 10000,
                    ["generate"] = 5000,
                    ["embeddings"] = 50000,
                    ["inspect"] = 2500,
                    ["audit.deep_report"] = 50,
                    ["visualization.deep_export"] = 50,
                    ["explain.trace"] = 250,
                    ["synthesis"] = 500
                },
                "priority"),
            Plan(
                "enterprise", "plan.enterprise", "Enterprise", "High-throughput substrate access with large synthesis/export meters and account-level support.", 49900,
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["chat.completions"] = 100000,
                    ["completions"] = 100000,
                    ["generate"] = 50000,
                    ["embeddings"] = 500000,
                    ["inspect"] = 25000,
                    ["audit.deep_report"] = 500,
                    ["visualization.deep_export"] = 500,
                    ["explain.trace"] = 2500,
                    ["synthesis"] = 10000,
                    ["recipe.export"] = 1000
                },
                "account")
        };

        BillingPlan Plan(string planId, string serviceId, string name, string description, long monthlyPriceCents,
            IReadOnlyDictionary<string, int> credits, string supportTier) =>
            new(
                PlanId: planId,
                ServiceId: serviceId,
                Name: name,
                Description: description,
                MonthlyPriceCents: monthlyPriceCents,
                Currency: currency,
                MonthlyCredits: credits,
                IncludedProductIds: _products.Keys
                    .Where(id => !string.Equals(id, "laplace.platform", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray(),
                SupportTier: supportTier,
                Active: true);
    }

    public IReadOnlyList<BillingProduct> ListProducts() =>
        _products.Values.OrderBy(p => p.ProductId, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<BillingPlan> ListPlans() => _plans;

    public IReadOnlyList<ServicePrice> List() =>
        _prices.Values.OrderBy(x => x.ServiceId, StringComparer.Ordinal).ToArray();

    public bool TryGet(string serviceId, out ServicePrice price) => _prices.TryGetValue(serviceId, out price!);

    public bool TryGetProduct(string productId, out BillingProduct product) => _products.TryGetValue(productId, out product!);
}

internal interface IStripePriceMap
{
    Task<string?> TryGetAsync(string lookupKey, CancellationToken ct);
    Task SetAsync(string lookupKey, string stripePriceId, CancellationToken ct);
}

internal sealed class InMemoryStripePriceMap : IStripePriceMap
{
    private readonly ConcurrentDictionary<string, string> _map = new(StringComparer.Ordinal);

    public Task<string?> TryGetAsync(string lookupKey, CancellationToken ct) =>
        Task.FromResult(_map.TryGetValue(lookupKey, out var id) ? id : null);

    public Task SetAsync(string lookupKey, string stripePriceId, CancellationToken ct)
    {
        _map[lookupKey] = stripePriceId;
        return Task.CompletedTask;
    }
}

internal sealed record StripeCatalogEntryResult(
    string ServiceId,
    string LookupKey,
    string? StripePriceId,
    string? StripeProductId,
    string Status);

internal sealed record StripeCatalogSyncResult(bool StripeConfigured, IReadOnlyList<StripeCatalogEntryResult> Entries);

internal interface IStripeCatalogSync
{
    Task<StripeCatalogSyncResult> EnsureAllAsync(CancellationToken ct);
    Task<string?> EnsurePriceAsync(ServicePrice price, CancellationToken ct);
}

internal sealed class StripeCatalogSync : IStripeCatalogSync
{
    private readonly IBillingCatalog _catalog;
    private readonly IStripePriceMap _map;
    private readonly StripeBillingOptions _options;

    public StripeCatalogSync(IBillingCatalog catalog, IStripePriceMap map, IOptions<StripeBillingOptions> options)
    {
        _catalog = catalog;
        _map = map;
        _options = options.Value;
    }

    public async Task<StripeCatalogSyncResult> EnsureAllAsync(CancellationToken ct)
    {
        var configured = !string.IsNullOrWhiteSpace(_options.ApiKey);
        var entries = new List<StripeCatalogEntryResult>();

        foreach (var price in _catalog.List())
        {
            if (!configured)
            {
                entries.Add(new(price.ServiceId, price.LookupKey, null, null, "stripe_not_configured"));
                continue;
            }

            try
            {
                var (priceId, productId, status) = await EnsurePriceInternalAsync(price, ct);
                entries.Add(new(price.ServiceId, price.LookupKey, priceId, productId, status));
            }
            catch (StripeException ex)
            {
                entries.Add(new(price.ServiceId, price.LookupKey, null, null, $"error:{ex.StripeError?.Code ?? "stripe_error"}"));
            }
        }

        return new StripeCatalogSyncResult(configured, entries);
    }

    public async Task<string?> EnsurePriceAsync(ServicePrice price, CancellationToken ct)
    {
        var cached = await _map.TryGetAsync(price.LookupKey, ct);
        if (cached is not null)
            return cached;
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return null;

        try
        {
            var (priceId, _, _) = await EnsurePriceInternalAsync(price, ct);
            return priceId;
        }
        catch (StripeException)
        {
            return null;
        }
    }

    private async Task<(string priceId, string productId, string status)> EnsurePriceInternalAsync(ServicePrice price, CancellationToken ct)
    {
        StripeConfiguration.ApiKey = _options.ApiKey;

        var priceService = new PriceService();
        var existing = await priceService.ListAsync(new PriceListOptions
        {
            LookupKeys = new List<string> { price.LookupKey },
            Active = true,
            Limit = 1
        }, cancellationToken: ct);

        if (existing.Data.Count > 0)
        {
            var found = existing.Data[0];
            await _map.SetAsync(price.LookupKey, found.Id, ct);
            return (found.Id, found.ProductId, "exists");
        }

        var productId = await EnsureProductAsync(price.ProductId, ct);

        var created = await priceService.CreateAsync(new PriceCreateOptions
        {
            Product = productId,
            Currency = price.Currency,
            UnitAmount = price.UnitPriceCents,
            Nickname = price.DisplayName,
            LookupKey = price.LookupKey,
            TransferLookupKey = true,
            Recurring = price.RecurringInterval is null
                ? null
                : new PriceRecurringOptions { Interval = price.RecurringInterval },
            Metadata = new Dictionary<string, string>
            {
                ["laplace_service_id"] = price.ServiceId,
                ["laplace_unit"] = price.UnitName
            }
        }, cancellationToken: ct);

        await _map.SetAsync(price.LookupKey, created.Id, ct);
        return (created.Id, productId, "created");
    }

    private async Task<string> EnsureProductAsync(string productKey, CancellationToken ct)
    {
        _catalog.TryGetProduct(productKey, out var product);
        var productService = new ProductService();

        try
        {
            var search = await productService.SearchAsync(new ProductSearchOptions
            {
                Query = $"metadata['laplace_product_id']:'{productKey}'",
                Limit = 1
            }, cancellationToken: ct);
            if (search.Data.Count > 0)
                return search.Data[0].Id;
        }
        catch (StripeException)
        {
        }

        var createdProduct = await productService.CreateAsync(new ProductCreateOptions
        {
            Name = product?.Name ?? productKey,
            Description = product?.Description,
            Metadata = new Dictionary<string, string>
            {
                ["laplace_product_id"] = productKey,
                ["laplace_category"] = product?.Category ?? "inference"
            }
        }, cancellationToken: ct);

        return createdProduct.Id;
    }
}

internal interface IBillingQuoteStore
{
    Task<BillingQuote> PutAsync(BillingQuote quote, CancellationToken ct);
    Task<BillingQuote?> TryGetAsync(string quoteId, CancellationToken ct);
    Task<BillingQuote> UpdateAsync(BillingQuote quote, CancellationToken ct);
}

internal sealed class InMemoryBillingQuoteStore : IBillingQuoteStore
{
    private readonly ConcurrentDictionary<string, BillingQuote> _quotes = new(StringComparer.OrdinalIgnoreCase);

    public Task<BillingQuote> PutAsync(BillingQuote quote, CancellationToken ct)
    {
        _quotes[quote.QuoteId] = quote;
        return Task.FromResult(quote);
    }

    public Task<BillingQuote?> TryGetAsync(string quoteId, CancellationToken ct) =>
        Task.FromResult(_quotes.TryGetValue(quoteId, out var quote) ? quote : null);

    public Task<BillingQuote> UpdateAsync(BillingQuote quote, CancellationToken ct)
    {
        _quotes[quote.QuoteId] = quote;
        return Task.FromResult(quote);
    }
}

internal interface IBillingLedger
{
    Task RecordAsync(BillingUsageRecord record, CancellationToken ct);
    Task<IReadOnlyList<BillingUsageRecord>> GetByTenantAsync(string tenant, CancellationToken ct);
}

internal sealed class InMemoryBillingLedger : IBillingLedger
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<BillingUsageRecord>> _records = new(StringComparer.OrdinalIgnoreCase);

    public Task RecordAsync(BillingUsageRecord record, CancellationToken ct)
    {
        var queue = _records.GetOrAdd(record.Tenant, _ => new ConcurrentQueue<BillingUsageRecord>());
        queue.Enqueue(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BillingUsageRecord>> GetByTenantAsync(string tenant, CancellationToken ct)
    {
        if (!_records.TryGetValue(tenant, out var queue))
            return Task.FromResult<IReadOnlyList<BillingUsageRecord>>(Array.Empty<BillingUsageRecord>());
        return Task.FromResult<IReadOnlyList<BillingUsageRecord>>(
            queue.ToArray().OrderByDescending(r => r.ExecutedAt).ToArray());
    }
}

internal sealed record StripeCheckoutSessionResult(bool Created, string? SessionId, string? Url, string? Reason);

internal interface IStripeCheckoutGateway
{
    Task<StripeCheckoutSessionResult> CreateCheckoutSessionAsync(BillingQuote quote, ServicePrice price, CancellationToken ct);
    Task<bool> IsSessionPaidAsync(string sessionId, CancellationToken ct);
}

internal sealed class StripeCheckoutGateway : IStripeCheckoutGateway
{
    private readonly StripeBillingOptions _options;
    private readonly IStripeCatalogSync _catalogSync;

    public StripeCheckoutGateway(IOptions<StripeBillingOptions> options, IStripeCatalogSync catalogSync)
    {
        _options = options.Value;
        _catalogSync = catalogSync;
    }

    public async Task<StripeCheckoutSessionResult> CreateCheckoutSessionAsync(BillingQuote quote, ServicePrice price, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return new StripeCheckoutSessionResult(false, null, null, "stripe_not_configured");

        StripeConfiguration.ApiKey = _options.ApiKey;

        try
        {
            SessionLineItemOptions lineItem;
            if (price.Metered || price.BaseFeeCents > 0)
            {
                lineItem = new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = quote.Currency,
                        UnitAmount = quote.AmountCents,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = price.DisplayName,
                            Description = $"{price.ServiceId}: {quote.Units} {price.UnitName}"
                        }
                    }
                };
            }
            else
            {
                var stripePriceId = await _catalogSync.EnsurePriceAsync(price, ct);
                lineItem = stripePriceId is not null
                    ? new SessionLineItemOptions { Quantity = quote.Units, Price = stripePriceId }
                    : new SessionLineItemOptions
                    {
                        Quantity = quote.Units,
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            Currency = quote.Currency,
                            UnitAmount = price.UnitPriceCents,
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name = price.DisplayName,
                                Description = $"{price.ServiceId} billed per {price.UnitName}"
                            }
                        }
                    };
            }

            var service = new SessionService();
            var options = new SessionCreateOptions
            {
                Mode = price.RecurringInterval is null ? "payment" : "subscription",
                SuccessUrl = _options.SuccessUrl,
                CancelUrl = _options.CancelUrl,
                Metadata = new Dictionary<string, string>
                {
                    ["quote_id"] = quote.QuoteId,
                    ["tenant"] = quote.Tenant,
                    ["service_id"] = quote.ServiceId
                },
                SubscriptionData = price.RecurringInterval is null
                    ? null
                    : new SessionSubscriptionDataOptions
                    {
                        Metadata = new Dictionary<string, string>
                        {
                            ["quote_id"] = quote.QuoteId,
                            ["tenant"] = quote.Tenant,
                            ["service_id"] = quote.ServiceId
                        }
                    },
                LineItems = new List<SessionLineItemOptions> { lineItem }
            };

            var session = await service.CreateAsync(options, cancellationToken: ct);
            return new StripeCheckoutSessionResult(true, session.Id, session.Url, null);
        }
        catch (StripeException ex)
        {
            return new StripeCheckoutSessionResult(false, null, null, $"stripe_error:{ex.StripeError?.Code ?? "stripe_error"}");
        }
    }

    public async Task<bool> IsSessionPaidAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return false;

        StripeConfiguration.ApiKey = _options.ApiKey;
        try
        {
            var service = new SessionService();
            var session = await service.GetAsync(sessionId, cancellationToken: ct);
            return string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase);
        }
        catch (StripeException)
        {
            return false;
        }
    }
}

internal sealed record QuoteExecutionGate(bool Allowed, string Code, string Message, BillingQuote? Quote);

internal interface IBillingOrchestrator
{
    Task<BillingQuote> CreatePreflightQuoteAsync(string tenant, string serviceId, int units, CancellationToken ct);
    Task<QuoteExecutionGate> EnsureExecutableAsync(string quoteId, string serviceId, CancellationToken ct);
    Task<BillingQuote?> TryApproveQuoteAsync(string quoteId, CancellationToken ct);
    Task MarkConsumedAndRecordAsync(BillingQuote quote, CancellationToken ct);
    Task<BillingQuote?> TryGetQuoteAsync(string quoteId, CancellationToken ct);
    IReadOnlyList<ServicePrice> ListCatalog();
    Task<IReadOnlyList<BillingUsageRecord>> GetUsageAsync(string tenant, CancellationToken ct);
}

internal sealed class BillingOrchestrator : IBillingOrchestrator
{
    private readonly IBillingCatalog _catalog;
    private readonly IBillingQuoteStore _store;
    private readonly IBillingLedger _ledger;
    private readonly IStripeCheckoutGateway _stripe;
    private readonly StripeBillingOptions _options;

    public BillingOrchestrator(
        IBillingCatalog catalog,
        IBillingQuoteStore store,
        IBillingLedger ledger,
        IStripeCheckoutGateway stripe,
        IOptions<StripeBillingOptions> options)
    {
        _catalog = catalog;
        _store = store;
        _ledger = ledger;
        _stripe = stripe;
        _options = options.Value;
    }

    public IReadOnlyList<ServicePrice> ListCatalog() => _catalog.List();

    public Task<IReadOnlyList<BillingUsageRecord>> GetUsageAsync(string tenant, CancellationToken ct) =>
        _ledger.GetByTenantAsync(tenant, ct);

    public Task<BillingQuote?> TryGetQuoteAsync(string quoteId, CancellationToken ct) =>
        _store.TryGetAsync(quoteId, ct);

    public async Task<BillingQuote?> TryApproveQuoteAsync(string quoteId, CancellationToken ct)
    {
        var existing = await _store.TryGetAsync(quoteId, ct);
        if (existing is null)
            return null;

        return await _store.UpdateAsync(existing with { Status = "approved" }, ct);
    }

    public async Task<BillingQuote> CreatePreflightQuoteAsync(string tenant, string serviceId, int units, CancellationToken ct)
    {
        if (!_catalog.TryGet(serviceId, out var price))
            throw new ArgumentException($"Unknown service_id '{serviceId}'.", nameof(serviceId));
        if (units <= 0)
            throw new ArgumentException("units must be >= 1.", nameof(units));

        var quote = new BillingQuote(
            QuoteId: $"q_{Guid.NewGuid():N}",
            Tenant: tenant,
            ServiceId: serviceId,
            Units: units,
            AmountCents: checked(price.BaseFeeCents + price.UnitPriceCents * units),
            Currency: price.Currency,
            Status: "pending_payment",
            StripeSessionId: null,
            StripeCheckoutUrl: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            Consumed: false);

        quote = await _store.PutAsync(quote, ct);

        var checkout = await _stripe.CreateCheckoutSessionAsync(quote, price, ct);
        if (!checkout.Created)
            return await _store.UpdateAsync(quote with { Status = "awaiting_manual_approval" }, ct);

        return await _store.UpdateAsync(quote with
        {
            StripeSessionId = checkout.SessionId,
            StripeCheckoutUrl = checkout.Url,
            Status = "pending_payment"
        }, ct);
    }

    public async Task<QuoteExecutionGate> EnsureExecutableAsync(string quoteId, string serviceId, CancellationToken ct)
    {
        if (_options.Bypass)
            return new QuoteExecutionGate(true, "bypass", "Billing bypass active (LAPLACE_BILLING_BYPASS=true).", null);

        var quote = await _store.TryGetAsync(quoteId, ct);
        if (quote is null)
            return new QuoteExecutionGate(false, "quote_not_found", "Quote does not exist.", null);

        if (!string.Equals(quote.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
            return new QuoteExecutionGate(false, "quote_service_mismatch", "Quote is for a different service.", quote);

        if (quote.ExpiresAt <= DateTimeOffset.UtcNow)
            return new QuoteExecutionGate(false, "quote_expired", "Quote expired before execution.", quote);

        if (quote.Consumed)
            return new QuoteExecutionGate(false, "quote_already_consumed", "Quote already consumed.", quote);

        if (string.Equals(quote.Status, "approved", StringComparison.OrdinalIgnoreCase))
            return new QuoteExecutionGate(true, "approved", "Quote approved.", quote);

        if (string.Equals(quote.Status, "awaiting_manual_approval", StringComparison.OrdinalIgnoreCase))
            return new QuoteExecutionGate(false, "payment_pending", "Stripe is not configured; quote is awaiting manual approval.", quote);

        if (!string.IsNullOrWhiteSpace(quote.StripeSessionId))
        {
            var paid = await _stripe.IsSessionPaidAsync(quote.StripeSessionId, ct);
            if (paid)
            {
                var approved = await _store.UpdateAsync(quote with { Status = "approved" }, ct);
                return new QuoteExecutionGate(true, "approved", "Stripe payment verified.", approved);
            }

            return new QuoteExecutionGate(false, "payment_pending", "Stripe checkout session is not paid yet.", quote);
        }

        return new QuoteExecutionGate(false, "payment_pending", "Quote is not yet approved.", quote);
    }

    public async Task MarkConsumedAndRecordAsync(BillingQuote quote, CancellationToken ct)
    {
        var consumed = await _store.UpdateAsync(quote with { Consumed = true, Status = "consumed" }, ct);
        await _ledger.RecordAsync(new BillingUsageRecord(
            QuoteId: consumed.QuoteId,
            Tenant: consumed.Tenant,
            ServiceId: consumed.ServiceId,
            Units: consumed.Units,
            AmountCents: consumed.AmountCents,
            ExecutedAt: DateTimeOffset.UtcNow), ct);
    }
}
