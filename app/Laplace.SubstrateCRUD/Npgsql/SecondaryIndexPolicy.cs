using System.Text.RegularExpressions;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Bulk-load secondary-index policy for substrate tables.
///
/// Index-free bulk load is correct ONLY when seeding an EMPTY table: dropping the secondary
/// indexes forces a full-table rebuild bounded by the WHOLE corpus, not by the incoming source.
/// On a populated (live, large) substrate that rebuild dwarfs a bounded load, holds locks, and
/// de-indexes status queries — so we drop+rebuild only when the table is empty; otherwise we keep
/// indexes live and let the bounded load maintain them incrementally.
///
/// The rebuild is structural, not happy-path: <see cref="SuspendForBulkLoadAsync"/> returns a
/// scope whose <see cref="SecondaryIndexScope.DisposeAsync"/> rebuilds whatever it dropped no
/// matter how the load exits (success, failure rows, or a thrown exception). Rebuilding only on
/// success is what once stranded <c>consensus</c> index-free in production: a throwing model ingest
/// left it with only its primary key, forcing recall/neighbors into seq-scans over tens of millions
/// of rows. Callers may call <see cref="SecondaryIndexScope.RebuildAsync"/> explicitly to narrate
/// timing; dispose is then a no-op safety net.
/// </summary>
public sealed class SecondaryIndexPolicy
{
    // Substrate tables live in the laplace schema. Table identifiers cannot be passed as bind
    // parameters in EXISTS(SELECT 1 FROM laplace.<t>), so validate before interpolating.
    private static readonly Regex SafeTable = new("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    private readonly NpgsqlDataSource _ds;
    private readonly ILogger _log;

    public SecondaryIndexPolicy(NpgsqlDataSource dataSource, ILogger? logger = null)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _log = logger ?? NullLogger.Instance;
    }

    private static string RequireSafeTable(string table)
    {
        if (string.IsNullOrEmpty(table) || !SafeTable.IsMatch(table))
            throw new ArgumentException($"unsafe substrate table identifier: '{table}'", nameof(table));
        return table;
    }

    /// <summary>
    /// O(1) existence probe — stops at the first row. Cannot be fooled by stale planner statistics
    /// the way <c>reltuples</c> can, so the "is this an empty first-seed?" decision is always correct.
    /// </summary>
    public async Task<bool> TableHasAnyRowsAsync(string table, CancellationToken ct = default)
    {
        RequireSafeTable(table);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM laplace.{table})";
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is bool b && b;
    }

    /// <summary>
    /// Opens a bulk-load scope for <paramref name="table"/>: if the table is empty, drops its
    /// secondary (non-primary, non-unique) indexes for an index-free load; if populated, keeps
    /// them live. The returned scope rebuilds exactly what it dropped on dispose.
    /// </summary>
    public async Task<SecondaryIndexScope> SuspendForBulkLoadAsync(string table, CancellationToken ct = default)
    {
        RequireSafeTable(table);
        bool populated = await TableHasAnyRowsAsync(table, ct);
        var dropped = populated
            ? new List<string>()
            : await DropSecondaryIndexesAsync(_ds, table, ct);
        return new SecondaryIndexScope(_ds, _log, table, populated, dropped);
    }

    /// <summary>
    /// Drops every secondary (non-primary, non-unique) index on <c>laplace.&lt;table&gt;</c> and
    /// returns their <c>CREATE INDEX</c> definitions so they can be rebuilt verbatim.
    /// </summary>
    internal static async Task<List<string>> DropSecondaryIndexesAsync(
        NpgsqlDataSource ds, string table, CancellationToken ct)
    {
        RequireSafeTable(table);
        var names = new List<string>();
        var defs = new List<string>();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using (var q = conn.CreateCommand())
        {
            q.CommandText =
                "SELECT c.relname, pg_get_indexdef(i.indexrelid) "
                + "FROM pg_index i "
                + "JOIN pg_class c ON c.oid = i.indexrelid "
                + "JOIN pg_class t ON t.oid = i.indrelid "
                + "JOIN pg_namespace n ON n.oid = t.relnamespace "
                + "WHERE n.nspname = 'laplace' AND t.relname = $1 "
                + "  AND NOT i.indisprimary AND NOT i.indisunique";
            q.Parameters.AddWithValue(table);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                names.Add(r.GetString(0));
                defs.Add(r.GetString(1));
            }
        }
        foreach (var n in names)
        {
            await using var d = conn.CreateCommand();
            d.CommandTimeout = 0;
            d.CommandText = $"DROP INDEX IF EXISTS laplace.\"{n}\"";
            await d.ExecuteNonQueryAsync(ct);
        }
        return defs;
    }

    /// <summary>Rebuilds indexes from their <c>CREATE INDEX</c> definitions, with maintenance memory tuned.</summary>
    internal static async Task RebuildIndexesAsync(
        NpgsqlDataSource ds, IReadOnlyList<string> indexDefs, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using (var t = conn.CreateCommand())
        {
            t.CommandText =
                "SET maintenance_work_mem = '2GB'; "
                + "SET max_parallel_maintenance_workers = 4";
            await t.ExecuteNonQueryAsync(ct);
        }
        foreach (var def in indexDefs)
        {
            await using var c = conn.CreateCommand();
            c.CommandTimeout = 0;
            c.CommandText = def;
            await c.ExecuteNonQueryAsync(ct);
        }
    }
}

/// <summary>
/// A bulk-load scope returned by <see cref="SecondaryIndexPolicy.SuspendForBulkLoadAsync"/>.
/// Holds the secondary-index definitions dropped on entry and rebuilds them when the scope exits.
/// </summary>
public sealed class SecondaryIndexScope : IAsyncDisposable
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger _log;
    private List<string> _pending;

    internal SecondaryIndexScope(
        NpgsqlDataSource ds, ILogger log, string table, bool tableWasPopulated, List<string> droppedDefs)
    {
        _ds = ds;
        _log = log;
        Table = table;
        TableWasPopulated = tableWasPopulated;
        DroppedIndexDefs = droppedDefs;
        _pending = new List<string>(droppedDefs);
    }

    /// <summary>The table this scope governs.</summary>
    public string Table { get; }

    /// <summary>True when the table already held rows on entry, so no indexes were dropped.</summary>
    public bool TableWasPopulated { get; }

    /// <summary>The <c>CREATE INDEX</c> definitions dropped on entry (empty when the table was populated).</summary>
    public IReadOnlyList<string> DroppedIndexDefs { get; }

    /// <summary>True when this scope dropped at least one secondary index that must be rebuilt.</summary>
    public bool Dropped => DroppedIndexDefs.Count > 0;

    /// <summary>True once every dropped index has been rebuilt (or there were none to rebuild).</summary>
    public bool Rebuilt => _pending.Count == 0;

    /// <summary>
    /// Rebuilds any not-yet-rebuilt dropped indexes. Idempotent: a second call (e.g. from dispose
    /// after an explicit call) is a no-op.
    /// </summary>
    public async Task RebuildAsync(CancellationToken ct = default)
    {
        if (_pending.Count == 0) return;
        var defs = _pending;
        _pending = new List<string>();
        await SecondaryIndexPolicy.RebuildIndexesAsync(_ds, defs, ct);
    }

    /// <summary>Safety net: rebuilds whatever was dropped if a caller did not rebuild explicitly.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_pending.Count == 0) return;
        try
        {
            await RebuildAsync();
        }
        catch (Exception ex)
        {
            // Dispose must not throw over an in-flight exception; surface the rebuild failure loudly
            // instead — a stranded index-free table is the scar this policy exists to prevent.
            _log.LogError(ex, "B2: rebuild of {Count} secondary {Table} index(es) FAILED on scope dispose; "
                + "the table may be left index-free — rebuild manually", _pending.Count, Table);
        }
    }
}
