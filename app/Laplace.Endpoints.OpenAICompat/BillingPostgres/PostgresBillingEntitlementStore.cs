using System.Text.Json;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingEntitlementStore : IBillingEntitlementStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBillingEntitlementStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<BillingEntitlement> ActivatePlanAsync(
        string tenant, BillingPlan plan, string? stripeCustomerId, string? stripeSubscriptionId,
        DateTimeOffset activatedAt, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.billing_entitlements
                (tenant, plan_id, status, period_start, period_end, monthly_credits, used_credits,
                 stripe_customer_id, stripe_subscription_id, updated_at)
            VALUES (@tenant, @plan_id, 'active', @start, @end, @credits, '{}'::jsonb,
                    @customer, @subscription, @start)
            ON CONFLICT (tenant, plan_id) DO UPDATE SET
                status = 'active',
                period_start = EXCLUDED.period_start,
                period_end = EXCLUDED.period_end,
                monthly_credits = EXCLUDED.monthly_credits,
                used_credits = '{}'::jsonb,
                stripe_customer_id = EXCLUDED.stripe_customer_id,
                stripe_subscription_id = EXCLUDED.stripe_subscription_id,
                updated_at = EXCLUDED.updated_at;
            """;
        await NpgsqlRead.ExecuteNonQueryAsync(_dataSource, sql,
            p => BindPeriod(p, tenant, plan, stripeCustomerId, stripeSubscriptionId, activatedAt), ct: ct);

        return new BillingEntitlement(
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
    }

    public async Task<BillingEntitlement> RenewPlanAsync(
        string tenant, BillingPlan plan, string? stripeCustomerId, string? stripeSubscriptionId,
        DateTimeOffset renewedAt, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.billing_entitlements
                (tenant, plan_id, status, period_start, period_end, monthly_credits, used_credits,
                 stripe_customer_id, stripe_subscription_id, updated_at)
            VALUES (@tenant, @plan_id, 'active', @start, @end, @credits, '{}'::jsonb,
                    @customer, @subscription, @start)
            ON CONFLICT (tenant, plan_id) DO UPDATE SET
                status = 'active',
                period_start = EXCLUDED.period_start,
                period_end = EXCLUDED.period_end,
                monthly_credits = EXCLUDED.monthly_credits,
                used_credits = '{}'::jsonb,
                stripe_customer_id = COALESCE(EXCLUDED.stripe_customer_id, app.billing_entitlements.stripe_customer_id),
                stripe_subscription_id = COALESCE(EXCLUDED.stripe_subscription_id, app.billing_entitlements.stripe_subscription_id),
                updated_at = EXCLUDED.updated_at
            RETURNING stripe_customer_id, stripe_subscription_id;
            """;
        // The COALESCE keeps whatever Stripe ids the row already carried, so the RETURNING
        // row — not the arguments — is the authority when there is one.
        var returned = await NpgsqlRead.ReadFirstOrDefaultAsync(_dataSource, sql,
            r => new StripeIds(
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1)),
            p => BindPeriod(p, tenant, plan, stripeCustomerId, stripeSubscriptionId, renewedAt), ct: ct);

        return new BillingEntitlement(
            Tenant: tenant,
            PlanId: plan.PlanId,
            Status: "active",
            PeriodStart: renewedAt,
            PeriodEnd: renewedAt.AddMonths(1),
            MonthlyCredits: new Dictionary<string, int>(plan.MonthlyCredits, StringComparer.OrdinalIgnoreCase),
            UsedCredits: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            StripeCustomerId: returned?.CustomerId ?? stripeCustomerId,
            StripeSubscriptionId: returned?.SubscriptionId ?? stripeSubscriptionId,
            UpdatedAt: renewedAt);
    }

    public Task<BillingEntitlement?> DeactivateSubscriptionAsync(string stripeSubscriptionId, string status, CancellationToken ct)
    {
        const string sql = """
            UPDATE app.billing_entitlements
            SET status = @status, updated_at = now()
            WHERE stripe_subscription_id = @subscription
            RETURNING tenant, plan_id, status, period_start, period_end, monthly_credits, used_credits,
                      stripe_customer_id, stripe_subscription_id, updated_at;
            """;
        return NpgsqlRead.ReadFirstOrDefaultAsync(_dataSource, sql, ReadEntitlement, p =>
        {
            p.AddWithValue("status", status);
            p.AddWithValue("subscription", stripeSubscriptionId);
        }, ct: ct);
    }

    public Task<IReadOnlyList<BillingEntitlement>> GetByTenantAsync(string tenant, CancellationToken ct)
    {
        const string sql = """
            SELECT tenant, plan_id, status, period_start, period_end, monthly_credits, used_credits,
                   stripe_customer_id, stripe_subscription_id, updated_at
            FROM app.billing_entitlements
            WHERE tenant = @tenant
            ORDER BY plan_id;
            """;
        return NpgsqlRead.ReadRowsAsync(_dataSource, sql, ReadEntitlement,
            p => p.AddWithValue("tenant", tenant), ct: ct);
    }

    public async Task<(bool Consumed, BillingCreditDebit Debit)> TryConsumeCreditAsync(
        string tenant, string serviceId, int units, CancellationToken ct)
    {
        if (units <= 0)
            return (false, new BillingCreditDebit(tenant, string.Empty, serviceId, units, 0, DateTimeOffset.MinValue, "invalid_units"));

        // GH #531: debit lives in app.consume_credit — one function, pinned by
        // BillingStoreContractTests, not a hand-rolled FOR UPDATE CTE per caller.
        const string sql = """
            SELECT plan_id, remaining, period_end
            FROM app.consume_credit(@tenant, @service, @units);
            """;
        var debit = await NpgsqlRead.ReadFirstOrDefaultAsync(_dataSource, sql,
            r => new BillingCreditDebit(
                Tenant: tenant,
                PlanId: r.GetString(0),
                ServiceId: serviceId,
                Units: units,
                Remaining: r.GetInt32(1),
                PeriodEnd: r.GetFieldValue<DateTimeOffset>(2),
                Status: "consumed"),
            p =>
            {
                p.AddWithValue("tenant", tenant);
                p.AddWithValue("service", serviceId);
                p.AddWithValue("units", units);
            }, ct: ct);

        return debit is null
            ? (false, new BillingCreditDebit(tenant, string.Empty, serviceId, units, 0, DateTimeOffset.MinValue, "insufficient_credits"))
            : (true, debit);
    }

    /// <summary>Stripe ids as a period upsert returns them; a record so it can be absent.</summary>
    private sealed record StripeIds(string? CustomerId, string? SubscriptionId);

    /// <summary>The activate/renew upserts differ only in their conflict clause, not their binds.</summary>
    private static void BindPeriod(
        NpgsqlParameterCollection p, string tenant, BillingPlan plan,
        string? stripeCustomerId, string? stripeSubscriptionId, DateTimeOffset periodStart)
    {
        p.AddWithValue("tenant", tenant);
        p.AddWithValue("plan_id", plan.PlanId);
        p.AddWithValue("start", periodStart);
        p.AddWithValue("end", periodStart.AddMonths(1));
        p.Add(new NpgsqlParameter("credits", NpgsqlDbType.Jsonb)
        { Value = JsonSerializer.Serialize(plan.MonthlyCredits) });
        p.AddWithValue("customer", (object?)stripeCustomerId ?? DBNull.Value);
        p.AddWithValue("subscription", (object?)stripeSubscriptionId ?? DBNull.Value);
    }

    private static BillingEntitlement ReadEntitlement(NpgsqlDataReader reader) => new(
        Tenant: reader.GetString(0),
        PlanId: reader.GetString(1),
        Status: reader.GetString(2),
        PeriodStart: reader.GetFieldValue<DateTimeOffset>(3),
        PeriodEnd: reader.GetFieldValue<DateTimeOffset>(4),
        MonthlyCredits: ReadCredits(reader.GetString(5)),
        UsedCredits: ReadCredits(reader.GetString(6)),
        StripeCustomerId: reader.IsDBNull(7) ? null : reader.GetString(7),
        StripeSubscriptionId: reader.IsDBNull(8) ? null : reader.GetString(8),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(9));

    private static Dictionary<string, int> ReadCredits(string json)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new();
        return new Dictionary<string, int>(parsed, StringComparer.OrdinalIgnoreCase);
    }
}
