using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

/// <summary>Reachability probe for the Layer-1 billing schema (not substrate catalog).</summary>
internal static class BillingSchemaProbe
{
    public static void EnsureQuotesTableReachable(NpgsqlDataSource dataSource)
    {
        using var cmd = NpgsqlCatalog.Command(dataSource, "billing.probe_quotes");
        cmd.ExecuteNonQuery();
    }
}
