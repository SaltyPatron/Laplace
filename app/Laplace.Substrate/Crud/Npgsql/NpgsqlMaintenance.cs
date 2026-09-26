using Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Maintenance statements that cannot be installed operations. Other operator calls go
/// through <see cref="InstalledOpInvoker"/> against <c>ops.api()</c>; VACUUM cannot,
/// because PostgreSQL refuses it inside a transaction block and a PL/pgSQL body always
/// runs in one. It is issued here by the client on a connection outside a transaction.
/// </summary>
public static class NpgsqlMaintenance
{
    /// <summary>
    /// The schema-qualified, correctly quoted name of a substrate table, or null when the
    /// name is not one.
    ///
    /// A table name is an identifier and cannot be a bound parameter, so it is resolved
    /// through the catalog instead of quoted: an unknown name returns null, and
    /// <c>regclass</c> renders exactly the quoting it parses.
    /// </summary>
    public static async Task<string?> ResolveSubstrateTableAsync(
        NpgsqlDataSource db, string table, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(SubstrateTableSql);
        cmd.Parameters.Add(new NpgsqlParameter { Value = table });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private const string SubstrateTableSql = """
        SELECT c.oid::regclass::text
        FROM pg_class c
        WHERE c.relname = $1
          AND c.relkind IN ('r', 'p')
          AND c.relnamespace IN ('laplace'::regnamespace, 'realize'::regnamespace,
                                 'consensus'::regnamespace, 'generation'::regnamespace)
        LIMIT 1
        """;

    /// <summary>
    /// Runs VACUUM, optionally on one table.
    ///
    /// <paramref name="qualifiedTable"/> is interpolated as an identifier and must come
    /// from <see cref="ResolveSubstrateTableAsync"/>; an unresolved string would be an
    /// injection. Null vacuums the whole database.
    ///
    /// FULL rewrites the table under ACCESS EXCLUSIVE and needs free disk equal to
    /// the table's size; plain VACUUM reclaims space without blocking readers.
    /// </summary>
    public static async Task<string> VacuumAsync(
        NpgsqlDataSource db,
        string? qualifiedTable,
        bool full,
        bool analyze,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        var verb = full ? "VACUUM (FULL, ANALYZE)" : analyze ? "VACUUM (ANALYZE)" : "VACUUM";
        var sql = qualifiedTable is null ? verb : $"{verb} {qualifiedTable}";

        await using var cmd = db.CreateCommand(sql);
        cmd.CommandTimeout = InstalledOpInvoker.RequestedCommandTimeout(timeoutSeconds);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return sql;
    }
}
