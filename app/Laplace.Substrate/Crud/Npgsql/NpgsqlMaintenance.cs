using Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The maintenance statements that cannot be installed operations.
///
/// Everything else the operator surface runs is a named call through
/// <see cref="InstalledOpInvoker"/> against the live <c>ops.api()</c> catalog.
/// VACUUM cannot join them: Postgres refuses it inside a transaction block, and a
/// PL/pgSQL procedure body is always in one, so there is no nesting at which it
/// can be wrapped in SQL. It has to be issued by a client on a connection that is
/// not in a transaction — which is what this is.
///
/// It lives here rather than in the endpoint because the read-path gate is right:
/// SQL written in a consumer is SQL written twice. The CLI will want this too.
/// </summary>
public static class NpgsqlMaintenance
{
    /// <summary>
    /// The schema-qualified, correctly-quoted name of a substrate table, or null
    /// when the name is not one.
    ///
    /// The table name reaches the planner as an IDENTIFIER, which cannot be a bound
    /// parameter, so it is RESOLVED rather than quoted and hoped for: the lookup
    /// both refuses an unknown name and returns the form Postgres will accept back.
    /// <c>regclass</c> renders exactly the quoting it parses, so no caller ever
    /// composes an identifier.
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
    /// Run VACUUM, optionally on one table.
    ///
    /// <paramref name="qualifiedTable"/> must have come from
    /// <see cref="ResolveSubstrateTableAsync"/> — it is interpolated as an
    /// identifier, so an unresolved caller string would be the one injection hole
    /// on this surface. Null vacuums the whole database.
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
