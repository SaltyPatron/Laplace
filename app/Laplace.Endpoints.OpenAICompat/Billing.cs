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

    /// <summary>Externally reachable base URL; enables webhook self-provisioning when set.</summary>
    public string? PublicBaseUrl { get; set; }
    public bool Bypass { get; set; }
    public bool CommercialCatalogApproved { get; set; }


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

internal sealed record RoyaltyAttributionShare(
    string SourceIdHex,
    int ShareBp,
    long HitCount,
    double TotalWeight,
    string? SourceOwnerTenant = null);

internal sealed record BillingRoyaltyRecord(
    string QuoteId,
    string PayerTenant,
    string ServiceId,
    string SourceIdHex,
    string? SourceOwnerTenant,
    int ShareBp,
    long RoyaltyCents,
    long HitCount,
    double TotalWeight,
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
                "Mold-A-Model export: render the consensus substrate into a recipe's tensor layout (your choice of vocabulary, dimensionality, heads, layers) and download the model. Metered by parameter count.",
                "synthesis"),
            ["laplace.recipe"] = new(
                "laplace.recipe", "Laplace Synthesis Recipes",
                "Author, publish, and access reusable Mold-A-Model recipes (the architecture template that defines content scope, shape, and output format).",
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
    private readonly ConcurrentDictionary<string, string> _productIds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _productGate = new(1, 1);

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

        if (configured && IsLiveKey(_options.ApiKey) && !_options.CommercialCatalogApproved)
            return new(true, _catalog.List().Select(price => new StripeCatalogEntryResult(
                price.ServiceId, price.LookupKey, null, null, "commercial_catalog_not_approved")).ToArray());

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
                Serilog.Log.ForContext<StripeCatalogSync>().Error(ex,
                    "Stripe catalog sync failed for {ServiceId} / {LookupKey}.", price.ServiceId, price.LookupKey);
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
        if (IsLiveKey(_options.ApiKey) && !_options.CommercialCatalogApproved)
            return null;

        try
        {
            var (priceId, _, _) = await EnsurePriceInternalAsync(price, ct);
            return priceId;
        }
        catch (StripeException ex)
        {
            Serilog.Log.ForContext<StripeCatalogSync>().Error(ex, "Stripe price resolution failed for {LookupKey}.", price.LookupKey);
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
        }, new RequestOptions { IdempotencyKey = $"laplace-price:{price.LookupKey}" }, ct);

        await _map.SetAsync(price.LookupKey, created.Id, ct);
        return (created.Id, productId, "created");
    }

    private async Task<string> EnsureProductAsync(string productKey, CancellationToken ct)
    {
        if (_productIds.TryGetValue(productKey, out var cached)) return cached;
        await _productGate.WaitAsync(ct);
        try
        {
            if (_productIds.TryGetValue(productKey, out cached)) return cached;
            var resolved = await EnsureProductCoreAsync(productKey, ct);
            _productIds[productKey] = resolved;
            return resolved;
        }
        finally { _productGate.Release(); }
    }

    private async Task<string> EnsureProductCoreAsync(string productKey, CancellationToken ct)
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
        catch (StripeException ex)
        {
            Serilog.Log.ForContext<StripeCatalogSync>().Error(ex,
                "Stripe product search failed for {ProductKey}; refusing to create a possible duplicate.", productKey);
            throw;
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
        }, new RequestOptions { IdempotencyKey = $"laplace-product:{productKey}" }, ct);

        return createdProduct.Id;
    }

    private static bool IsLiveKey(string? key) =>
        key?.StartsWith("sk_live_", StringComparison.Ordinal) == true
        || key?.StartsWith("rk_live_", StringComparison.Ordinal) == true;
}
