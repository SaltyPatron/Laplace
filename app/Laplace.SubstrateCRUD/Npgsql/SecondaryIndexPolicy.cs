using System.Text.RegularExpressions;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.SubstrateCRUD.Npgsql;


















public sealed class SecondaryIndexPolicy
{
    
    
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

    
    
    
    
    public async Task<bool> TableHasAnyRowsAsync(string table, CancellationToken ct = default)
    {
        RequireSafeTable(table);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM laplace.{table})";
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is bool b && b;
    }

    
    
    
    
    
    public async Task<SecondaryIndexScope> SuspendForBulkLoadAsync(string table, CancellationToken ct = default)
    {
        RequireSafeTable(table);
        bool populated = await TableHasAnyRowsAsync(table, ct);
        // BULK seed mode (LAPLACE_BULK_FRESH=1): drop secondary indexes even on a POPULATED table. The
        // big seed sources (conceptnet ~100M rows, wiktionary) pour tens of millions of random-key
        // (BLAKE3) rows; into LIVE secondary indexes that is the page-split / write-amplification cliff
        // that collapses the insert rate as the table grows — the conceptnet slowdown. The id PK is
        // never dropped, so the dedup existence-check stays fast; dropping the SECONDARY indexes for the
        // bulk load and rebuilding after (SecondaryIndexScope.DisposeAsync) is pure win.
        bool bulk = string.Equals(Environment.GetEnvironmentVariable("LAPLACE_BULK_FRESH"), "1", StringComparison.Ordinal);
        var dropped = (populated && !bulk)
            ? new List<string>()
            : await DropSecondaryIndexesAsync(_ds, table, ct);
        return new SecondaryIndexScope(_ds, _log, table, populated, dropped);
    }

    
    
    
    
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

    
    public string Table { get; }

    
    public bool TableWasPopulated { get; }

    
    public IReadOnlyList<string> DroppedIndexDefs { get; }

    
    public bool Dropped => DroppedIndexDefs.Count > 0;

    
    public bool Rebuilt => _pending.Count == 0;

    
    
    
    
    public async Task RebuildAsync(CancellationToken ct = default)
    {
        if (_pending.Count == 0) return;
        var defs = _pending;
        _pending = new List<string>();
        await SecondaryIndexPolicy.RebuildIndexesAsync(_ds, defs, ct);
    }

    
    public async ValueTask DisposeAsync()
    {
        if (_pending.Count == 0) return;
        try
        {
            await RebuildAsync();
        }
        catch (Exception ex)
        {
            
            
            _log.LogError(ex, "B2: rebuild of {Count} secondary {Table} index(es) FAILED on scope dispose; "
                + "the table may be left index-free — rebuild manually", _pending.Count, Table);
        }
    }
}
