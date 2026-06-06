using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Stripe;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record BillingEntitlement(
    string Tenant,
    string PlanId,
    string Status,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    IReadOnlyDictionary<string, int> MonthlyCredits,
    IReadOnlyDictionary<string, int> UsedCredits,
    string? StripeCustomerId,
    string? StripeSubscriptionId,
    DateTimeOffset UpdatedAt);

internal sealed record BillingCreditDebit(
    string Tenant,
    string PlanId,
    string ServiceId,
    int Units,
    int Remaining,
    DateTimeOffset PeriodEnd,
    string Status);

internal interface IBillingEntitlementStore
{
    BillingEntitlement ActivatePlan(
        string tenant,
        BillingPlan plan,
        string? stripeCustomerId,
        string? stripeSubscriptionId,
        DateTimeOffset activatedAt);

    BillingEntitlement RenewPlan(
        string tenant,
        BillingPlan plan,
        string? stripeCustomerId,
        string? stripeSubscriptionId,
        DateTimeOffset renewedAt);

    bool DeactivateSubscription(string stripeSubscriptionId, string status, out BillingEntitlement entitlement);

    IReadOnlyList<BillingEntitlement> GetByTenant(string tenant);

    bool TryConsumeCredit(string tenant, string serviceId, int units, out BillingCreditDebit debit);
}

internal sealed class InMemoryBillingEntitlementStore : IBillingEntitlementStore
{
    private readonly ConcurrentDictionary<string, BillingEntitlement> _entitlements = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public BillingEntitlement ActivatePlan(
        string tenant,
        BillingPlan plan,
        string? stripeCustomerId,
        string? stripeSubscriptionId,
        DateTimeOffset activatedAt)
    {
        var entitlement = new BillingEntitlement(
            Tenant: tenant,
            PlanId: plan.PlanId,
            Status: "active",
            PeriodStart: activatedAt,
            PeriodEnd: activatedAt.AddMonths(1),
            MonthlyCredits: new Dictionary<string, int>(plan.MonthlyCredits, StringComparer.OrdinalIgnoreCase),
            UsedCredits: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            StripeCustomerId: stripeCustomerId,
            StripeSubscriptionId: stripeSubscriptionId,
            UpdatedAt: activatedAt);

        _entitlements[Key(tenant, plan.PlanId)] = entitlement;
        return entitlement;
    }

    public BillingEntitlement RenewPlan(
        string tenant,
        BillingPlan plan,
        string? stripeCustomerId,
        string? stripeSubscriptionId,
        DateTimeOffset renewedAt)
    {
        var key = Key(tenant, plan.PlanId);
        var existing = _entitlements.TryGetValue(key, out var current) ? current : null;
        var entitlement = new BillingEntitlement(
            Tenant: tenant,
            PlanId: plan.PlanId,
            Status: "active",
            PeriodStart: renewedAt,
            PeriodEnd: renewedAt.AddMonths(1),
            MonthlyCredits: new Dictionary<string, int>(plan.MonthlyCredits, StringComparer.OrdinalIgnoreCase),
            UsedCredits: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            StripeCustomerId: stripeCustomerId ?? existing?.StripeCustomerId,
            StripeSubscriptionId: stripeSubscriptionId ?? existing?.StripeSubscriptionId,
            UpdatedAt: renewedAt);

        _entitlements[key] = entitlement;
        return entitlement;
    }

    public bool DeactivateSubscription(string stripeSubscriptionId, string status, out BillingEntitlement entitlement)
    {
        lock (_gate)
        {
            var current = _entitlements.Values.FirstOrDefault(e =>
                string.Equals(e.StripeSubscriptionId, stripeSubscriptionId, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                entitlement = default!;
                return false;
            }

            entitlement = current with
            {
                Status = status,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _entitlements[Key(entitlement.Tenant, entitlement.PlanId)] = entitlement;
            return true;
        }
    }

    public IReadOnlyList<BillingEntitlement> GetByTenant(string tenant) =>
        _entitlements.Values
            .Where(e => string.Equals(e.Tenant, tenant, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.PlanId, StringComparer.Ordinal)
            .ToArray();

    public bool TryConsumeCredit(string tenant, string serviceId, int units, out BillingCreditDebit debit)
    {
        if (units <= 0)
        {
            debit = new BillingCreditDebit(tenant, string.Empty, serviceId, units, 0, DateTimeOffset.MinValue, "invalid_units");
            return false;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var candidates = GetByTenant(tenant)
                .Where(e => string.Equals(e.Status, "active", StringComparison.OrdinalIgnoreCase) && e.PeriodEnd > now)
                .OrderByDescending(e => CreditLimit(e, serviceId))
                .ToArray();

            foreach (var entitlement in candidates)
            {
                var limit = CreditLimit(entitlement, serviceId);
                if (limit <= 0)
                    continue;

                var used = Used(entitlement, serviceId);
                var remaining = limit - used;
                if (remaining < units)
                    continue;

                var usedCredits = new Dictionary<string, int>(entitlement.UsedCredits, StringComparer.OrdinalIgnoreCase)
                {
                    [serviceId] = used + units
                };
                var updated = entitlement with
                {
                    UsedCredits = usedCredits,
                    UpdatedAt = now
                };
                _entitlements[Key(entitlement.Tenant, entitlement.PlanId)] = updated;

                debit = new BillingCreditDebit(
                    Tenant: tenant,
                    PlanId: entitlement.PlanId,
                    ServiceId: serviceId,
                    Units: units,
                    Remaining: remaining - units,
                    PeriodEnd: entitlement.PeriodEnd,
                    Status: "consumed");
                return true;
            }
        }

        debit = new BillingCreditDebit(tenant, string.Empty, serviceId, units, 0, DateTimeOffset.MinValue, "insufficient_credits");
        return false;
    }

    private static int CreditLimit(BillingEntitlement entitlement, string serviceId) =>
        entitlement.MonthlyCredits.TryGetValue(serviceId, out var limit) ? limit : 0;

    private static int Used(BillingEntitlement entitlement, string serviceId) =>
        entitlement.UsedCredits.TryGetValue(serviceId, out var used) ? used : 0;

    private static string Key(string tenant, string planId) => $"{tenant.Trim()}::{planId.Trim()}";
}

internal sealed record StripeWebhookProcessResult(
    bool Accepted,
    bool Verified,
    bool Duplicate,
    string? EventId,
    string EventType,
    string Status,
    string? Tenant,
    string? ServiceId,
    string? QuoteId,
    string? PlanId);

internal interface IBillingWebhookEventStore
{
    bool TryBegin(string eventId, string eventType);
    void Complete(string eventId, string status);
}

internal sealed class InMemoryBillingWebhookEventStore : IBillingWebhookEventStore
{
    private readonly ConcurrentDictionary<string, string> _events = new(StringComparer.OrdinalIgnoreCase);

    public bool TryBegin(string eventId, string eventType) => _events.TryAdd(eventId, $"processing:{eventType}");

    public void Complete(string eventId, string status) => _events[eventId] = status;
}

internal interface IBillingWebhookHandler
{
    Task<StripeWebhookProcessResult> HandleStripeAsync(string payload, string? signature, CancellationToken ct);
}

internal sealed class BillingWebhookHandler : IBillingWebhookHandler
{
    private readonly StripeBillingOptions _options;
    private readonly IBillingCatalog _catalog;
    private readonly IBillingOrchestrator _billing;
    private readonly IBillingEntitlementStore _entitlements;
    private readonly IBillingWebhookEventStore _eventStore;

    public BillingWebhookHandler(
        IOptions<StripeBillingOptions> options,
        IBillingCatalog catalog,
        IBillingOrchestrator billing,
        IBillingEntitlementStore entitlements,
        IBillingWebhookEventStore eventStore)
    {
        _options = options.Value;
        _catalog = catalog;
        _billing = billing;
        _entitlements = entitlements;
        _eventStore = eventStore;
    }

    public Task<StripeWebhookProcessResult> HandleStripeAsync(string payload, string? signature, CancellationToken ct)
    {
        // Fail closed: with no signing secret we cannot verify authenticity, so we
        // refuse to act on the event rather than trusting an unverified payload.
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
            return Task.FromResult(Result(false, false, false, null, "unknown", "webhook_secret_unconfigured", null, null, null, null));

        try
        {
            EventUtility.ConstructEvent(payload, signature, _options.WebhookSecret);
        }
        catch (StripeException)
        {
            return Task.FromResult(Result(false, false, false, null, "unknown", "invalid_signature", null, null, null, null));
        }

        const bool verified = true;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return Task.FromResult(Result(false, verified, false, null, "unknown", "invalid_json", null, null, null, null));
        }

        using (doc)
        {
        var root = doc.RootElement;
        var eventId = StringProperty(root, "id") ?? $"synthetic_{Guid.NewGuid():N}";
        var eventType = root.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? "unknown"
            : "unknown";

        if (!_eventStore.TryBegin(eventId, eventType))
            return Task.FromResult(Result(true, verified, true, eventId, eventType, "duplicate", null, null, null, null));

        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var obj))
        {
            _eventStore.Complete(eventId, "missing_object");
            return Task.FromResult(Result(false, verified, false, eventId, eventType, "missing_object", null, null, null, null));
        }

        var metadata = MetadataContainer(obj);
        var tenant = Metadata(metadata, "tenant");
        var serviceId = Metadata(metadata, "service_id");
        var quoteId = Metadata(metadata, "quote_id");
        var customerId = StringProperty(obj, "customer") ?? StringProperty(obj, "customer_id");
        var subscriptionId = StringProperty(obj, "subscription") ?? StringProperty(obj, "id");

        if (string.Equals(eventType, "checkout.session.completed", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(quoteId))
                _billing.TryApproveQuote(quoteId, out _);

            var plan = _catalog.ListPlans()
                .FirstOrDefault(p => string.Equals(p.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase));
            if (plan is not null && !string.IsNullOrWhiteSpace(tenant))
            {
                _entitlements.ActivatePlan(tenant, plan, customerId, subscriptionId, DateTimeOffset.UtcNow);
                _eventStore.Complete(eventId, "plan_activated");
                return Task.FromResult(Result(true, verified, false, eventId, eventType, "plan_activated", tenant, serviceId, quoteId, plan.PlanId));
            }

            _eventStore.Complete(eventId, "quote_approved");
            return Task.FromResult(Result(true, verified, false, eventId, eventType, "quote_approved", tenant, serviceId, quoteId, null));
        }

        if (string.Equals(eventType, "invoice.paid", StringComparison.OrdinalIgnoreCase))
        {
            var plan = _catalog.ListPlans()
                .FirstOrDefault(p => string.Equals(p.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase));
            if (plan is not null && !string.IsNullOrWhiteSpace(tenant))
            {
                _entitlements.RenewPlan(tenant, plan, customerId, subscriptionId, DateTimeOffset.UtcNow);
                _eventStore.Complete(eventId, "plan_renewed");
                return Task.FromResult(Result(true, verified, false, eventId, eventType, "plan_renewed", tenant, serviceId, quoteId, plan.PlanId));
            }
        }

        if (string.Equals(eventType, "customer.subscription.deleted", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(subscriptionId) && _entitlements.DeactivateSubscription(subscriptionId, "canceled", out var entitlement))
            {
                _eventStore.Complete(eventId, "plan_canceled");
                return Task.FromResult(Result(true, verified, false, eventId, eventType, "plan_canceled", entitlement.Tenant, serviceId, quoteId, entitlement.PlanId));
            }
        }

        if (string.Equals(eventType, "customer.subscription.updated", StringComparison.OrdinalIgnoreCase))
        {
            var subscriptionStatus = StringProperty(obj, "status") ?? "updated";
            if (subscriptionStatus is "canceled" or "unpaid" or "incomplete_expired" && !string.IsNullOrWhiteSpace(subscriptionId) &&
                _entitlements.DeactivateSubscription(subscriptionId, subscriptionStatus, out var entitlement))
            {
                _eventStore.Complete(eventId, $"plan_{subscriptionStatus}");
                return Task.FromResult(Result(true, verified, false, eventId, eventType, $"plan_{subscriptionStatus}", entitlement.Tenant, serviceId, quoteId, entitlement.PlanId));
            }
        }

        _eventStore.Complete(eventId, "ignored");
        return Task.FromResult(Result(true, verified, false, eventId, eventType, "ignored", tenant, serviceId, quoteId, null));
        }
    }

    private static StripeWebhookProcessResult Result(
        bool accepted,
        bool verified,
        bool duplicate,
        string? eventId,
        string eventType,
        string status,
        string? tenant,
        string? serviceId,
        string? quoteId,
        string? planId) =>
        new(accepted, verified, duplicate, eventId, eventType, status, tenant, serviceId, quoteId, planId);

    private static JsonElement MetadataContainer(JsonElement obj)
    {
        if (obj.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            return metadata;

        if (obj.TryGetProperty("subscription_details", out var subscriptionDetails) &&
            subscriptionDetails.TryGetProperty("metadata", out var subscriptionMetadata) &&
            subscriptionMetadata.ValueKind == JsonValueKind.Object)
            return subscriptionMetadata;

        return default;
    }

    private static string? Metadata(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;
        return metadata.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static string? StringProperty(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }
}
