using System.Text.Json;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

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
        await using var command = NpgsqlCatalog.Command(_dataSource, "billing.apply_subscription",
            state.Tenant, plan.PlanId, state.Status, state.PeriodStart, state.PeriodEnd,
            JsonSerializer.Serialize(plan.MonthlyCredits), state.CustomerId, state.SubscriptionId,
            state.EventCreated, state.CancelAtPeriodEnd);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntitlement(reader)
            : throw new InvalidOperationException("The subscription operation returned no account state.");
    }

    public async Task<BillingEntitlement?> DeactivateSubscriptionAsync(string stripeSubscriptionId, string status, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "billing.deactivate_subscription", status, stripeSubscriptionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntitlement(reader) : null;
    }

    public async Task<IReadOnlyList<BillingEntitlement>> GetByTenantAsync(string tenant, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "billing.entitlements", tenant);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<BillingEntitlement>();
        while (await reader.ReadAsync(ct)) result.Add(ReadEntitlement(reader));
        return result;
    }

    public async Task<(bool Consumed, BillingCreditDebit Debit)> TryConsumeCreditAsync(
        string tenant, string serviceId, int units, CancellationToken ct)
    {
        if (units <= 0) return (false, new BillingCreditDebit(tenant, "", serviceId, units, 0, DateTimeOffset.MinValue, "invalid_units"));
        await using var command = NpgsqlCatalog.Command(_dataSource, "billing.consume_credit", tenant, serviceId, units);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (false, new BillingCreditDebit(tenant, "", serviceId, units, 0, DateTimeOffset.MinValue, "insufficient_credits"));
        return (true, new BillingCreditDebit(tenant, reader.GetString(0), serviceId, units,
            reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2), "consumed"));
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
