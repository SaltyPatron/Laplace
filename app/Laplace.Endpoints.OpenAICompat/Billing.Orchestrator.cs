using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Laplace.Endpoints.OpenAICompat;

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
    Task RecordRoyaltiesAsync(IReadOnlyList<BillingRoyaltyRecord> records, CancellationToken ct);
    Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesByTenantAsync(string tenant, CancellationToken ct);
    Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesBySourceAsync(string sourceIdHex, CancellationToken ct);
}

internal sealed class InMemoryBillingLedger : IBillingLedger
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<BillingUsageRecord>> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<BillingRoyaltyRecord> _royalties = new();

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

    public Task RecordRoyaltiesAsync(IReadOnlyList<BillingRoyaltyRecord> records, CancellationToken ct)
    {
        foreach (var r in records)
            _royalties.Enqueue(r);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesByTenantAsync(string tenant, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BillingRoyaltyRecord>>(
            _royalties.Where(r => string.Equals(r.SourceOwnerTenant, tenant, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(r.PayerTenant, tenant, StringComparison.OrdinalIgnoreCase))
                      .OrderByDescending(r => r.ExecutedAt)
                      .ToArray());

    public Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesBySourceAsync(string sourceIdHex, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BillingRoyaltyRecord>>(
            _royalties.Where(r => string.Equals(r.SourceIdHex, sourceIdHex, StringComparison.OrdinalIgnoreCase))
                      .OrderByDescending(r => r.ExecutedAt)
                      .ToArray());
}

internal sealed record StripeCheckoutSessionResult(bool Created, string? SessionId, string? Url, string? Reason);

internal sealed record StripeSessionDetails(
    bool Found,
    bool Paid,
    string? Tenant,
    string? ServiceId,
    string? QuoteId,
    string? CustomerId,
    string? SubscriptionId);

internal interface IStripeCheckoutGateway
{
    Task<StripeCheckoutSessionResult> CreateCheckoutSessionAsync(
        BillingQuote quote, ServicePrice price, string? stripeCustomerId, CancellationToken ct);
    Task<bool> IsSessionPaidAsync(string sessionId, CancellationToken ct);
    Task<StripeSessionDetails> TryGetSessionAsync(string sessionId, CancellationToken ct);
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

    public async Task<StripeCheckoutSessionResult> CreateCheckoutSessionAsync(
        BillingQuote quote, ServicePrice price, string? stripeCustomerId, CancellationToken ct)
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
                ClientReferenceId = quote.Tenant,
                Customer = string.IsNullOrWhiteSpace(stripeCustomerId) ? null : stripeCustomerId,
                AllowPromotionCodes = true,
                ExpiresAt = quote.ExpiresAt.UtcDateTime,
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

            var session = await service.CreateAsync(options,
                new RequestOptions { IdempotencyKey = $"laplace-checkout:{quote.QuoteId}" }, ct);
            return new StripeCheckoutSessionResult(true, session.Id, session.Url, null);
        }
        catch (StripeException ex)
        {
            Serilog.Log.ForContext<StripeCheckoutGateway>().Error(ex, "Stripe checkout creation failed for {QuoteId}.", quote.QuoteId);
            return new StripeCheckoutSessionResult(false, null, null, $"stripe_error:{ex.StripeError?.Code ?? "stripe_error"}");
        }
    }

    public async Task<bool> IsSessionPaidAsync(string sessionId, CancellationToken ct) =>
        (await TryGetSessionAsync(sessionId, ct)).Paid;

    public async Task<StripeSessionDetails> TryGetSessionAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return new StripeSessionDetails(false, false, null, null, null, null, null);

        StripeConfiguration.ApiKey = _options.ApiKey;
        try
        {
            var service = new SessionService();
            var session = await service.GetAsync(sessionId, cancellationToken: ct);
            string? Meta(string name) =>
                session.Metadata is not null && session.Metadata.TryGetValue(name, out var v) ? v : null;
            return new StripeSessionDetails(
                Found: true,
                Paid: string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase),
                Tenant: Meta("tenant"),
                ServiceId: Meta("service_id"),
                QuoteId: Meta("quote_id"),
                CustomerId: session.CustomerId,
                SubscriptionId: session.SubscriptionId);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Serilog.Log.ForContext<StripeCheckoutGateway>().Information("Stripe checkout session {SessionId} does not exist.", sessionId);
            return new StripeSessionDetails(false, false, null, null, null, null, null);
        }
        catch (StripeException ex)
        {
            Serilog.Log.ForContext<StripeCheckoutGateway>().Error(ex, "Stripe checkout session lookup failed for {SessionId}.", sessionId);
            throw;
        }
    }
}

internal sealed record QuoteExecutionGate(bool Allowed, string Code, string Message, BillingQuote? Quote);

internal interface IBillingOrchestrator
{
    Task<BillingQuote> CreatePreflightQuoteAsync(string tenant, string serviceId, int units, CancellationToken ct);
    Task<QuoteExecutionGate> EnsureExecutableAsync(string quoteId, string tenant, string serviceId, CancellationToken ct);
    Task<BillingQuote?> TryApproveQuoteAsync(string quoteId, CancellationToken ct);
    Task MarkConsumedAndRecordAsync(BillingQuote quote, CancellationToken ct);
    Task<IReadOnlyList<BillingRoyaltyRecord>> MarkConsumedAndRecordWithRoyaltiesAsync(
        BillingQuote quote, IReadOnlyList<RoyaltyAttributionShare> attributions, CancellationToken ct);
    Task<BillingQuote?> TryGetQuoteAsync(string quoteId, CancellationToken ct);
    IReadOnlyList<ServicePrice> ListCatalog();
    Task<IReadOnlyList<BillingUsageRecord>> GetUsageAsync(string tenant, CancellationToken ct);
    Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesByTenantAsync(string tenant, CancellationToken ct);
    Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesBySourceAsync(string sourceIdHex, CancellationToken ct);
}

internal sealed class BillingOrchestrator : IBillingOrchestrator
{
    private readonly IBillingCatalog _catalog;
    private readonly IBillingQuoteStore _store;
    private readonly IBillingLedger _ledger;
    private readonly IStripeCheckoutGateway _stripe;
    private readonly IBillingEntitlementStore _entitlements;
    private readonly StripeBillingOptions _options;

    public BillingOrchestrator(
        IBillingCatalog catalog,
        IBillingQuoteStore store,
        IBillingLedger ledger,
        IStripeCheckoutGateway stripe,
        IBillingEntitlementStore entitlements,
        IOptions<StripeBillingOptions> options)
    {
        _catalog = catalog;
        _store = store;
        _ledger = ledger;
        _stripe = stripe;
        _entitlements = entitlements;
        _options = options.Value;
    }

    public IReadOnlyList<ServicePrice> ListCatalog() => _catalog.List();

    public Task<IReadOnlyList<BillingUsageRecord>> GetUsageAsync(string tenant, CancellationToken ct) =>
        _ledger.GetByTenantAsync(tenant, ct);

    public Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesByTenantAsync(string tenant, CancellationToken ct) =>
        _ledger.GetRoyaltiesByTenantAsync(tenant, ct);

    public Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesBySourceAsync(string sourceIdHex, CancellationToken ct) =>
        _ledger.GetRoyaltiesBySourceAsync(sourceIdHex, ct);

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
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Consumed: false);

        quote = await _store.PutAsync(quote, ct);

        // Reuse the workspace's existing Stripe customer after cancellation or
        // plan replacement. A workspace is one payer boundary; silently creating
        // another customer on every checkout fragments invoices and portal state.
        var stripeCustomerId = (await _entitlements.GetByTenantAsync(tenant, ct))
            .Where(e => !string.IsNullOrWhiteSpace(e.StripeCustomerId))
            .OrderByDescending(e => e.UpdatedAt)
            .Select(e => e.StripeCustomerId)
            .FirstOrDefault();
        var checkout = await _stripe.CreateCheckoutSessionAsync(quote, price, stripeCustomerId, ct);
        if (!checkout.Created)
            return await _store.UpdateAsync(quote with { Status = "awaiting_manual_approval" }, ct);

        return await _store.UpdateAsync(quote with
        {
            StripeSessionId = checkout.SessionId,
            StripeCheckoutUrl = checkout.Url,
            Status = "pending_payment"
        }, ct);
    }

    public async Task<QuoteExecutionGate> EnsureExecutableAsync(string quoteId, string tenant, string serviceId, CancellationToken ct)
    {
        if (_options.Bypass)
            return new QuoteExecutionGate(true, "bypass", "Billing bypass active (local endpoint).", null);

        // No quote presented: a subscriber's monthly plan credits cover flat
        // (non-metered) services directly — one unit per request, debited atomically.
        // Metered services size their units through a quote, so credits for those
        // are redeemed against the quote below.
        if (string.IsNullOrWhiteSpace(quoteId))
        {
            if (!string.IsNullOrWhiteSpace(tenant) &&
                _catalog.TryGet(serviceId, out var flatPrice) && !flatPrice.Metered)
            {
                var (consumed, debit) = await _entitlements.TryConsumeCreditAsync(tenant, serviceId, 1, ct);
                if (consumed)
                {
                    var creditQuote = await _store.PutAsync(new BillingQuote(
                        QuoteId: $"q_credit_{Guid.NewGuid():N}",
                        Tenant: tenant,
                        ServiceId: serviceId,
                        Units: 1,
                        AmountCents: 0,
                        Currency: flatPrice.Currency,
                        Status: "approved_credit",
                        StripeSessionId: null,
                        StripeCheckoutUrl: null,
                        CreatedAt: DateTimeOffset.UtcNow,
                        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
                        Consumed: false), ct);
                    return new QuoteExecutionGate(true, "plan_credit",
                        $"Covered by plan '{debit.PlanId}' credits ({debit.Remaining} remaining).", creditQuote);
                }
            }

            return new QuoteExecutionGate(false, "quote_not_found", "Quote does not exist.", null);
        }

        var quote = await _store.TryGetAsync(quoteId, ct);
        if (quote is null || !string.Equals(quote.Tenant, tenant, StringComparison.Ordinal))
            return new QuoteExecutionGate(false, "quote_not_found", "Quote does not exist.", null);

        if (!string.Equals(quote.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
            return new QuoteExecutionGate(false, "quote_service_mismatch", "Quote is for a different service.", quote);

        if (quote.ExpiresAt <= DateTimeOffset.UtcNow)
            return new QuoteExecutionGate(false, "quote_expired", "Quote expired before execution.", quote);

        if (quote.Consumed)
            return new QuoteExecutionGate(false, "quote_already_consumed", "Quote already consumed.", quote);

        if (quote.Status is { } status &&
            (string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(status, "approved_credit", StringComparison.OrdinalIgnoreCase)))
            return new QuoteExecutionGate(true, "approved", "Quote approved.", quote);

        // Pending quote of any shape (incl. metered): plan credits redeem it in full
        // for the quoted units before we ever ask Stripe whether cash arrived.
        if (!string.IsNullOrWhiteSpace(tenant) &&
            string.Equals(quote.Tenant, tenant, StringComparison.OrdinalIgnoreCase))
        {
            var (consumed, debit) = await _entitlements.TryConsumeCreditAsync(tenant, quote.ServiceId, quote.Units, ct);
            if (consumed)
            {
                var credited = await _store.UpdateAsync(quote with { Status = "approved_credit", AmountCents = 0 }, ct);
                return new QuoteExecutionGate(true, "plan_credit",
                    $"Covered by plan '{debit.PlanId}' credits ({debit.Remaining} remaining).", credited);
            }
        }

        if (string.Equals(quote.Status, "awaiting_manual_approval", StringComparison.OrdinalIgnoreCase))
            return new QuoteExecutionGate(false, "payment_pending", "Stripe is not configured; quote is awaiting manual approval (operator: POST /v1/billing/operator/quotes/{id}/approve).", quote);

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

    public async Task<IReadOnlyList<BillingRoyaltyRecord>> MarkConsumedAndRecordWithRoyaltiesAsync(
        BillingQuote quote, IReadOnlyList<RoyaltyAttributionShare> attributions, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var consumed = await _store.UpdateAsync(quote with { Consumed = true, Status = "consumed" }, ct);
        await _ledger.RecordAsync(new BillingUsageRecord(
            QuoteId: consumed.QuoteId,
            Tenant: consumed.Tenant,
            ServiceId: consumed.ServiceId,
            Units: consumed.Units,
            AmountCents: consumed.AmountCents,
            ExecutedAt: now), ct);

        if (attributions is null || attributions.Count == 0)
            return Array.Empty<BillingRoyaltyRecord>();

        var royalties = ComputeRoyaltyShares(consumed, attributions, now);
        await _ledger.RecordRoyaltiesAsync(royalties, ct);
        return royalties;
    }

    /// <summary>
    /// Computes deterministic Mold-A-Model basis-point royalty payouts from source attribution shares.
    /// Uses largest-remainder rounding when share_bp is not yet normalized to 10000 bp, and converts
    /// basis points into exact integer cent payouts from the seeder pool (default 70% of AmountCents).
    /// </summary>
    public static IReadOnlyList<BillingRoyaltyRecord> ComputeRoyaltyShares(
        BillingQuote quote,
        IReadOnlyList<RoyaltyAttributionShare> attributions,
        DateTimeOffset executedAt,
        int seederPoolBp = 7000)
    {
        if (attributions.Count == 0)
            return Array.Empty<BillingRoyaltyRecord>();

        // Normalize weights into basis points summing to 10000 if caller did not supply pre-normalized share_bp.
        int suppliedBpSum = attributions.Sum(a => a.ShareBp);
        int[] normalizedBp = new int[attributions.Count];
        if (suppliedBpSum == 10000 && attributions.All(a => a.ShareBp >= 0))
        {
            for (int i = 0; i < attributions.Count; i++)
                normalizedBp[i] = attributions[i].ShareBp;
        }
        else
        {
            double totalWeight = attributions.Sum(a => a.TotalWeight > 0 ? a.TotalWeight : Math.Max(1, a.HitCount));
            if (totalWeight <= 0) totalWeight = attributions.Count;
            var raw = new double[attributions.Count];
            int sumFloor = 0;
            for (int i = 0; i < attributions.Count; i++)
            {
                double w = attributions[i].TotalWeight > 0 ? attributions[i].TotalWeight : Math.Max(1, attributions[i].HitCount);
                raw[i] = (w * 10000.0) / totalWeight;
                normalizedBp[i] = (int)Math.Floor(raw[i]);
                sumFloor += normalizedBp[i];
            }
            int rem = 10000 - sumFloor;
            var order = Enumerable.Range(0, attributions.Count)
                .OrderByDescending(i => raw[i] - normalizedBp[i])
                .ThenBy(i => attributions[i].SourceIdHex, StringComparer.Ordinal)
                .ToArray();
            for (int k = 0; k < rem && k < order.Length; k++)
                normalizedBp[order[k]]++;
        }

        long poolCents = (quote.AmountCents * Math.Clamp(seederPoolBp, 0, 10000)) / 10000L;
        long[] cents = new long[attributions.Count];
        long centsSum = 0;
        var centRems = new long[attributions.Count];
        for (int i = 0; i < attributions.Count; i++)
        {
            long num = poolCents * normalizedBp[i];
            cents[i] = num / 10000L;
            centRems[i] = num % 10000L;
            centsSum += cents[i];
        }
        long centLeft = poolCents - centsSum;
        if (centLeft > 0)
        {
            var centOrder = Enumerable.Range(0, attributions.Count)
                .OrderByDescending(i => centRems[i])
                .ThenByDescending(i => normalizedBp[i])
                .ThenBy(i => attributions[i].SourceIdHex, StringComparer.Ordinal)
                .ToArray();
            for (int k = 0; k < centLeft && k < centOrder.Length; k++)
                cents[centOrder[k]]++;
        }

        var results = new BillingRoyaltyRecord[attributions.Count];
        for (int i = 0; i < attributions.Count; i++)
        {
            var a = attributions[i];
            results[i] = new BillingRoyaltyRecord(
                QuoteId: quote.QuoteId,
                PayerTenant: quote.Tenant,
                ServiceId: quote.ServiceId,
                SourceIdHex: a.SourceIdHex.ToLowerInvariant(),
                SourceOwnerTenant: a.SourceOwnerTenant,
                ShareBp: normalizedBp[i],
                RoyaltyCents: cents[i],
                HitCount: a.HitCount,
                TotalWeight: a.TotalWeight,
                ExecutedAt: executedAt);
        }
        return results;
    }
}
