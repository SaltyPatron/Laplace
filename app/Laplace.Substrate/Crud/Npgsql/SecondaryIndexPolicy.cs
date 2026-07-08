using System.Text.RegularExpressions;

using global::Npgsql;



namespace Laplace.SubstrateCRUD.Npgsql;



public sealed class SecondaryIndexPolicy

{

    private static readonly Regex SafeTable = new("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);



    private readonly NpgsqlDataSource _ds;



    public SecondaryIndexPolicy(NpgsqlDataSource dataSource)

    {

        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    }



    internal static string RequireSafeTable(string table)

    {

        if (string.IsNullOrEmpty(table) || !SafeTable.IsMatch(table))

            throw new ArgumentException($"unsafe substrate table identifier: '{table}'", nameof(table));

        return table;

    }



    public async Task<bool> SecondaryIndexesPresentAsync(string table, CancellationToken ct = default)

    {

        RequireSafeTable(table);

        await using var conn = await _ds.OpenConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();

        cmd.CommandText =

            "SELECT EXISTS ("

            + "SELECT 1 FROM pg_index i "

            + "JOIN pg_class t ON t.oid = i.indrelid "

            + "JOIN pg_namespace n ON n.oid = t.relnamespace "

            + "WHERE n.nspname = 'laplace' AND t.relname = $1 "

            + "  AND NOT i.indisprimary AND NOT i.indisunique)";

        cmd.Parameters.AddWithValue(table);

        var r = await cmd.ExecuteScalarAsync(ct);

        return r is bool b && b;

    }



    public static async Task EnsureIndexesAsync(

        NpgsqlDataSource ds, IReadOnlyList<string> indexDefs, CancellationToken ct)

    {

        var defs = DedupeIndexDefs(indexDefs);

        await using var conn = await ds.OpenConnectionAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);

        try

        {

            await using (var t = conn.CreateCommand())

            {

                t.Transaction = tx;

                t.CommandText =

                    $"SET LOCAL maintenance_work_mem = '{Laplace.Engine.Core.MemoryTopology.MaintenanceWorkMemBytes >> 20}MB'; "

                    + $"SET LOCAL max_parallel_maintenance_workers = {Laplace.Engine.Core.CpuTopology.ParallelMaintenanceWorkers}";

                await t.ExecuteNonQueryAsync(ct);

            }

            foreach (var def in defs)

            {

                await using var c = conn.CreateCommand();

                c.Transaction = tx;

                c.CommandTimeout = 0;

                c.CommandText = EnsureCreateIndexIfNotExists(def);

                await c.ExecuteNonQueryAsync(ct);

            }

            await tx.CommitAsync(ct);

        }

        catch

        {

            try { await tx.RollbackAsync(CancellationToken.None); }

            catch { }

            throw;

        }

    }



    internal static List<string> DedupeIndexDefs(IReadOnlyList<string> indexDefs)

    {

        var seen = new HashSet<string>(StringComparer.Ordinal);

        var deduped = new List<string>(indexDefs.Count);

        foreach (var def in indexDefs)

        {

            var name = TryExtractIndexName(def);

            if (name is not null && !seen.Add(name)) continue;

            deduped.Add(def);

        }

        return deduped;

    }



    internal static string EnsureCreateIndexIfNotExists(string indexDef)

    {

        if (indexDef.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase))

            return indexDef;

        return Regex.Replace(

            indexDef,

            @"^(\s*CREATE\s+INDEX\s+)",

            "$1IF NOT EXISTS ",

            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    }



    internal static string? TryExtractIndexName(string indexDef)

    {

        var m = Regex.Match(

            indexDef,

            @"CREATE\s+INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?<schema>\w+)\.)?(?<name>""[^""]+""|\w+)",

            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!m.Success) return null;

        var name = m.Groups["name"].Value;

        return name.Length >= 2 && name[0] == '"' && name[^1] == '"'

            ? name[1..^1]

            : name;

    }

}

