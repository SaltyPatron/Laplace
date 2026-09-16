using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingWebhookEventStore : IBillingWebhookEventStore
{
    private readonly NpgsqlDataSource _dataSource;
    public PostgresBillingWebhookEventStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> TryBeginAsync(string eventId, string eventType, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "billing.begin_webhook", eventId, $"processing:{eventType}");
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task CompleteAsync(string eventId, string status, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "billing.complete_webhook", eventId, status);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetStatusAsync(string eventId, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "billing.webhook_status", eventId);
        return await command.ExecuteScalarAsync(ct) as string;
    }
}
