using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingWebhookEventStore : IBillingWebhookEventStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBillingWebhookEventStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> TryBeginAsync(string eventId, string eventType, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.billing_webhook_events (event_id, status)
            VALUES (@event_id, @status)
            ON CONFLICT (event_id) DO NOTHING;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("event_id", eventId);
        cmd.Parameters.AddWithValue("status", $"processing:{eventType}");
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task CompleteAsync(string eventId, string status, CancellationToken ct)
    {
        const string sql = "UPDATE app.billing_webhook_events SET status = @status WHERE event_id = @event_id;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("event_id", eventId);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
