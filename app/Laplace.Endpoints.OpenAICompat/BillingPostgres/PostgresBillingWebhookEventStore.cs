using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingWebhookEventStore : IBillingWebhookEventStore
{
    private readonly NpgsqlDataSource _dataSource;
    public PostgresBillingWebhookEventStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> TryBeginAsync(string eventId, string eventType, CancellationToken ct)
    {
        var sql = SqlCatalog.Get("billing.webhook_begin").Text;
        return await NpgsqlRead.ExecuteNonQueryAsync(_dataSource, sql, p =>
        {
            p.Add(new NpgsqlParameter { Value = eventId });
            p.Add(new NpgsqlParameter { Value = $"processing:{eventType}" });
        }, ct: ct) == 1;
    }

    public Task CompleteAsync(string eventId, string status, CancellationToken ct) =>
        NpgsqlRead.ExecuteNonQueryAsync(_dataSource,
            SqlCatalog.Get("billing.webhook_complete").Text,
            p =>
            {
                p.Add(new NpgsqlParameter { Value = eventId });
                p.Add(new NpgsqlParameter { Value = status });
            }, ct: ct);

    public Task<string?> GetStatusAsync(string eventId, CancellationToken ct) =>
        NpgsqlRead.ReadFirstOrDefaultAsync(_dataSource,
            SqlCatalog.Get("billing.webhook_status").Text,
            r => r.GetString(0), p => p.Add(new NpgsqlParameter { Value = eventId }), ct: ct);
}
