using global::Npgsql;
using Microsoft.Extensions.Logging;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Bulk-load index cycling. Per-row secondary-index maintenance is the
/// dominant cost of a fresh-seed COPY (physicalities alone carries ~9
/// indexes — every staged row pays ~9 scattered index inserts and the
/// backends contend on shared index pages). When an apply is fresh-seed
/// shaped (staged rows rival or exceed the table's cardinality), it is
/// strictly cheaper to drop the secondaries, COPY clean heaps, and rebuild
/// each index as a sort-based parallel bulk build — the one PostgreSQL bulk
/// path that actually saturates NVMe.
///
/// Crash safety: every dropped index's definition is journaled to
/// laplace.index_cycle_journal in its own committed transaction BEFORE the
/// drop; rows are removed as rebuilds succeed. RecoverAsync re-creates
/// whatever a crash left journaled and runs before any cycling decision.
/// Only plain secondary indexes cycle — PK/unique/exclusion constraints
/// stay (verification probes and merge lanes depend on them).
/// </summary>
internal sealed class NpgsqlIndexCycle
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger _log;
    private readonly List<(string Name, string Def)> _dropped = new();

    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("LAPLACE_INDEX_CYCLE") != "0";

    /// <summary>Cycle when staged rows ≥ this (absolute). The run-scoped cycle
    /// drops once and rebuilds once at run end, so a bulk source cycles even as
    /// an increment onto an already-large table — where per-row secondary
    /// maintenance (esp. the coord GiST) otherwise dominates.</summary>
    private const long MinRowsToCycle = 1_000_000;

    public NpgsqlIndexCycle(NpgsqlDataSource ds, ILogger log)
    {
        _ds = ds;
        _log = log;
    }

    public static async Task RecoverAsync(NpgsqlDataSource ds, ILogger log, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var pending = new List<(string Name, string Def)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT index_name, index_def FROM laplace.index_cycle_journal";
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                pending.Add((rd.GetString(0), rd.GetString(1)));
        }
        foreach (var (name, def) in pending)
        {
            log.LogWarning("INDEX_CYCLE recovery: re-creating {Index} from journal", name);
            await using var mk = conn.CreateCommand();
            mk.CommandTimeout = 0;
            mk.CommandText = def.Replace("CREATE INDEX", "CREATE INDEX IF NOT EXISTS", StringComparison.Ordinal);
            await mk.ExecuteNonQueryAsync(ct);
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM laplace.index_cycle_journal WHERE index_name = $1";
            del.Parameters.AddWithValue(name);
            await del.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Decides per table and drops secondaries where the staged volume says
    /// cycling wins. Runs OUTSIDE the apply transaction (drops must commit
    /// so the COPY backends see index-free heaps) — correct because the
    /// caller holds the apply advisory lock for the whole window.
    /// </summary>
    public async Task<bool> BeginAsync(
        IReadOnlyList<(string Table, long StagedRows)> tables, CancellationToken ct)
    {
        if (!Enabled) return false;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        foreach (var (table, staged) in tables)
        {
            if (staged < MinRowsToCycle) continue;

            long live;
            await using (var est = conn.CreateCommand())
            {
                est.CommandText = "SELECT reltuples::bigint FROM pg_class WHERE oid = ($1)::regclass";
                est.Parameters.AddWithValue($"laplace.{table}");
                live = (long)(await est.ExecuteScalarAsync(ct) ?? 0L);
            }

            var secondaries = new List<(string Name, string Def)>();
            await using (var list = conn.CreateCommand())
            {
                list.CommandText =
                    "SELECT c.relname, pg_get_indexdef(i.indexrelid) "
                    + "FROM pg_index i "
                    + "JOIN pg_class c ON c.oid = i.indexrelid "
                    + "WHERE i.indrelid = ($1)::regclass "
                    + "  AND NOT i.indisprimary AND NOT i.indisunique AND NOT i.indisexclusion";
                list.Parameters.AddWithValue($"laplace.{table}");
                await using var rd = await list.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    secondaries.Add((rd.GetString(0), rd.GetString(1)));
            }

            foreach (var (name, def) in secondaries)
            {
                await using (var journal = conn.CreateCommand())
                {
                    journal.CommandText =
                        "INSERT INTO laplace.index_cycle_journal (index_name, table_name, index_def) "
                        + "VALUES ($1, $2, $3) ON CONFLICT (index_name) DO NOTHING";
                    journal.Parameters.AddWithValue(name);
                    journal.Parameters.AddWithValue(table);
                    journal.Parameters.AddWithValue(def);
                    await journal.ExecuteNonQueryAsync(ct);
                }
                await using (var drop = conn.CreateCommand())
                {
                    drop.CommandTimeout = 0;
                    drop.CommandText = $"DROP INDEX IF EXISTS laplace.\"{name}\"";
                    await drop.ExecuteNonQueryAsync(ct);
                }
                _dropped.Add((name, def));
            }
            if (secondaries.Count > 0)
                _log.LogInformation(
                    "INDEX_CYCLE {Table}: dropped {Count} secondary index(es) for {Staged:N0}-row bulk load (live≈{Live:N0})",
                    table, secondaries.Count, staged, live);
        }
        return _dropped.Count > 0;
    }

    /// <summary>Rebuilds every dropped index — one connection per index,
    /// parallel maintenance workers inside each build.</summary>
    public async Task FinishAsync(CancellationToken ct)
    {
        if (_dropped.Count == 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int workers = Math.Min(_dropped.Count, NpgsqlSubstrateWriter.ApplyParallelism);
        int next = -1;
        await Laplace.Engine.Core.CpuTopology.RunPinnedAsyncParallel(workers, async (_, token) =>
        {
            for (int i = Interlocked.Increment(ref next); i < _dropped.Count;
                 i = Interlocked.Increment(ref next))
            {
                var (name, def) = _dropped[i];
                var one = System.Diagnostics.Stopwatch.StartNew();
                await using var conn = await _ds.OpenConnectionAsync(token);
                await using (var guc = conn.CreateCommand())
                {
                    guc.CommandText =
                        "SET maintenance_work_mem = '2GB'; "
                        + "SET max_parallel_maintenance_workers = 4";
                    await guc.ExecuteNonQueryAsync(token);
                }
                await using (var mk = conn.CreateCommand())
                {
                    mk.CommandTimeout = 0;
                    mk.CommandText = def.Replace(
                        "CREATE INDEX", "CREATE INDEX IF NOT EXISTS", StringComparison.Ordinal);
                    await mk.ExecuteNonQueryAsync(token);
                }
                await using (var del = conn.CreateCommand())
                {
                    del.CommandText = "DELETE FROM laplace.index_cycle_journal WHERE index_name = $1";
                    del.Parameters.AddWithValue(name);
                    await del.ExecuteNonQueryAsync(token);
                }
                _log.LogInformation("INDEX_CYCLE rebuilt {Index} in {Ms:N0}ms", name, one.ElapsedMilliseconds);
            }
        }, ct);

        sw.Stop();
        _log.LogInformation(
            "INDEX_CYCLE complete: {Count} index(es) rebuilt in {Ms:N0}ms across {Workers} connection(s)",
            _dropped.Count, sw.ElapsedMilliseconds, workers);
        _dropped.Clear();
    }
}
