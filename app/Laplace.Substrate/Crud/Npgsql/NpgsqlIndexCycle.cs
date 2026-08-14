using global::Npgsql;
using Laplace.Engine.Core;
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
///
/// Concurrent CREATE INDEX is forbidden here. 2026-07-10 evidence: FinishAsync /
/// RecoverAsync previously ran up to ApplyParallelism connections, each with
/// full MemoryTopology.MaintenanceWorkMemBytes and ParallelMaintenanceWorkers,
/// while shared_buffers was ~25% of RAM. Two multi-GB builds
/// (attestations_relation_btree + consensus_object_btree) overlapped, spilled
/// many 1 GiB temp filesets, then a backend ACCESS_VIOLATIONed in
/// VCRUNTIME140.dll (1.6 GiB minidump) and tore the postmaster off SCM.
/// Parallelism belongs INSIDE one CREATE INDEX (max_parallel_maintenance_workers),
/// not across multiple CREATE INDEX sessions.
/// </summary>
public sealed class NpgsqlIndexCycle
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger _log;
    private readonly List<(string Name, string Def)> _dropped = new();

    /// <summary>
    /// Master switch, DEFAULT ON (2026-08-13; was opt-in). The old OFF default
    /// existed because cycling was all-or-nothing; the protection is now the
    /// volume gates themselves — <see cref="MinRowsToCycle"/> and
    /// <see cref="CycleMinLiveFraction"/> over CUMULATIVE run volume — so small
    /// runs never cycle regardless of the switch. Opt-in left every hand-run
    /// ingest unprotected: CI exported the flag, a shell `ingest-source.sh` did
    /// not, and the manual seed paid live secondary maintenance for every row
    /// (measured 2026-08-12: 21.3KB WAL and 5.5 pages dirtied per consensus
    /// insert; Wiktionary COPY at ~2.4k phys rows/s). One write path, one law:
    /// set <c>LAPLACE_INDEX_CYCLE=0</c> to opt OUT (e.g. a run that must keep
    /// every index online for concurrent serving); campaign mode
    /// (<c>drop-indexes</c> + <c>LAPLACE_INDEX_CYCLE_DEFER=1</c>) is unchanged.
    /// Journal RECOVERY stays active regardless.
    /// </summary>
    public static readonly bool Enabled = ReadEnabled();

    private static bool ReadEnabled()
    {
        var v = Environment.GetEnvironmentVariable("LAPLACE_INDEX_CYCLE");
        if (string.IsNullOrWhiteSpace(v)) return true;
        return EnvFlag.IsSet("LAPLACE_INDEX_CYCLE");
    }

    /// <summary>Hard cap: one CREATE INDEX session at a time (see class note).</summary>
    private const int MaxConcurrentIndexBuilds = 1;

    /// <summary>Cycle when staged rows ≥ this (absolute). The run-scoped cycle
    /// drops once and rebuilds once at run end, so a bulk source cycles even as
    /// an increment onto an already-large table — where per-row secondary
    /// maintenance (esp. the coord GiST) otherwise dominates.</summary>
    private const long MinRowsToCycle = 1_000_000;


    /// <summary>
    /// Indexes the cycle must NOT drop, comma-separated in LAPLACE_INDEX_CYCLE_KEEP.
    /// Cycling takes every plain secondary offline for the whole run — hours on a corpus
    /// seed — which turns every walk/hydrate/inspection query into an 89M-row scan while
    /// the ingest runs (and until recovery, if the run is killed). Naming the
    /// serving-critical indexes here keeps the database usable during bulk loads at some
    /// COPY cost. Empty (the default) preserves the tuned full-cycle behavior.
    /// </summary>
    private static readonly IReadOnlySet<string> KeepIndexes =
        (Environment.GetEnvironmentVariable("LAPLACE_INDEX_CYCLE_KEEP") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Cycle only when staged rows are at least this fraction of the table's LIVE cardinality —
    /// i.e. the apply is fresh-seed shaped. A cycle rebuilds the whole index over (live+staged)
    /// rows; below this fraction the apply is an INCREMENT and dropping an index that already
    /// covers `live` rows to rebuild it is pure loss — COPY's per-row maintenance of just the
    /// staged rows is cheaper AND keeps the index online. Default 1.0 ("staged rivals or exceeds
    /// cardinality", the class-note definition). Tune DOWN (LAPLACE_INDEX_CYCLE_MIN_FRACTION) for
    /// GiST/GIN-heavy tables where per-row maintenance dominates and cycling wins at a smaller
    /// fraction; 0 restores the old absolute-only behavior (always cycle at MinRowsToCycle).
    /// </summary>
    private static readonly double CycleMinLiveFraction =
        double.TryParse(
            Environment.GetEnvironmentVariable("LAPLACE_INDEX_CYCLE_MIN_FRACTION"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var frac) && frac >= 0
            ? frac : 1.0;

    /// <summary>
    /// Cross-step campaign mode. When set, secondaries dropped by ANY step stay down for the
    /// whole campaign: RecoverAsync does NOT auto-rebuild the journal at run start (the drops
    /// are intentional, not a crash), and each step COPYs into index-free heaps. The terminal
    /// `ingest index-rebuild` calls RebuildJournaledAsync ONCE at the end. This turns N per-step
    /// drop/rebuild cycles into one drop and one rebuild across the whole seed.
    /// </summary>
    public static readonly bool Deferred =
        EnvFlag.IsSet("LAPLACE_INDEX_CYCLE_DEFER");

    public NpgsqlIndexCycle(NpgsqlDataSource ds, ILogger log)
    {
        _ds = ds;
        _log = log;
    }

    /// <summary>
    /// Per-connection GUCs for one index build. Outer concurrency is
    /// <see cref="MaxConcurrentIndexBuilds"/>; inner parallel workers stay on
    /// for a single sort-based build, with maintenance_work_mem left at the
    /// topology value because only one build runs at a time.
    /// </summary>
    private static string IndexBuildGucs() =>
        "SET search_path = laplace, public; "
        + $"SET maintenance_work_mem = '{Laplace.Engine.Core.MemoryTopology.MaintenanceWorkMemBytes >> 20}MB'; "
        + $"SET max_parallel_maintenance_workers = {Laplace.Engine.Core.CpuTopology.ParallelMaintenanceWorkers}";

    /// <summary>
    /// Auto-rebuild whatever a crashed prior run left journaled, BEFORE this run cycles.
    /// In <see cref="Deferred"/> campaign mode this is a no-op: the journaled drops are
    /// intentional (held down across steps) and the terminal `ingest index-rebuild` rebuilds
    /// them once via <see cref="RebuildJournaledAsync"/>.
    /// </summary>
    public static async Task RecoverAsync(NpgsqlDataSource ds, ILogger log, CancellationToken ct)
    {
        if (Deferred)
        {
            log.LogInformation(
                "INDEX_CYCLE deferred (LAPLACE_INDEX_CYCLE_DEFER) — leaving journaled drops down; "
                + "run `ingest index-rebuild` at campaign end to rebuild once");
            return;
        }
        await RebuildJournaledAsync(ds, log, ct);
    }

    /// <summary>
    /// Rebuild every index currently named in laplace.index_cycle_journal — one CREATE INDEX at
    /// a time — and clear each journal row as its rebuild commits. This is the campaign-end
    /// rebuild and the crash-recovery rebuild; it ignores <see cref="Deferred"/> (it IS the
    /// deliberate rebuild the defer flag was waiting for).
    /// </summary>
    public static async Task RebuildJournaledAsync(NpgsqlDataSource ds, ILogger log, CancellationToken ct)
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
        if (pending.Count == 0) return;

        // Serial rebuild only — see class note on the 2026-07-10 VCRUNTIME crash.
        int workers = Math.Min(pending.Count, MaxConcurrentIndexBuilds);
        int next = -1;
        await Laplace.Engine.Core.CpuTopology.RunPinnedAsyncParallel(workers, async (_, token) =>
        {
            for (int i = Interlocked.Increment(ref next); i < pending.Count;
                 i = Interlocked.Increment(ref next))
            {
                var (name, def) = pending[i];
                log.LogWarning("INDEX_CYCLE recovery: re-creating {Index} from journal", name);
                await using var conn = await ds.OpenConnectionAsync(token);
                await using (var guc = conn.CreateCommand())
                {
                    guc.CommandText = IndexBuildGucs();
                    await guc.ExecuteNonQueryAsync(token);
                }
                await DropIfInvalidAsync(conn, name, token);
                await using (var mk = conn.CreateCommand())
                {
                    mk.CommandTimeout = 0;
                    mk.CommandText = def.Replace("CREATE INDEX", "CREATE INDEX IF NOT EXISTS", StringComparison.Ordinal);
                    await mk.ExecuteNonQueryAsync(token);
                }
                await using (var del = conn.CreateCommand())
                {
                    del.CommandText = "DELETE FROM laplace.index_cycle_journal WHERE index_name = $1";
                    del.Parameters.AddWithValue(name);
                    await del.ExecuteNonQueryAsync(token);
                }
            }
        }, ct);
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

            // Ratio gate: cycle only when the staged volume is fresh-seed shaped (a large
            // fraction of the live cardinality). On an increment onto a big table, rebuilding
            // the whole (live+staged) index to add `staged` costs far more than letting COPY
            // maintain the existing index per-staged-row — and it keeps the index online.
            // live <= 0 (fresh / never-analyzed table) always cycles.
            if (live > 0 && CycleMinLiveFraction > 0 && staged < (long)(live * CycleMinLiveFraction))
            {
                _log.LogInformation(
                    "INDEX_CYCLE {Table}: NOT cycling — {Staged:N0} staged is {Frac:P1} of {Live:N0} live "
                    + "(< {Min:P0} threshold); incremental COPY maintenance beats a full rebuild",
                    table, staged, (double)staged / live, live, CycleMinLiveFraction);
                continue;
            }

            var secondaries = await ListPlainSecondariesAsync(conn, table, ct);
            int droppedHere = 0;
            foreach (var (name, def) in secondaries)
            {
                if (KeepIndexes.Contains(name))
                {
                    _log.LogInformation("INDEX_CYCLE {Table}: keeping {Index} (LAPLACE_INDEX_CYCLE_KEEP)", table, name);
                    continue;
                }
                try
                {
                    await JournalAndDropAsync(conn, table, name, def, ct);
                }
                catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.LockNotAvailable)
                {
                    // Whoever holds the conflicting lock holds it on the table,
                    // so the remaining secondaries would each burn the same
                    // timeout. COPY proceeds with this table's indexes live.
                    _log.LogWarning(
                        "INDEX_CYCLE {Table}: lock not acquired in 5s for {Index} — skipping cycle for this table, COPY keeps live indexes",
                        table, name);
                    break;
                }
                _dropped.Add((name, def));
                droppedHere++;
            }
            if (droppedHere > 0)
                _log.LogInformation(
                    "INDEX_CYCLE {Table}: dropped {Count} secondary index(es) for {Staged:N0}-row bulk load (live≈{Live:N0})",
                    table, droppedHere, staged, live);
        }
        return _dropped.Count > 0;
    }

    /// <summary>Rebuilds every dropped index — one CREATE INDEX at a time.
    /// Parallel maintenance workers run inside that single build only.</summary>
    public async Task FinishAsync(CancellationToken ct)
    {
        if (_dropped.Count == 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int workers = Math.Min(_dropped.Count, MaxConcurrentIndexBuilds);
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
                    guc.CommandText = IndexBuildGucs();
                    await guc.ExecuteNonQueryAsync(token);
                }
                await DropIfInvalidAsync(conn, name, token);
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

    /// <summary>
    /// Campaign entry point: drop + journal EVERY plain secondary on the given tables up front,
    /// regardless of staged volume. Paired with <see cref="Deferred"/> (so mid-campaign runs do
    /// not auto-rebuild) and a terminal <see cref="RebuildJournaledAsync"/>, this is the
    /// "drop once, ingest everything across N steps, rebuild once" load. Idempotent: an
    /// already-dropped index simply does not appear in pg_index. Returns the count dropped.
    /// </summary>
    public static async Task<int> DropSecondariesAsync(
        NpgsqlDataSource ds, ILogger log, IReadOnlyList<string> tables, CancellationToken ct)
    {
        int total = 0;
        await using var conn = await ds.OpenConnectionAsync(ct);
        foreach (var table in tables)
        {
            var secondaries = await ListPlainSecondariesAsync(conn, table, ct);
            int here = 0;
            foreach (var (name, def) in secondaries)
            {
                if (KeepIndexes.Contains(name))
                {
                    log.LogInformation("INDEX_CYCLE {Table}: keeping {Index} (LAPLACE_INDEX_CYCLE_KEEP)", table, name);
                    continue;
                }
                try
                {
                    await JournalAndDropAsync(conn, table, name, def, ct);
                }
                catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.LockNotAvailable)
                {
                    log.LogWarning(
                        "INDEX_CYCLE {Table}: lock not acquired in 5s for {Index} — skipping remaining secondaries on this table",
                        table, name);
                    break;
                }
                here++;
            }
            if (here > 0)
                log.LogInformation("INDEX_CYCLE {Table}: dropped {Count} secondary index(es) up front (campaign)", table, here);
        }
        total = tables.Count;
        log.LogInformation("INDEX_CYCLE campaign drop across {Tables} table(s) — journaled for one rebuild at campaign end", total);
        return total;
    }

    /// <summary>
    /// An INVALID index of the target name (a killed recursive build, or a shell
    /// from a pre-fix ON ONLY rebuild) satisfies CREATE INDEX IF NOT EXISTS and
    /// discharges the journal row with zero children built — drop it first so the
    /// create actually runs. A VALID index passes through untouched.
    /// </summary>
    private static async Task DropIfInvalidAsync(
        NpgsqlConnection conn, string name, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText =
            "SELECT NOT i.indisvalid FROM pg_index i "
            + "JOIN pg_class c ON c.oid = i.indexrelid "
            + "JOIN pg_namespace n ON n.oid = c.relnamespace "
            + "WHERE n.nspname = 'laplace' AND c.relname = $1";
        probe.Parameters.AddWithValue(name);
        if (await probe.ExecuteScalarAsync(ct) is not true) return;

        await using var drop = conn.CreateCommand();
        drop.CommandTimeout = 0;
        drop.CommandText = $"DROP INDEX IF EXISTS laplace.\"{name}\"";
        await drop.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<(string Name, string Def)>> ListPlainSecondariesAsync(
        NpgsqlConnection conn, string table, CancellationToken ct)
    {
        // Partitioned heaps (physicalities HASH, entities LIST, attestations
        // LIST→HASH, consensus LIST) attach secondaries on LEAF relations.
        // Parent-only listing left every partition GiST/GIN/btree up — measured
        // Wiktionary COPY physicalities ~2.4k rows/s with indexes live vs the
        // heap path the cycle was built for. Recurse pg_inherits so campaign
        // drop / BeginAsync actually clear the indexes COPY pays for.
        var secondaries = new List<(string, string)>();
        await using var list = conn.CreateCommand();
        // Roots only. Partitioned indexes attach leaf/mid indexes via
        // pg_inherits; DROP of the root removes the whole tree. Listing every
        // leaf hit 2BP01 (child before parent) and would journal orphans after
        // the parent DROP already removed them. Independent per-partition
        // indexes (no inhparent) are roots and still appear here.
        list.CommandText =
            "WITH RECURSIVE parts AS ("
            + "  SELECT ($1)::regclass AS oid "
            + "  UNION ALL "
            + "  SELECT i.inhrelid FROM pg_inherits i JOIN parts p ON i.inhparent = p.oid"
            + ") "
            // ON ONLY stripped at capture: pg_get_indexdef on a partitioned ROOT
            // emits the non-recursive form, and rebuilding that creates an empty
            // invalid shell with zero children instead of the index tree the DROP
            // removed. Both rebuild paths run this def verbatim.
            + "SELECT c.relname, replace(pg_get_indexdef(i.indexrelid), ' ON ONLY ', ' ON ') "
            + "FROM pg_index i "
            + "JOIN pg_class c ON c.oid = i.indexrelid "
            + "WHERE i.indrelid IN (SELECT oid FROM parts) "
            + "  AND NOT i.indisprimary AND NOT i.indisunique AND NOT i.indisexclusion "
            + "  AND NOT EXISTS ("
            + "    SELECT 1 FROM pg_inherits ih WHERE ih.inhrelid = i.indexrelid"
            + ") "
            + "ORDER BY c.relname";
        list.Parameters.AddWithValue($"laplace.{table}");
        await using var rd = await list.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            secondaries.Add((rd.GetString(0), rd.GetString(1)));
        return secondaries;
    }

    private static async Task JournalAndDropAsync(
        NpgsqlConnection conn, string table, string name, string def, CancellationToken ct)
    {
        // One transaction: a failed DROP must not leave a journal row for an
        // index that still exists (measured: entities_t0_type_id_idx journaled
        // then 2BP01 on DROP, orphaning the journal).
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var guard = conn.CreateCommand())
        {
            // Cycling is an optimization; its AccessExclusive DROP must never
            // queue unboundedly behind a reader. Unbounded wait wedged a whole
            // foundation seed for 22+ min (2026-08-13) when the apply's own
            // control tx held AccessShare on the target — callers catch 55P03
            // and proceed with the index live instead.
            guard.Transaction = tx;
            guard.CommandText = "SET LOCAL lock_timeout = '5s'";
            await guard.ExecuteNonQueryAsync(ct);
        }
        await using (var journal = conn.CreateCommand())
        {
            journal.Transaction = tx;
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
            drop.Transaction = tx;
            drop.CommandTimeout = 0;
            drop.CommandText = $"DROP INDEX IF EXISTS laplace.\"{name}\"";
            await drop.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
