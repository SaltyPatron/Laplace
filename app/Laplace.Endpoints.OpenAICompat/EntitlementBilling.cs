using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Stripe;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record BillingEntitlement(
    string Tenant, string PlanId, string Status,
    DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    IReadOnlyDictionary<string, int> MonthlyCredits,
    IReadOnlyDictionary<string, int> UsedCredits,
    string? StripeCustomerId, string? StripeSubscriptionId, DateTimeOffset UpdatedAt,
    bool CancelAtPeriodEnd = false);

internal sealed record BillingCreditDebit(
    string Tenant, string PlanId, string ServiceId, int Units, int Remaining,
    DateTimeOffset PeriodEnd, string Status);

internal sealed record BillingSubscriptionState(
    string Tenant, string SubscriptionId, string? CustomerId, string Status,
    DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    long EventCreated, bool CancelAtPeriodEnd);

internal interface IBillingEntitlementStore
{
    Task<BillingEntitlement> ActivatePlanAsync(string tenant, BillingPlan plan,
        string? stripeCustomerId, string? stripeSubscriptionId, DateTimeOffset activatedAt, CancellationToken ct);
    Task<BillingEntitlement> RenewPlanAsync(string tenant, BillingPlan plan,
        string? stripeCustomerId, string? stripeSubscriptionId, DateTimeOffset renewedAt, CancellationToken ct);
    Task<BillingEntitlement> ApplySubscriptionAsync(BillingPlan plan, BillingSubscriptionState state, CancellationToken ct);
    Task<BillingEntitlement?> DeactivateSubscriptionAsync(string stripeSubscriptionId, string status, CancellationToken ct);
    Task<IReadOnlyList<BillingEntitlement>> GetByTenantAsync(string tenant, CancellationToken ct);
    Task<(bool Consumed, BillingCreditDebit Debit)> TryConsumeCreditAsync(string tenant, string serviceId, int units, CancellationToken ct);
}

internal sealed class InMemoryBillingEntitlementStore : IBillingEntitlementStore
{
    private readonly ConcurrentDictionary<string, BillingEntitlement> _entitlements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    // These explicit-period adapters retain the existing non-provider store API.
    // Stripe events use ApplySubscriptionAsync with provider-owned boundaries.
    public Task<BillingEntitlement> ActivatePlanAsync(string tenant, BillingPlan plan,
        string? stripeCustomerId, string? stripeSubscriptionId, DateTimeOffset activatedAt, CancellationToken ct) =>
        ApplySubscriptionAsync(plan, new(tenant, stripeSubscriptionId ?? "", stripeCustomerId,
            "active", activatedAt, activatedAt.AddMonths(1), activatedAt.ToUnixTimeSeconds(), false), ct);

    public Task<BillingEntitlement> RenewPlanAsync(string tenant, BillingPlan plan,
        string? stripeCustomerId, string? stripeSubscriptionId, DateTimeOffset renewedAt, CancellationToken ct) =>
        ActivatePlanAsync(tenant, plan, stripeCustomerId, stripeSubscriptionId, renewedAt, ct);

    public Task<BillingEntitlement> ApplySubscriptionAsync(BillingPlan plan, BillingSubscriptionState state, CancellationToken ct)
    {
        if (state.PeriodEnd <= state.PeriodStart) throw new ArgumentException("Subscription period must have a positive duration.");
        lock (_gate)
        {
            var related = _entitlements.Values.Where(e => e.Tenant == state.Tenant
                && (e.PlanId == plan.PlanId || (!string.IsNullOrEmpty(state.SubscriptionId) && e.StripeSubscriptionId == state.SubscriptionId))).ToArray();
            var current = related.OrderByDescending(e => _versions.GetValueOrDefault(Key(e.Tenant, e.PlanId))).FirstOrDefault();
            if (current is not null && (_versions.GetValueOrDefault(Key(current.Tenant, current.PlanId)) > state.EventCreated
                || (current.StripeSubscriptionId == state.SubscriptionId && current.Status == "canceled" && state.Status != "canceled")))
                return Task.FromResult(current);

            var samePeriod = related.FirstOrDefault(e => e.StripeSubscriptionId == state.SubscriptionId && e.PeriodStart == state.PeriodStart);
            foreach (var old in related.Where(e => e.PlanId != plan.PlanId && e.StripeSubscriptionId == state.SubscriptionId))
            {
                _entitlements[Key(old.Tenant, old.PlanId)] = old with { Status = "replaced", UpdatedAt = DateTimeOffset.UtcNow };
                _versions[Key(old.Tenant, old.PlanId)] = state.EventCreated;
            }
            var entitlement = new BillingEntitlement(state.Tenant, plan.PlanId, state.Status,
                state.PeriodStart, state.PeriodEnd,
                new Dictionary<string, int>(plan.MonthlyCredits, StringComparer.OrdinalIgnoreCase),
                samePeriod?.UsedCredits ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                state.CustomerId ?? current?.StripeCustomerId,
                string.IsNullOrEmpty(state.SubscriptionId) ? current?.StripeSubscriptionId : state.SubscriptionId,
                DateTimeOffset.UtcNow, state.CancelAtPeriodEnd);
            _entitlements[Key(state.Tenant, plan.PlanId)] = entitlement;
            _versions[Key(state.Tenant, plan.PlanId)] = state.EventCreated;
            return Task.FromResult(entitlement);
        }
    }

    public Task<BillingEntitlement?> DeactivateSubscriptionAsync(string stripeSubscriptionId, string status, CancellationToken ct)
    {
        lock (_gate)
        {
            var current = _entitlements.Values.FirstOrDefault(e => e.StripeSubscriptionId == stripeSubscriptionId);
            if (current is null) return Task.FromResult<BillingEntitlement?>(null);
            var updated = current with { Status = status, UpdatedAt = DateTimeOffset.UtcNow };
            _entitlements[Key(updated.Tenant, updated.PlanId)] = updated;
            return Task.FromResult<BillingEntitlement?>(updated);
        }
    }

    public Task<IReadOnlyList<BillingEntitlement>> GetByTenantAsync(string tenant, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BillingEntitlement>>(_entitlements.Values
            .Where(e => string.Equals(e.Tenant, tenant, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.PlanId).ToArray());

    public Task<(bool Consumed, BillingCreditDebit Debit)> TryConsumeCreditAsync(string tenant, string serviceId, int units, CancellationToken ct)
    {
        if (units <= 0) return Task.FromResult((false, new BillingCreditDebit(tenant, "", serviceId, units, 0, DateTimeOffset.MinValue, "invalid_units")));
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var current in _entitlements.Values.Where(e => string.Equals(e.Tenant, tenant, StringComparison.OrdinalIgnoreCase)
                && e.Status == "active" && e.PeriodStart <= now && e.PeriodEnd > now)
                .OrderByDescending(e => e.MonthlyCredits.GetValueOrDefault(serviceId)))
            {
                var limit = current.MonthlyCredits.GetValueOrDefault(serviceId);
                var used = current.UsedCredits.GetValueOrDefault(serviceId);
                if (limit - used < units) continue;
                var changed = new Dictionary<string, int>(current.UsedCredits, StringComparer.OrdinalIgnoreCase) { [serviceId] = used + units };
                _entitlements[Key(current.Tenant, current.PlanId)] = current with { UsedCredits = changed, UpdatedAt = now };
                return Task.FromResult((true, new BillingCreditDebit(tenant, current.PlanId, serviceId, units, limit - used - units, current.PeriodEnd, "consumed")));
            }
        }
        return Task.FromResult((false, new BillingCreditDebit(tenant, "", serviceId, units, 0, DateTimeOffset.MinValue, "insufficient_credits")));
    }
    private static string Key(string tenant, string planId) => $"{tenant.Trim()}::{planId.Trim()}";
}

internal sealed record StripeWebhookProcessResult(
    bool Accepted, bool Verified, bool Duplicate, string? EventId, string EventType,
    string Status, string? Tenant, string? ServiceId, string? QuoteId, string? PlanId);

internal interface IBillingWebhookEventStore
{
    Task<bool> TryBeginAsync(string eventId, string eventType, CancellationToken ct);
    Task CompleteAsync(string eventId, string status, CancellationToken ct);
    Task<string?> GetStatusAsync(string eventId, CancellationToken ct);
}

internal sealed class InMemoryBillingWebhookEventStore : IBillingWebhookEventStore
{
    private readonly Dictionary<string, (string Status, DateTimeOffset Updated)> _events = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public Task<bool> TryBeginAsync(string eventId, string eventType, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_events.TryGetValue(eventId, out var previous) && previous.Status != "retryable"
                && !(previous.Status.StartsWith("processing:", StringComparison.Ordinal) && previous.Updated < DateTimeOffset.UtcNow.AddMinutes(-5)))
                return Task.FromResult(false);
            _events[eventId] = ($"processing:{eventType}", DateTimeOffset.UtcNow);
            return Task.FromResult(true);
        }
    }
    public Task CompleteAsync(string eventId, string status, CancellationToken ct)
    {
        lock (_gate) _events[eventId] = (status, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
    public Task<string?> GetStatusAsync(string eventId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_events.TryGetValue(eventId, out var value) ? value.Status : null);
    }
}

internal interface IBillingWebhookHandler
{
    Task<StripeWebhookProcessResult> HandleStripeAsync(string payload, string? signature, CancellationToken ct);
}

internal sealed class BillingWebhookHandler : IBillingWebhookHandler
{
    private readonly StripeBillingOptions _options;
    private readonly IWebhookSecretProvider _secret;
    private readonly IBillingCatalog _catalog;
    private readonly IBillingOrchestrator _billing;
    private readonly IBillingEntitlementStore _entitlements;
    private readonly IBillingWebhookEventStore _events;

    public BillingWebhookHandler(IOptions<StripeBillingOptions> options, IWebhookSecretProvider webhookSecret,
        IBillingCatalog catalog, IBillingOrchestrator billing, IBillingEntitlementStore entitlements, IBillingWebhookEventStore eventStore)
    {
        _options = options.Value; _secret = webhookSecret; _catalog = catalog;
        _billing = billing; _entitlements = entitlements; _events = eventStore;
    }

    public async Task<StripeWebhookProcessResult> HandleStripeAsync(string payload, string? signature, CancellationToken ct)
    {
        var secret = await _secret.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(secret)) return new(false, false, false, null, "unknown", "webhook_secret_unconfigured", null, null, null, null);
        var verified = !_options.SkipSignatureVerification;
        if (verified)
        {
            try { EventUtility.ConstructEvent(payload, signature, secret, throwOnApiVersionMismatch: false); }
            catch (Exception) { return new(false, false, false, null, "unknown", "invalid_signature", null, null, null, null); }
        }
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { return new(false, verified, false, null, "unknown", "invalid_json", null, null, null, null); }
        using (document)
        {
            var root = document.RootElement;
            var id = Text(root, "id");
            var type = Text(root, "type") ?? "unknown";
            if (string.IsNullOrWhiteSpace(id) || type == "unknown" || !Child(root, "created").TryGetInt64Safe(out var created))
                return new(false, verified, false, id, type, "invalid_event", null, null, null, null);
            if (!await _events.TryBeginAsync(id, type, ct))
            {
                var status = await _events.GetStatusAsync(id, ct);
                var complete = status is not null && status != "retryable" && !status.StartsWith("processing:", StringComparison.Ordinal);
                return new(complete, verified, true, id, type, complete ? "duplicate" : "processing", null, null, null, null);
            }
            try
            {
                var obj = Child(Child(root, "data"), "object");
                if (obj.ValueKind != JsonValueKind.Object)
                {
                    await _events.CompleteAsync(id, "invalid_object", ct);
                    return new(false, verified, false, id, type, "invalid_object", null, null, null, null);
                }
                var result = await ApplyAsync(id, type, created, obj, verified, ct);
                await _events.CompleteAsync(id, result.Status, ct);
                return result;
            }
            catch (Exception)
            {
                // A started record is not a completed event. Stripe can retry this
                // event after transient API/DB failure, including a process restart.
                await _events.CompleteAsync(id, "retryable", CancellationToken.None);
                throw;
            }
        }
    }

    private async Task<StripeWebhookProcessResult> ApplyAsync(string id, string type, long created,
        JsonElement obj, bool verified, CancellationToken ct)
    {
        var metadata = Child(obj, "metadata");
        var tenant = Text(metadata, "tenant");
        var serviceId = Text(metadata, "service_id");
        var quoteId = Text(metadata, "quote_id");
        var checkout = type is "checkout.session.completed" or "checkout.session.async_payment_succeeded";
        var relevant = checkout || type is "invoice.paid" or "invoice.payment_failed"
            or "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted"
            or "customer.subscription.paused" or "customer.subscription.resumed";
        if (!relevant) return new(true, verified, false, id, type, "ignored", tenant, serviceId, quoteId, null);

        if (checkout)
        {
            var paymentStatus = Text(obj, "payment_status");
            if (paymentStatus is not ("paid" or "no_payment_required"))
                return new(true, verified, false, id, type, "awaiting_payment", tenant, serviceId, quoteId, null);
            if (!string.IsNullOrWhiteSpace(quoteId))
            {
                var quote = await _billing.TryGetQuoteAsync(quoteId, ct);
                if (quote is null || quote.Tenant != tenant || quote.ServiceId != serviceId || quote.StripeSessionId != Text(obj, "id"))
                    throw new InvalidOperationException("Paid checkout does not match its recorded Laplace quote.");
                await _billing.TryApproveQuoteAsync(quoteId, ct);
            }
        }

        var subscriptionId = type.StartsWith("customer.subscription.", StringComparison.Ordinal)
            ? Text(obj, "id") : ObjectId(Child(obj, "subscription"));
        subscriptionId ??= ObjectId(Child(Child(Child(obj, "parent"), "subscription_details"), "subscription"));
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return new(true, verified, false, id, type, checkout ? "quote_approved" : "ignored", tenant, serviceId, quoteId, null);
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Stripe subscription synchronization requires its configured API credential.");

        // Fetch the current provider object rather than reactivating an account
        // from an old invoice or an out-of-order subscription event snapshot.
        var subscriptions = new SubscriptionService(new StripeClient(_options.ApiKey));
        var subscription = await subscriptions.GetAsync(subscriptionId, cancellationToken: ct);
        string? Meta(string key) => subscription.Metadata is not null && subscription.Metadata.TryGetValue(key, out var value) ? value : null;
        tenant = Meta("tenant"); serviceId = Meta("service_id");
        var plan = _catalog.ListPlans().FirstOrDefault(p => p.ServiceId == serviceId);
        if (string.IsNullOrWhiteSpace(tenant) || plan is null)
            return new(true, verified, false, id, type, "unmanaged_subscription", tenant, serviceId, quoteId, null);
        var items = subscription.Items?.Data;
        var item = items is { Count: 1 } ? items[0] : null;
        if (item is null || item.CurrentPeriodEnd <= item.CurrentPeriodStart
            || item.CurrentPeriodStart <= DateTime.UnixEpoch)
            throw new InvalidOperationException("The Laplace subscription has no unambiguous provider billing period.");
        var start = new DateTimeOffset(DateTime.SpecifyKind(item.CurrentPeriodStart, DateTimeKind.Utc));
        var end = new DateTimeOffset(DateTime.SpecifyKind(item.CurrentPeriodEnd, DateTimeKind.Utc));
        var entitlement = await _entitlements.ApplySubscriptionAsync(plan, new BillingSubscriptionState(
            tenant, subscription.Id, subscription.CustomerId, subscription.Status,
            start, end, created, subscription.CancelAtPeriodEnd), ct);
        return new(true, verified, false, id, type, $"subscription_{entitlement.Status}",
            entitlement.Tenant, plan.ServiceId, quoteId, entitlement.PlanId);
    }

    private static JsonElement Child(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;
    private static string? Text(JsonElement value, string name)
    {
        var child = Child(value, name);
        return child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    }
    private static string? ObjectId(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : Text(value, "id");
}

internal static class BillingJsonNumbers
{
    internal static bool TryGetInt64Safe(this JsonElement value, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number) && number > 0;
    }
}
