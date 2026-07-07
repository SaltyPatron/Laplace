using Laplace.Engine.Core;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;







public abstract class BillingStoreContractTests
{
    private protected abstract IBillingQuoteStore Quotes { get; }
    private protected abstract IBillingLedger Ledger { get; }
    private protected abstract IBillingEntitlementStore Entitlements { get; }
    private protected abstract IBillingWebhookEventStore WebhookEvents { get; }
    private protected abstract IStripePriceMap PriceMap { get; }


    protected virtual bool Available => true;

    private void RequireStore() =>
        Skip.IfNot(Available, "LAPLACE_DB unreachable or app schema not applied — Postgres store contract skipped.");

    private static BillingQuote NewQuote(string tenant, string serviceId = "chat.completions") => new(
        QuoteId: $"q_{Guid.NewGuid():N}",
        Tenant: tenant,
        ServiceId: serviceId,
        Units: 1,
        AmountCents: 3,
        Currency: "usd",
        Status: "pending_payment",
        StripeSessionId: null,
        StripeCheckoutUrl: null,
        CreatedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
        Consumed: false);

    private static BillingPlan NewPlan(string planId, int synthesisCredits) => new(
        PlanId: planId,
        ServiceId: $"plan.{planId}",
        Name: planId,
        Description: "contract test plan",
        MonthlyPriceCents: 9900,
        Currency: "usd",
        MonthlyCredits: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["synthesis"] = synthesisCredits
        },
        IncludedProductIds: [],
        SupportTier: "test",
        Active: true);

    [SkippableFact]
    public async Task QuoteStore_PutGetUpdate_RoundTrips()
    {
        RequireStore();
        var quote = NewQuote($"t-{Guid.NewGuid():N}");
        await Quotes.PutAsync(quote, CancellationToken.None);

        var fetched = await Quotes.TryGetAsync(quote.QuoteId, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(quote.QuoteId, fetched!.QuoteId);
        Assert.Equal(quote.Tenant, fetched.Tenant);
        Assert.Equal("pending_payment", fetched.Status);
        Assert.False(fetched.Consumed);

        await Quotes.UpdateAsync(fetched with { Status = "approved", Consumed = true }, CancellationToken.None);
        var updated = await Quotes.TryGetAsync(quote.QuoteId, CancellationToken.None);
        Assert.Equal("approved", updated!.Status);
        Assert.True(updated.Consumed);

        Assert.Null(await Quotes.TryGetAsync("q_missing", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Ledger_RecordsAndReadsNewestFirst()
    {
        RequireStore();
        var tenant = $"t-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await Ledger.RecordAsync(new BillingUsageRecord("q_1", tenant, "completions", 1, 2, now.AddMinutes(-2)), CancellationToken.None);
        await Ledger.RecordAsync(new BillingUsageRecord("q_2", tenant, "completions", 1, 2, now.AddMinutes(-1)), CancellationToken.None);

        var usage = await Ledger.GetByTenantAsync(tenant, CancellationToken.None);
        Assert.Equal(2, usage.Count);
        Assert.Equal("q_2", usage[0].QuoteId);
        Assert.Equal("q_1", usage[1].QuoteId);

        Assert.Empty(await Ledger.GetByTenantAsync($"t-none-{Guid.NewGuid():N}", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Entitlements_ActivateConsumeExhaustDeactivate()
    {
        RequireStore();
        var tenant = $"t-{Guid.NewGuid():N}";
        var subscription = $"sub_{Guid.NewGuid():N}";
        var plan = NewPlan("studio", synthesisCredits: 100);

        await Entitlements.ActivatePlanAsync(tenant, plan, "cus_x", subscription, DateTimeOffset.UtcNow, CancellationToken.None);

        var (consumed, debit) = await Entitlements.TryConsumeCreditAsync(tenant, "synthesis", 30, CancellationToken.None);
        Assert.True(consumed);
        Assert.Equal(70, debit.Remaining);
        Assert.Equal("studio", debit.PlanId);

        var (overConsumed, overDebit) = await Entitlements.TryConsumeCreditAsync(tenant, "synthesis", 71, CancellationToken.None);
        Assert.False(overConsumed);
        Assert.Equal("insufficient_credits", overDebit.Status);

        var (unknownService, unknownDebit) = await Entitlements.TryConsumeCreditAsync(tenant, "no.such.service", 1, CancellationToken.None);
        Assert.False(unknownService);
        Assert.Equal("insufficient_credits", unknownDebit.Status);

        var entitlements = await Entitlements.GetByTenantAsync(tenant, CancellationToken.None);
        var entitlement = Assert.Single(entitlements);
        Assert.Equal(30, entitlement.UsedCredits["synthesis"]);

        var deactivated = await Entitlements.DeactivateSubscriptionAsync(subscription, "canceled", CancellationToken.None);
        Assert.NotNull(deactivated);
        Assert.Equal("canceled", deactivated!.Status);

        var (afterCancel, cancelDebit) = await Entitlements.TryConsumeCreditAsync(tenant, "synthesis", 1, CancellationToken.None);
        Assert.False(afterCancel);
        Assert.Equal("insufficient_credits", cancelDebit.Status);

        Assert.Null(await Entitlements.DeactivateSubscriptionAsync($"sub_missing_{Guid.NewGuid():N}", "canceled", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Entitlements_RenewResetsUsedCredits()
    {
        RequireStore();
        var tenant = $"t-{Guid.NewGuid():N}";
        var subscription = $"sub_{Guid.NewGuid():N}";
        var plan = NewPlan("studio", synthesisCredits: 50);

        await Entitlements.ActivatePlanAsync(tenant, plan, "cus_x", subscription, DateTimeOffset.UtcNow, CancellationToken.None);
        await Entitlements.TryConsumeCreditAsync(tenant, "synthesis", 50, CancellationToken.None);

        var (exhausted, _) = await Entitlements.TryConsumeCreditAsync(tenant, "synthesis", 1, CancellationToken.None);
        Assert.False(exhausted);


        var renewed = await Entitlements.RenewPlanAsync(tenant, plan, null, null, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(subscription, renewed.StripeSubscriptionId);

        var (afterRenew, debit) = await Entitlements.TryConsumeCreditAsync(tenant, "synthesis", 50, CancellationToken.None);
        Assert.True(afterRenew);
        Assert.Equal(0, debit.Remaining);
    }

    [SkippableFact]
    public async Task WebhookEvents_DuplicateBeginIsRejected()
    {
        RequireStore();
        var eventId = $"evt_{Guid.NewGuid():N}";
        Assert.True(await WebhookEvents.TryBeginAsync(eventId, "checkout.session.completed", CancellationToken.None));
        Assert.False(await WebhookEvents.TryBeginAsync(eventId, "checkout.session.completed", CancellationToken.None));
        await WebhookEvents.CompleteAsync(eventId, "quote_approved", CancellationToken.None);
        Assert.False(await WebhookEvents.TryBeginAsync(eventId, "checkout.session.completed", CancellationToken.None));
    }

    [SkippableFact]
    public async Task PriceMap_SetOverwritesAndGets()
    {
        RequireStore();
        var key = $"lookup_{Guid.NewGuid():N}";
        Assert.Null(await PriceMap.TryGetAsync(key, CancellationToken.None));

        await PriceMap.SetAsync(key, "price_1", CancellationToken.None);
        Assert.Equal("price_1", await PriceMap.TryGetAsync(key, CancellationToken.None));

        await PriceMap.SetAsync(key, "price_2", CancellationToken.None);
        Assert.Equal("price_2", await PriceMap.TryGetAsync(key, CancellationToken.None));
    }
}

public sealed class InMemoryBillingStoreContractTests : BillingStoreContractTests
{
    private protected override IBillingQuoteStore Quotes { get; } = new InMemoryBillingQuoteStore();
    private protected override IBillingLedger Ledger { get; } = new InMemoryBillingLedger();
    private protected override IBillingEntitlementStore Entitlements { get; } = new InMemoryBillingEntitlementStore();
    private protected override IBillingWebhookEventStore WebhookEvents { get; } = new InMemoryBillingWebhookEventStore();
    private protected override IStripePriceMap PriceMap { get; } = new InMemoryStripePriceMap();
}





public sealed class PostgresBillingStoreContractTests : BillingStoreContractTests
{
    private static readonly NpgsqlDataSource? Shared = TryBuild();

    protected override bool Available => Shared is not null;

    private NpgsqlDataSource DataSource => Shared!;

    private static NpgsqlDataSource? TryBuild()
    {
        var connString = LaplaceInstall.PostgresConnectionString();
        try
        {
            var dataSource = new NpgsqlDataSourceBuilder(connString).Build();
            using var conn = dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand("SELECT 1 FROM app.billing_quotes LIMIT 1;", conn);
            cmd.ExecuteNonQuery();
            return dataSource;
        }
        catch
        {
            return null;
        }
    }

    private protected override IBillingQuoteStore Quotes => new BillingPostgres.PostgresBillingQuoteStore(DataSource);
    private protected override IBillingLedger Ledger => new BillingPostgres.PostgresBillingLedger(DataSource);
    private protected override IBillingEntitlementStore Entitlements => new BillingPostgres.PostgresBillingEntitlementStore(DataSource);
    private protected override IBillingWebhookEventStore WebhookEvents => new BillingPostgres.PostgresBillingWebhookEventStore(DataSource);
    private protected override IStripePriceMap PriceMap => new BillingPostgres.PostgresStripePriceMap(DataSource);
}
