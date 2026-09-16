using System.Text.Json;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingEntitlementStore : IBillingEntitlementStore
{
    private readonly NpgsqlDataSource _dataSource;
    public PostgresBillingEntitlementStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public Task<BillingEntitlement> ActivatePlanAsync(
        string tenant, BillingPlan plan, string? stripeCustomerId, string? stripeSubscriptionId,
        DateTimeOffset activatedAt, CancellationToken ct) =>
        ApplySubscriptionAsync(plan, new BillingSubscriptionState(tenant, stripeSubscriptionId ?? "", stripeCustomerId,
            "active", activatedAt, activatedAt.AddMonths(1), activatedAt.ToUnixTimeSeconds(), false), ct);

    public Task<BillingEntitlement> RenewPlanAsync(
        string tenant, BillingPlan plan, string? stripeCustomerId, string? stripeSubscriptionId,
        DateTimeOffset renewedAt, CancellationToken ct) =>
        ActivatePlanAsync(tenant, plan, stripeCustomerId, stripeSubscriptionId, renewedAt, ct);

    public async Task<BillingEntitlement> ApplySubscriptionAsync(BillingPlan plan, BillingSubscriptionState state, CancellationToken ct)
    {
        const string sql = """
            SELECT tenant, plan_id, status, period_start, period_end, monthly_credits, used_credits,
                   stripe_customer_id, stripe_subscription_id, updated_at, cancel_at_period_end
            FROM app.apply_subscription(@tenant, @plan, @status, @start, @end, @credits,
                @customer, @subscription, @event_created, @cancel_at_period_end);
            """;
        var result = await NpgsqlRead.ReadFirstOrDefaultAsync(_dataSource, sql, ReadEntitlement, p =>
        {
            p.AddWithValue("tenant", state.Tenant);
            p.AddWithValue("plan", plan.PlanId);
            p.AddWithValue("status", state.Status);
            p.AddWithValue("start", state.PeriodStart);
            p.AddWithValue("end", state.PeriodEnd);
            p.Add(new NpgsqlParameter("credits", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(plan.MonthlyCredits) });
            p.Add(new NpgsqlParameter("customer", NpgsqlDbType.Text) { Value = (object?)state.CustomerId ?? DBNull.Value });
            p.AddWithValue("subscription", state.SubscriptionId);
            p.AddWithValue("event_created", state.EventCreated);
            p.AddWithValue("cancel_at_period_end", state.CancelAtPeriodEnd);
        }, ct: ct);
        return result ?? throw new InvalidOperationException("The subscription operation returned no account state.");
    }

    public Task<BillingEntitlement?> DeactivateSubscriptionAsync(string stripeSubscriptionId, string status, CancellationToken ct)
    {
        const string sql = """
            UPDATE app.billing_entitlements SET status = @status, updated_at = now()
            WHERE stripe_subscription_id = @subscription
            RETURNING tenant, plan_id, status, period_start, period_end, monthly_credits, used_credits,
                      stripe_customer_id, stripe_subscription_id, updated_at, cancel_at_period_end;
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
                   stripe_customer_id, stripe_subscription_id, updated_at, cancel_at_period_end
            FROM app.billing_entitlements WHERE tenant = @tenant ORDER BY plan_id;
            """;
        return NpgsqlRead.ReadRowsAsync(_dataSource, sql, ReadEntitlement, p => p.AddWithValue("tenant", tenant), ct: ct);
    }

    public async Task<(bool Consumed, BillingCreditDebit Debit)> TryConsumeCreditAsync(
        string tenant, string serviceId, int units, CancellationToken ct)
    {
        if (units <= 0) return (false, new BillingCreditDebit(tenant, "", serviceId, units, 0, DateTimeOffset.MinValue, "invalid_units"));
        const string sql = """
            SELECT plan_id, remaining, period_end FROM app.consume_credit(@tenant, @service, @units);
            """;
        var debit = await NpgsqlRead.ReadFirstOrDefaultAsync(_dataSource, sql,
            r => new BillingCreditDebit(tenant, r.GetString(0), serviceId, units, r.GetInt32(1),
                r.GetFieldValue<DateTimeOffset>(2), "consumed"),
            p =>
            {
                p.AddWithValue("tenant", tenant);
                p.AddWithValue("service", serviceId);
                p.AddWithValue("units", units);
            }, ct: ct);
        return debit is null
            ? (false, new BillingCreditDebit(tenant, "", serviceId, units, 0, DateTimeOffset.MinValue, "insufficient_credits"))
            : (true, debit);
    }

    private static BillingEntitlement ReadEntitlement(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4),
        ReadCredits(r.GetString(5)), ReadCredits(r.GetString(6)),
        r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
        r.GetFieldValue<DateTimeOffset>(9), r.GetBoolean(10));

    private static Dictionary<string, int> ReadCredits(string json) => new(
        JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new(), StringComparer.OrdinalIgnoreCase);
}
