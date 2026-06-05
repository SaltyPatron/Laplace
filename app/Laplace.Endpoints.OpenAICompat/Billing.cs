using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class StripeBillingOptions
{
    public string? ApiKey { get; set; }
    public string Currency { get; set; } = "usd";
    public string SuccessUrl { get; set; } = "http://localhost:5187/billing/success";
    public string CancelUrl { get; set; } = "http://localhost:5187/billing/cancel";
}

internal sealed record ServicePrice(string ServiceId, string UnitName, long UnitPriceCents, string DisplayName);

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
    IReadOnlyList<ServicePrice> List();
    bool TryGet(string serviceId, out ServicePrice price);
}

internal sealed class StaticBillingCatalog : IBillingCatalog
{
    private readonly Dictionary<string, ServicePrice> _prices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chat.completions"] = new("chat.completions", "request", 3, "Laplace Chat Completion"),
        ["completions"] = new("completions", "request", 2, "Laplace Text Completion"),
        ["embeddings"] = new("embeddings", "request", 1, "Laplace Embeddings (pending)")
    };

    public IReadOnlyList<ServicePrice> List() => _prices.Values.OrderBy(x => x.ServiceId, StringComparer.Ordinal).ToArray();

    public bool TryGet(string serviceId, out ServicePrice price) => _prices.TryGetValue(serviceId, out price!);
}

internal interface IBillingQuoteStore
{
    BillingQuote Put(BillingQuote quote);
    bool TryGet(string quoteId, out BillingQuote quote);
    BillingQuote Update(BillingQuote quote);
}

internal sealed class InMemoryBillingQuoteStore : IBillingQuoteStore
{
    private readonly ConcurrentDictionary<string, BillingQuote> _quotes = new(StringComparer.OrdinalIgnoreCase);

    public BillingQuote Put(BillingQuote quote)
    {
        _quotes[quote.QuoteId] = quote;
        return quote;
    }

    public bool TryGet(string quoteId, out BillingQuote quote) => _quotes.TryGetValue(quoteId, out quote!);

    public BillingQuote Update(BillingQuote quote)
    {
        _quotes[quote.QuoteId] = quote;
        return quote;
    }
}

internal interface IBillingLedger
{
    void Record(BillingUsageRecord record);
    IReadOnlyList<BillingUsageRecord> GetByTenant(string tenant);
}

internal sealed class InMemoryBillingLedger : IBillingLedger
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<BillingUsageRecord>> _records = new(StringComparer.OrdinalIgnoreCase);

    public void Record(BillingUsageRecord record)
    {
        var queue = _records.GetOrAdd(record.Tenant, _ => new ConcurrentQueue<BillingUsageRecord>());
        queue.Enqueue(record);
    }

    public IReadOnlyList<BillingUsageRecord> GetByTenant(string tenant)
    {
        if (!_records.TryGetValue(tenant, out var queue))
            return Array.Empty<BillingUsageRecord>();
        return queue.ToArray().OrderByDescending(r => r.ExecutedAt).ToArray();
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

    public StripeCheckoutGateway(IOptions<StripeBillingOptions> options)
    {
        _options = options.Value;
    }

    public async Task<StripeCheckoutSessionResult> CreateCheckoutSessionAsync(BillingQuote quote, ServicePrice price, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return new StripeCheckoutSessionResult(false, null, null, "stripe_not_configured");

        StripeConfiguration.ApiKey = _options.ApiKey;
        var service = new SessionService();
        var options = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = _options.SuccessUrl,
            CancelUrl = _options.CancelUrl,
            Metadata = new Dictionary<string, string>
            {
                ["quote_id"] = quote.QuoteId,
                ["tenant"] = quote.Tenant,
                ["service_id"] = quote.ServiceId
            },
            LineItems = new List<SessionLineItemOptions>
            {
                new()
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
                }
            }
        };

        var session = await service.CreateAsync(options, cancellationToken: ct);
        return new StripeCheckoutSessionResult(true, session.Id, session.Url, null);
    }

    public async Task<bool> IsSessionPaidAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return false;

        StripeConfiguration.ApiKey = _options.ApiKey;
        var service = new SessionService();
        var session = await service.GetAsync(sessionId, cancellationToken: ct);
        return string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record QuoteExecutionGate(bool Allowed, string Code, string Message, BillingQuote? Quote);

internal interface IBillingOrchestrator
{
    Task<BillingQuote> CreatePreflightQuoteAsync(string tenant, string serviceId, int units, CancellationToken ct);
    Task<QuoteExecutionGate> EnsureExecutableAsync(string quoteId, string serviceId, CancellationToken ct);
    void MarkConsumedAndRecord(BillingQuote quote);
    bool TryGetQuote(string quoteId, out BillingQuote quote);
    IReadOnlyList<ServicePrice> ListCatalog();
    IReadOnlyList<BillingUsageRecord> GetUsage(string tenant);
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

    public IReadOnlyList<BillingUsageRecord> GetUsage(string tenant) => _ledger.GetByTenant(tenant);

    public bool TryGetQuote(string quoteId, out BillingQuote quote) => _store.TryGet(quoteId, out quote!);

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
            AmountCents: checked(price.UnitPriceCents * units),
            Currency: _options.Currency,
            Status: "pending_payment",
            StripeSessionId: null,
            StripeCheckoutUrl: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            Consumed: false);

        quote = _store.Put(quote);

        var checkout = await _stripe.CreateCheckoutSessionAsync(quote, price, ct);
        if (!checkout.Created)
            return _store.Update(quote with { Status = "awaiting_manual_approval" });

        return _store.Update(quote with
        {
            StripeSessionId = checkout.SessionId,
            StripeCheckoutUrl = checkout.Url,
            Status = "pending_payment"
        });
    }

    public async Task<QuoteExecutionGate> EnsureExecutableAsync(string quoteId, string serviceId, CancellationToken ct)
    {
        if (!_store.TryGet(quoteId, out var quote))
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
                var approved = _store.Update(quote with { Status = "approved" });
                return new QuoteExecutionGate(true, "approved", "Stripe payment verified.", approved);
            }

            return new QuoteExecutionGate(false, "payment_pending", "Stripe checkout session is not paid yet.", quote);
        }

        return new QuoteExecutionGate(false, "payment_pending", "Quote is not yet approved.", quote);
    }

    public void MarkConsumedAndRecord(BillingQuote quote)
    {
        var consumed = _store.Update(quote with { Consumed = true, Status = "consumed" });
        _ledger.Record(new BillingUsageRecord(
            QuoteId: consumed.QuoteId,
            Tenant: consumed.Tenant,
            ServiceId: consumed.ServiceId,
            Units: consumed.Units,
            AmountCents: consumed.AmountCents,
            ExecutedAt: DateTimeOffset.UtcNow));
    }
}
