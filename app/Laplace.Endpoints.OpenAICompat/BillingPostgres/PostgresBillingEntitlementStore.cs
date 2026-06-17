using System.Text.Json;
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
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("plan_id", plan.PlanId);
        cmd.Parameters.AddWithValue("start", activatedAt);
        cmd.Parameters.AddWithValue("end", activatedAt.AddMonths(1));
        cmd.Parameters.Add(new NpgsqlParameter("credits", NpgsqlDbType.Jsonb)
            { Value = JsonSerializer.Serialize(plan.MonthlyCredits) });
        cmd.Parameters.AddWithValue("customer", (object?)stripeCustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("subscription", (object?)stripeSubscriptionId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

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
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("plan_id", plan.PlanId);
        cmd.Parameters.AddWithValue("start", renewedAt);
        cmd.Parameters.AddWithValue("end", renewedAt.AddMonths(1));
        cmd.Parameters.Add(new NpgsqlParameter("credits", NpgsqlDbType.Jsonb)
            { Value = JsonSerializer.Serialize(plan.MonthlyCredits) });
        cmd.Parameters.AddWithValue("customer", (object?)stripeCustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("subscription", (object?)stripeSubscriptionId ?? DBNull.Value);

        string? customerId = stripeCustomerId, subscriptionId = stripeSubscriptionId;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                customerId = reader.IsDBNull(0) ? null : reader.GetString(0);
                subscriptionId = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        return new BillingEntitlement(
            Tenant: tenant,
            PlanId: plan.PlanId,
            Status: "active",
            PeriodStart: renewedAt,
            PeriodEnd: renewedAt.AddMonths(1),
            MonthlyCredits: new Dictionary<string, int>(plan.MonthlyCredits, StringComparer.OrdinalIgnoreCase),
            UsedCredits: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            StripeCustomerId: customerId,
            StripeSubscriptionId: subscriptionId,
            UpdatedAt: renewedAt);
    }

    public async Task<BillingEntitlement?> DeactivateSubscriptionAsync(string stripeSubscriptionId, string status, CancellationToken ct)
    {
        const string sql = """
            UPDATE app.billing_entitlements
            SET status = @status, updated_at = now()
            WHERE stripe_subscription_id = @subscription
            RETURNING tenant, plan_id, status, period_start, period_end, monthly_credits, used_credits,
                      stripe_customer_id, stripe_subscription_id, updated_at;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("subscription", stripeSubscriptionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return ReadEntitlement(reader);
    }

    public async Task<IReadOnlyList<BillingEntitlement>> GetByTenantAsync(string tenant, CancellationToken ct)
    {
        const string sql = """
            SELECT tenant, plan_id, status, period_start, period_end, monthly_credits, used_credits,
                   stripe_customer_id, stripe_subscription_id, updated_at
            FROM app.billing_entitlements
            WHERE tenant = @tenant
            ORDER BY plan_id;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant", tenant);
        var rows = new List<BillingEntitlement>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(ReadEntitlement(reader));
        return rows;
    }

    public async Task<(bool Consumed, BillingCreditDebit Debit)> TryConsumeCreditAsync(
        string tenant, string serviceId, int units, CancellationToken ct)
    {
        if (units <= 0)
            return (false, new BillingCreditDebit(tenant, string.Empty, serviceId, units, 0, DateTimeOffset.MinValue, "invalid_units"));

        
        
        
        const string sql = """
            WITH candidate AS (
                SELECT tenant, plan_id,
                       COALESCE((monthly_credits->>@service)::int, 0) AS credit_limit,
                       COALESCE((used_credits->>@service)::int, 0) AS used,
                       period_end
                FROM app.billing_entitlements
                WHERE tenant = @tenant
                  AND status = 'active'
                  AND period_end > now()
                  AND COALESCE((monthly_credits->>@service)::int, 0)
                      - COALESCE((used_credits->>@service)::int, 0) >= @units
                ORDER BY COALESCE((monthly_credits->>@service)::int, 0) DESC
                LIMIT 1
                FOR UPDATE
            )
            UPDATE app.billing_entitlements e
            SET used_credits = jsonb_set(e.used_credits, ARRAY[@service], to_jsonb(c.used + @units)),
                updated_at = now()
            FROM candidate c
            WHERE e.tenant = c.tenant AND e.plan_id = c.plan_id
            RETURNING e.plan_id, c.credit_limit - c.used - @units, e.period_end;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("service", serviceId);
        cmd.Parameters.AddWithValue("units", units);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (false, new BillingCreditDebit(tenant, string.Empty, serviceId, units, 0, DateTimeOffset.MinValue, "insufficient_credits"));

        return (true, new BillingCreditDebit(
            Tenant: tenant,
            PlanId: reader.GetString(0),
            ServiceId: serviceId,
            Units: units,
            Remaining: reader.GetInt32(1),
            PeriodEnd: reader.GetFieldValue<DateTimeOffset>(2),
            Status: "consumed"));
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
