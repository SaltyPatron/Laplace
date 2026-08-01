using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

/// <summary>Reachability probe for the Layer-1 billing schema (not substrate catalog).</summary>
internal static class BillingSchemaProbe
{
    public static void EnsureQuotesTableReachable(NpgsqlDataSource dataSource)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand("SELECT 1 FROM app.billing_quotes LIMIT 1;", conn);
        cmd.ExecuteNonQuery();
    }
}
