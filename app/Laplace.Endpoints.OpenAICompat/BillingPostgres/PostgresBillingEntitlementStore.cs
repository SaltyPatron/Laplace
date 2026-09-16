using Laplace.Engine.Core;
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
        var sql = SqlCatalog.Get("billing.subscription_apply").Text;
        var result = await NpgsqlRead.ReadFirstOrDefaultAsync(_dataSource, sql, ReadEntitlement, p =>
        {
            p.Add(new NpgsqlParameter { Value = state.Tenant });
            p.Add(new NpgsqlParameter { Value = plan.PlanId });
            p.Add(new NpgsqlParameter { Value = state.Status });
            p.Add(new NpgsqlParameter { Value = state.PeriodStart });
            p.Add(new NpgsqlParameter { Value = state.PeriodEnd });
            p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(plan.MonthlyCredits) });
            p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)state.CustomerId ?? DBNull.Value });
            p.Add(new NpgsqlParameter { Value = state.SubscriptionId });
            p.Add(new NpgsqlParameter { Value = state.EventCreated });
            p.Add(new NpgsqlParameter { Value = state.CancelAtPeriodEnd });
        }, ct: ct);
        return result ?? throw new InvalidOperationException("The subscription operation returned no account state.");
    }

    public Task<BillingEntitlement?> DeactivateSubscriptionAsync(string stripeSubscriptionId, string status, CancellationToken ct)
    {
        var sql = SqlCatalog.Get("billing.subscription_deactivate").Text;
        return NpgsqlRead.ReadFirstOrDefaultAsync(_dataSource, sql, ReadEntitlement, p =>
        {
            p.Add(new NpgsqlParameter { Value = status });
            p.Add(new NpgsqlParameter { Value = stripeSubscriptionId });
        }, ct: ct);
    }

    public Task<IReadOnlyList<BillingEntitlement>> GetByTenantAsync(string tenant, CancellationToken ct)
    {
        var sql = SqlCatalog.Get("billing.entitlements_list").Text;
        return NpgsqlRead.ReadRowsAsync(_dataSource, sql, ReadEntitlement,
            p => p.Add(new NpgsqlParameter { Value = tenant }), ct: ct);
    }

    public async Task<(bool Consumed, BillingCreditDebit Debit)> TryConsumeCreditAsync(
        string tenant, string serviceId, int units, CancellationToken ct)
    {
        if (units <= 0) return (false, new BillingCreditDebit(tenant, "", serviceId, units, 0, DateTimeOffset.MinValue, "invalid_units"));
        var sql = SqlCatalog.Get("billing.credit_consume").Text;
        var debit = await NpgsqlRead.ReadFirstOrDefaultAsync(_dataSource, sql,
            r => new BillingCreditDebit(tenant, r.GetString(0), serviceId, units, r.GetInt32(1),
                r.GetFieldValue<DateTimeOffset>(2), "consumed"),
            p =>
            {
                p.Add(new NpgsqlParameter { Value = tenant });
                p.Add(new NpgsqlParameter { Value = serviceId });
                p.Add(new NpgsqlParameter { Value = units });
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
