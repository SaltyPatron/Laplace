using Npgsql;

namespace Laplace.Data;

/// <summary>
/// Out-of-transaction maintenance statements (e.g. VACUUM) validated against PostgreSQL catalogs.
/// </summary>
public static class NpgsqlMaintenance
{
    public static async Task<string?> ResolveSubstrateTableAsync(
        NpgsqlDataSource db, string table, CancellationToken ct = default)
    {
        const string sql = """
            SELECT c.oid::regclass::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('public', 'ops', 'stage', 'app')
              AND c.relkind IN ('r', 'p', 'm')
              AND (c.relname = $1 OR (n.nspname || '.' || c.relname) = $1)
            ORDER BY CASE n.nspname WHEN 'public' THEN 0 WHEN 'ops' THEN 1 WHEN 'stage' THEN 2 ELSE 3 END
            LIMIT 1
            """;
        return await NpgsqlRead.ExecuteScalarAsync<string>(db, sql,
            p => p.AddWithValue(table), ct: ct, label: "resolve_substrate_table").ConfigureAwait(false);
    }

    public static async Task<string> VacuumAsync(
        NpgsqlDataSource db,
        string? qualifiedTable,
        bool full,
        bool analyze,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        await using var cmd = NpgsqlCatalog.VacuumCommand(db, qualifiedTable, full, analyze);
        cmd.CommandTimeout = InstalledOpInvoker.RequestedCommandTimeout(timeoutSeconds);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return cmd.CommandText;
    }
}
