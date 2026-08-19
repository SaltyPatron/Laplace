using global::Npgsql;
using Microsoft.Extensions.Logging;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Recovery for secondary indexes left absent by the retired index-cycle loader.
///
/// Normal ingest never drops an index. Partitioning, COPY batching, and bulk merge must remain
/// correct and performant with the production read surface online. The journal and recovery verb
/// remain because databases upgraded from the old loader can still contain a committed journal
/// entry for an index that a cancelled run removed.
/// </summary>
public static class NpgsqlIndexCycle
{
    /// <summary>
    /// Restore any index an older interrupted ingest left journaled before accepting new writes.
    /// Missing indexes are already an outage; this operation repairs that state and clears each
    /// journal entry only after PostgreSQL reports the rebuilt index valid.
    /// </summary>
    public static Task RecoverAsync(NpgsqlDataSource ds, ILogger log, CancellationToken ct) =>
        RebuildJournaledAsync(ds, log, ct);

    public static async Task RebuildJournaledAsync(
        NpgsqlDataSource ds, ILogger log, CancellationToken ct)
    {
        var pending = new List<(string Name, string Def)>();
        await using (var conn = await ds.OpenConnectionAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT index_name, index_def FROM laplace.index_cycle_journal";
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                pending.Add((rd.GetString(0), rd.GetString(1)));
        }

        foreach (var (name, def) in pending)
        {
            log.LogWarning("INDEX_RECOVERY re-creating {Index} from retired-cycle journal", name);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await RebuildOneValidAsync(conn, name, def, ct);
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM laplace.index_cycle_journal WHERE index_name = $1";
            del.Parameters.AddWithValue(name);
            await del.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task RebuildOneValidAsync(
        NpgsqlConnection conn, string name, string def, CancellationToken ct)
    {
        bool? existing = await IndexValidityAsync(conn, name, ct);
        if (existing is true) return;

        if (existing is false)
        {
            await using var drop = conn.CreateCommand();
            drop.CommandTimeout = 0;
            drop.CommandText = $"DROP INDEX laplace.{QuoteIdentifier(name)}";
            await drop.ExecuteNonQueryAsync(ct);
        }

        await using (var create = conn.CreateCommand())
        {
            create.CommandTimeout = 0;
            create.CommandText = def.Replace(" ON ONLY ", " ON ", StringComparison.Ordinal);
            await create.ExecuteNonQueryAsync(ct);
        }

        if (await IndexValidityAsync(conn, name, ct) is not true)
            throw new InvalidOperationException(
                $"INDEX_RECOVERY rebuilt '{name}' but it is invalid or absent; "
                + "refusing to clear its journal row");
    }

    private static async Task<bool?> IndexValidityAsync(
        NpgsqlConnection conn, string name, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText =
            "SELECT i.indisvalid FROM pg_catalog.pg_index i "
            + "JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
            + "WHERE n.nspname = 'laplace' AND c.relname = $1";
        probe.Parameters.AddWithValue(name);
        return await probe.ExecuteScalarAsync(ct) is bool valid ? valid : null;
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
