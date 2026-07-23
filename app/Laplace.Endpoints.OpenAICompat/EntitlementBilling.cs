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
    Task<BillingEntitlement> ActivatePlanAsync(
        string tenant,
        BillingPlan plan,
        string? stripeCustomerId,
        string? stripeSubscriptionId,
        DateTimeOffset activatedAt,
        CancellationToken ct);

    Task<BillingEntitlement> RenewPlanAsync(
        string tenant,
        BillingPlan plan,
        string? stripeCustomerId,
        string? stripeSubscriptionId,
        DateTimeOffset renewedAt,
        CancellationToken ct);


    Task<BillingEntitlement?> DeactivateSubscriptionAsync(string stripeSubscriptionId, string status, CancellationToken ct);

    Task<IReadOnlyList<BillingEntitlement>> GetByTenantAsync(string tenant, CancellationToken ct);

    Task<(bool Consumed, BillingCreditDebit Debit)> TryConsumeCreditAsync(
        string tenant, string serviceId, int units, CancellationToken ct);
}

internal sealed class InMemoryBillingEntitlementStore : IBillingEntitlementStore
{
    private readonly ConcurrentDictionary<string, BillingEntitlement> _entitlements = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public Task<BillingEntitlement> ActivatePlanAsync(
        string tenant,
        BillingPlan plan,
        string? stripeCustomerId,
        string? stripeSubscriptionId,
        DateTimeOffset activatedAt,
        CancellationToken ct)
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
        return Task.FromResult(entitlement);
    }

    public Task<BillingEntitlement> RenewPlanAsync(
        string tenant,
        BillingPlan plan,
        string? stripeCustomerId,
        string? stripeSubscriptionId,
        DateTimeOffset renewedAt,
        CancellationToken ct)
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
        return Task.FromResult(entitlement);
    }

    public Task<BillingEntitlement?> DeactivateSubscriptionAsync(string stripeSubscriptionId, string status, CancellationToken ct)
    {
        lock (_gate)
        {
            var current = _entitlements.Values.FirstOrDefault(e =>
                string.Equals(e.StripeSubscriptionId, stripeSubscriptionId, StringComparison.OrdinalIgnoreCase));
            if (current is null)
                return Task.FromResult<BillingEntitlement?>(null);

            var entitlement = current with
            {
                Status = status,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _entitlements[Key(entitlement.Tenant, entitlement.PlanId)] = entitlement;
            return Task.FromResult<BillingEntitlement?>(entitlement);
        }
    }

    public Task<IReadOnlyList<BillingEntitlement>> GetByTenantAsync(string tenant, CancellationToken ct) =>
        Task.FromResult(GetByTenant(tenant));

    private IReadOnlyList<BillingEntitlement> GetByTenant(string tenant) =>
        _entitlements.Values
            .Where(e => string.Equals(e.Tenant, tenant, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.PlanId, StringComparer.Ordinal)
            .ToArray();

    public Task<(bool Consumed, BillingCreditDebit Debit)> TryConsumeCreditAsync(
        string tenant, string serviceId, int units, CancellationToken ct)
    {
        if (units <= 0)
            return Task.FromResult((false,
                new BillingCreditDebit(tenant, string.Empty, serviceId, units, 0, DateTimeOffset.MinValue, "invalid_units")));

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

                return Task.FromResult((true, new BillingCreditDebit(
                    Tenant: tenant,
                    PlanId: entitlement.PlanId,
                    ServiceId: serviceId,
                    Units: units,
                    Remaining: remaining - units,
                    PeriodEnd: entitlement.PeriodEnd,
                    Status: "consumed")));
            }
        }

        return Task.FromResult((false,
            new BillingCreditDebit(tenant, string.Empty, serviceId, units, 0, DateTimeOffset.MinValue, "insufficient_credits")));
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
    Task<bool> TryBeginAsync(string eventId, string eventType, CancellationToken ct);
    Task CompleteAsync(string eventId, string status, CancellationToken ct);
}

internal sealed class InMemoryBillingWebhookEventStore : IBillingWebhookEventStore
{
    private readonly ConcurrentDictionary<string, string> _events = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> TryBeginAsync(string eventId, string eventType, CancellationToken ct) =>
        Task.FromResult(_events.TryAdd(eventId, $"processing:{eventType}"));

    public Task CompleteAsync(string eventId, string status, CancellationToken ct)
    {
        _events[eventId] = status;
        return Task.CompletedTask;
    }
}

internal interface IBillingWebhookHandler
{
    Task<StripeWebhookProcessResult> HandleStripeAsync(string payload, string? signature, CancellationToken ct);
}

internal sealed class BillingWebhookHandler : IBillingWebhookHandler
{
    private readonly StripeBillingOptions _options;
    private readonly IWebhookSecretProvider _webhookSecret;
    private readonly IBillingCatalog _catalog;
    private readonly IBillingOrchestrator _billing;
    private readonly IBillingEntitlementStore _entitlements;
    private readonly IBillingWebhookEventStore _eventStore;

    public BillingWebhookHandler(
        IOptions<StripeBillingOptions> options,
        IWebhookSecretProvider webhookSecret,
        IBillingCatalog catalog,
        IBillingOrchestrator billing,
        IBillingEntitlementStore entitlements,
        IBillingWebhookEventStore eventStore)
    {
        _options = options.Value;
        _webhookSecret = webhookSecret;
        _catalog = catalog;
        _billing = billing;
        _entitlements = entitlements;
        _eventStore = eventStore;
    }

    public async Task<StripeWebhookProcessResult> HandleStripeAsync(string payload, string? signature, CancellationToken ct)
    {
        var webhookSecret = await _webhookSecret.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(webhookSecret))
            return Result(false, false, false, null, "unknown", "webhook_secret_unconfigured", null, null, null, null);

        if (!_options.SkipSignatureVerification)
        {
            try
            {
                EventUtility.ConstructEvent(payload, signature, webhookSecret, throwOnApiVersionMismatch: false);
            }
            catch (Exception)
            {
                return Result(false, false, false, null, "unknown", "invalid_signature", null, null, null, null);
            }
        }

        const bool verified = true;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return Result(false, verified, false, null, "unknown", "invalid_json", null, null, null, null);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var eventId = StringProperty(root, "id") ?? $"synthetic_{Guid.NewGuid():N}";
            var eventType = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? "unknown"
                : "unknown";

            if (!await _eventStore.TryBeginAsync(eventId, eventType, ct))
                return Result(true, verified, true, eventId, eventType, "duplicate", null, null, null, null);

            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var obj))
            {
                await _eventStore.CompleteAsync(eventId, "missing_object", ct);
                return Result(false, verified, false, eventId, eventType, "missing_object", null, null, null, null);
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
                    await _billing.TryApproveQuoteAsync(quoteId, ct);

                var plan = _catalog.ListPlans()
                    .FirstOrDefault(p => string.Equals(p.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase));
                if (plan is not null && !string.IsNullOrWhiteSpace(tenant))
                {
                    await _entitlements.ActivatePlanAsync(tenant, plan, customerId, subscriptionId, DateTimeOffset.UtcNow, ct);
                    await _eventStore.CompleteAsync(eventId, "plan_activated", ct);
                    return Result(true, verified, false, eventId, eventType, "plan_activated", tenant, serviceId, quoteId, plan.PlanId);
                }

                await _eventStore.CompleteAsync(eventId, "quote_approved", ct);
                return Result(true, verified, false, eventId, eventType, "quote_approved", tenant, serviceId, quoteId, null);
            }

            if (string.Equals(eventType, "invoice.paid", StringComparison.OrdinalIgnoreCase))
            {
                var plan = _catalog.ListPlans()
                    .FirstOrDefault(p => string.Equals(p.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase));
                if (plan is not null && !string.IsNullOrWhiteSpace(tenant))
                {
                    await _entitlements.RenewPlanAsync(tenant, plan, customerId, subscriptionId, DateTimeOffset.UtcNow, ct);
                    await _eventStore.CompleteAsync(eventId, "plan_renewed", ct);
                    return Result(true, verified, false, eventId, eventType, "plan_renewed", tenant, serviceId, quoteId, plan.PlanId);
                }
            }

            if (string.Equals(eventType, "customer.subscription.deleted", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(subscriptionId) &&
                    await _entitlements.DeactivateSubscriptionAsync(subscriptionId, "canceled", ct) is { } entitlement)
                {
                    await _eventStore.CompleteAsync(eventId, "plan_canceled", ct);
                    return Result(true, verified, false, eventId, eventType, "plan_canceled", entitlement.Tenant, serviceId, quoteId, entitlement.PlanId);
                }
            }

            if (string.Equals(eventType, "customer.subscription.updated", StringComparison.OrdinalIgnoreCase))
            {
                var subscriptionStatus = StringProperty(obj, "status") ?? "updated";
                if (subscriptionStatus is "canceled" or "unpaid" or "incomplete_expired" && !string.IsNullOrWhiteSpace(subscriptionId) &&
                    await _entitlements.DeactivateSubscriptionAsync(subscriptionId, subscriptionStatus, ct) is { } entitlement)
                {
                    await _eventStore.CompleteAsync(eventId, $"plan_{subscriptionStatus}", ct);
                    return Result(true, verified, false, eventId, eventType, $"plan_{subscriptionStatus}", entitlement.Tenant, serviceId, quoteId, entitlement.PlanId);
                }
            }

            await _eventStore.CompleteAsync(eventId, "ignored", ct);
            return Result(true, verified, false, eventId, eventType, "ignored", tenant, serviceId, quoteId, null);
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
