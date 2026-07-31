using Laplace.SubstrateCRUD.Npgsql;
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
        return await NpgsqlRead.ExecuteNonQueryAsync(_dataSource, sql, p =>
        {
            p.AddWithValue("event_id", eventId);
            p.AddWithValue("status", $"processing:{eventType}");
        }, ct: ct) == 1;
    }

    public Task CompleteAsync(string eventId, string status, CancellationToken ct)
    {
        const string sql = "UPDATE app.billing_webhook_events SET status = @status WHERE event_id = @event_id;";
        return NpgsqlRead.ExecuteNonQueryAsync(_dataSource, sql, p =>
        {
            p.AddWithValue("event_id", eventId);
            p.AddWithValue("status", status);
        }, ct: ct);
    }
}
