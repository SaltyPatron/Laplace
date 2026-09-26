using System.Buffers;
using System.Diagnostics;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The generic ingest order of a source.
/// <list type="number">
/// <item>Extraction: every record the source's decomposition produces (entities, their
/// physicalities, claims) is copied into the run's own staging schema as it is produced.
/// Records collide there; nothing touches the substrate and nothing is scored. When the
/// source's last file is done, staging holds everything the source said.</item>
/// <item>Load, once, over the complete staged set: the records converge by identity, each
/// with the hash partition it lands in, and a staged id equal to a stored id is that
/// entity; claims merged per id natively; consensus folded, one rating period per witness
/// per cell on prior standing, natively.</item>
/// <item>Landing: every touched leaf of entities, physicalities, attestations and consensus
/// is rebuilt on the side (its rows and its own indexes written without WAL) and one
/// transaction swaps them all in, so a source's compositions, claims and standing appear
/// together.</item>
/// </list>
/// PostgreSQL stores and sorts; native code merges and folds; this class sequences the
/// operations and moves COPY streams. No record becomes a managed object.
/// </summary>
public sealed class StagedSourceWriter : ISubstrateWriter, IConsensusFoldMetrics, IAsyncDisposable
{
    private readonly ISubstrateWriter _inner;
    private readonly NpgsqlDataSource _ds;
    private readonly PostgresWriteDurability _durability;
    private readonly int _lanes;
    private readonly ILogger _log;
    private readonly string _stage = "laplace_stage_" + Guid.NewGuid().ToString("N")[..16];
    private readonly SemaphoreSlim _create = new(1, 1);
    private readonly List<SubstrateChange> _held = [];
    private bool _staged;
    private long _observations, _cells;
    private int _epochRoute = -1;
    private int _modulus;

    // PostgreSQL combines a hash-partition key's hash with zero as hash + this constant
    // (hash_combine64); modulo a power of two only its low bits matter.
    private const ulong HashPartitionCombine = 0x49a0f4dd15e5a8e3UL;

    public StagedSourceWriter(ISubstrateWriter inner, NpgsqlDataSource dataSource,
        PostgresWriteDurability durability, int lanes, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lanes);
        _durability = durability;
        _lanes = lanes;
        _log = logger ?? NullLogger.Instance;
    }

    public long ObservationsAccumulated => Interlocked.Read(ref _observations)
        + ((_inner as IConsensusFoldMetrics)?.ObservationsAccumulated ?? 0);

    public long CellsFolded => Interlocked.Read(ref _cells)
        + ((_inner as IConsensusFoldMetrics)?.CellsFolded ?? 0);

    // Writes outside the source's stream (vocabulary and provenance at initialization)
    // keep their own writer.
    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        => _inner.ApplyAsync(change, ct);

    public Task<ApplyResult> ApplyManyAsync(IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        => _inner.ApplyManyAsync(changes, ct);

    public Task<ApplyResult> AppendAsync(IReadOnlyList<SubstrateChange> changes, Hash128 sourceId,
        CancellationToken ct = default) => _inner.AppendAsync(changes, sourceId, ct);

    public Task<ApplyResult> ApplyWorkingSetAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyWorkingSetAsync([change], ct);

    public async Task<ApplyResult> ApplyWorkingSetAsync(IReadOnlyList<SubstrateChange> changes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        // Testimony that folds from transient inputs cannot wait for the load.
        if (changes.Any(static change => !change.EphemeralFoldInputs.IsDefaultOrEmpty
                                         || !change.TestimonyWalks.IsDefaultOrEmpty))
            return await _inner.ApplyWorkingSetAsync(changes, ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        await EnsureStageAsync(ct).ConfigureAwait(false);
        var stages = new List<IntentStage>();
        var owned = new List<IntentStage>();
        long entities = 0, physicalities = 0, attestations = 0;
        try
        {
            foreach (SubstrateChange change in changes)
            {
                if (change.Entities.Length > 0 || change.Physicalities.Length > 0 || change.Attestations.Length > 0)
                {
                    IntentStage? managed = NpgsqlSubstrateWriter.BuildManagedStage([change],
                        change.Entities.Length, change.Physicalities.Length, change.Attestations.Length, ct);
                    if (managed is not null) { stages.Add(managed); owned.Add(managed); }
                }
                if (!change.IntentStages.IsDefaultOrEmpty)
                    foreach (IntentStage stage in change.IntentStages)
                        if (!stage.IsInvalid && !stage.IsClosed) { stages.Add(stage); owned.Add(stage); }
                if (change.HasCompletions || !change.CanonicalNames.IsDefaultOrEmpty)
                    lock (_held)
                        _held.Add(change with { Entities = [], Physicalities = [], Attestations = [], IntentStages = [] });
            }
            foreach (IntentStage stage in stages)
            {
                entities += stage.EntityCount;
                physicalities += stage.PhysicalityCount;
                attestations += stage.AttestationCount;
            }
            if (stages.Count > 0)
            {
                await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
                await StageTableAsync(conn, "stage.entity_copy", stages, IntentStageTable.Entities, ct).ConfigureAwait(false);
                await StageTableAsync(conn, "stage.physicality_copy", stages, IntentStageTable.Physicalities, ct).ConfigureAwait(false);
                await StageTableAsync(conn, "stage.attestation_copy", stages, IntentStageTable.Attestations, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // Ownership of a change's native records transferred with the change.
            foreach (IntentStage stage in owned) stage.Dispose();
        }
        return new ApplyResult(checked((int)entities), 0, checked((int)physicalities), 0,
            checked((int)attestations), 0, 3, sw.Elapsed, TrunkShortcircuitHit: false);
    }

    public Task<ApplyResult> ApplyWorkingSetAsync(IReadOnlyList<SubstrateChange> changes,
        Func<CancellationToken, ValueTask> precommitVerifier, CancellationToken ct = default)
        => _inner.ApplyWorkingSetAsync(changes, precommitVerifier, ct);

    public Task DrainFoldsAsync() => _inner.DrainFoldsAsync();

    public Task CompleteFileAsync(string fileLabel, CancellationToken ct = default)
        => _inner.CompleteFileAsync(fileLabel, ct);

    public Task<(int Entities, int Physicalities, int Attestations)> FinalizeSourceAsync(
        Hash128 sourceId, CancellationToken ct = default) => _inner.FinalizeSourceAsync(sourceId, ct);

    public Task BeginBulkRunAsync(CancellationToken ct = default) => _inner.BeginBulkRunAsync(ct);

    public Task CompleteBulkRunAsync(CancellationToken ct = default) => CompleteBulkRunAsync(null, ct);

    public async Task CompleteBulkRunAsync(Action<BulkRunCompletionPhase>? onPhase, CancellationToken ct = default)
    {
        onPhase?.Invoke(BulkRunCompletionPhase.ConsensusDrain);
        if (_staged)
        {
            await LoadAsync(ct).ConfigureAwait(false);
            await DropStageAsync(ct).ConfigureAwait(false);
        }
        SubstrateChange[] held;
        lock (_held) { held = [.. _held]; _held.Clear(); }
        // Completion rows commit only after every record they vouch for is loaded.
        if (held.Length > 0) await _inner.ApplyWorkingSetAsync(held, ct).ConfigureAwait(false);
        await _inner.CompleteBulkRunAsync(onPhase, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_staged)
        {
            try { await DropStageAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "STAGED_LOAD could not drop {Stage}", _stage); }
        }
        _create.Dispose();
    }

    private async Task EnsureStageAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _staged)) return;
        await _create.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_staged) return;
            await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(conn, null, Sql("stage.create"), ct).ConfigureAwait(false);
            Volatile.Write(ref _staged, true);
            _log.LogInformation("STAGED_LOAD extraction stages into {Stage}", _stage);
        }
        finally
        {
            _create.Release();
        }
    }

    private async Task StageTableAsync(NpgsqlConnection conn, string copy, List<IntentStage> stages,
        IntentStageTable table, CancellationToken ct)
    {
        var blobs = new List<(IntPtr Ptr, long Len)>(stages.Count);
        foreach (IntentStage stage in stages)
        {
            (IntPtr ptr, long len) = stage.TupleBuffer(table);
            if (len > 0) blobs.Add((ptr, len));
        }
        if (blobs.Count == 0) return;
        await using var target = await conn.BeginRawBinaryCopyAsync(Sql(copy), ct).ConfigureAwait(false);
        await PgBinaryCopy.WriteNativeBlobsAsync(target, blobs, ct).ConfigureAwait(false);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        await using var control = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        // One load at a time reads and replaces the substrate's leaves.
        await LoadLockAsync(control, "pg_advisory_lock", ct).ConfigureAwait(false);
        try
        {
            await LoadHeldAsync(control, ct).ConfigureAwait(false);
        }
        finally
        {
            await LoadLockAsync(control, "pg_advisory_unlock", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task LoadLockAsync(NpgsqlConnection control, string function, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {function}($1, $2)", control) { CommandTimeout = 0 };
        cmd.Parameters.AddWithValue(AdvisoryTxLock.StagedLoadLockClass);
        cmd.Parameters.AddWithValue(AdvisoryTxLock.StagedLoadLockKey);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task LoadHeldAsync(NpgsqlConnection control, CancellationToken ct)
    {
        _modulus = await ModulusAsync(control, ct).ConfigureAwait(false);
        var total = Stopwatch.StartNew();
        var marks = new List<string>();
        // Each phase reports its wall time and the WAL the cluster wrote during it.
        long walMark = await WalAsync(control, ct).ConfigureAwait(false);
        long timeMark = 0;
        async Task Mark(string phase)
        {
            long wal = await WalAsync(control, ct).ConfigureAwait(false);
            long now = total.ElapsedMilliseconds;
            string mark = $"{phase}={now - timeMark}ms/{(wal - walMark) / (1024 * 1024)}MB";
            marks.Add(mark);
            _log.LogInformation("STAGED_PHASE {Stage} {Mark}", _stage, mark);
            walMark = wal;
            timeMark = now;
        }

        long stagedEntities, stagedPhysicalities, stagedClaims;
        await using (var census = new NpgsqlCommand(Sql("stage.census"), control) { CommandTimeout = 0 })
        await using (var reader = await census.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            await reader.ReadAsync(ct).ConfigureAwait(false);
            stagedEntities = reader.GetInt64(0);
            stagedPhysicalities = reader.GetInt64(1);
            stagedClaims = reader.GetInt64(2);
        }
        await SweepOrphanLeavesAsync(control, ct).ConfigureAwait(false);
        await Mark("census");

        // The staged records converged by identity, each routed to its hash partition.
        await ExecuteAsync(control, null, Sql("stage.structure"), ct).ConfigureAwait(false);
        await Mark("converge");

        // Claims the substrate has not admitted, every observation of one claim merged natively.
        await ExecuteAsync(control, null, Sql("stage.claim_table"), ct).ConfigureAwait(false);
        (ulong claimsIn, ulong claimsOut) = await TransformAsync(StagedLoadOperation.MergeClaims,
            Sql("stage.claims_out"), [Sql("stage.claim_copy")], ct).ConfigureAwait(false);
        await ExecuteAsync(control, null, Sql("stage.claim_parts"), ct).ConfigureAwait(false);
        await Mark("claim merge");

        // Scoring: one rating period per witness per cell on prior standing, lane by lane
        // over the partitions each lane owns; the result is staged with the claims.
        await ExecuteAsync(control, null, Sql("stage.cell_table") + "; " + Sql("stage.standing_table"), ct)
            .ConfigureAwait(false);
        long cells = 0, games = 0;
        await ForEachLaneAsync(async lane =>
        {
            (ulong c, ulong g) = await TransformAsync(StagedLoadOperation.Score, Lane(Sql("stage.score_out"), lane),
                [Sql("stage.cell_copy"), Sql("stage.standing_copy")], ct).ConfigureAwait(false);
            Interlocked.Add(ref cells, checked((long)c));
            Interlocked.Add(ref games, checked((long)g));
        }, connectionsPerLane: 3, ct).ConfigureAwait(false);
        Interlocked.Add(ref _cells, cells);
        Interlocked.Add(ref _observations, games);
        await Mark("scoring");

        // The new records of every table, by the partition they land in.
        await ExecuteAsync(control, null, Sql("stage.new_sets"), ct).ConfigureAwait(false);
        await Mark("new sets");

        // Every touched leaf is rebuilt on the side, then swapped in.
        IReadOnlyList<LeafWork> work = await PlanLeavesAsync(control, ct).ConfigureAwait(false);
        await ForEachAsync(work, leaf => BuildLeafAsync(leaf, ct), ct, connectionsPerItem: 2).ConfigureAwait(false);
        await Mark($"leaves({work.Count} rebuilt)");

        // One transaction makes every rebuilt leaf of the source visible at once: a stored
        // composition's subtree, its claims and their standing all appear together.
        await SwapAsync(control, work, ct).ConfigureAwait(false);
        await Mark("swap");

        _log.LogInformation(
            "STAGED_LOAD {Stage}: staged {E:N0}e/{P:N0}p/{A:N0}a; claims {CI:N0} new rows -> {CO:N0} merged; "
            + "scored {Cells:N0} cells from {Games:N0} games; {Marks}",
            _stage, stagedEntities, stagedPhysicalities, stagedClaims, claimsIn, claimsOut, cells, games,
            string.Join(" ", marks));
    }

    private sealed record LeafWork(string Table, string Leaf, int Modulus, int Remainder, long Rows,
        long NewRows, string Columns, IReadOnlyList<(string Name, string Definition, bool Primary)> Indexes);

    // Tables hashed on their key; a replacement leaf is bound to its partition by it.
    private static readonly IReadOnlyDictionary<string, string> PartitionKey = new Dictionary<string, string>
    {
        ["entities"] = "id", ["physicalities"] = "id", ["attestations"] = "subject_id", ["consensus"] = "subject_id",
    };

    // Every substrate table is hashed into the same power-of-two number of leaves.
    private static async Task<int> ModulusAsync(NpgsqlConnection control, CancellationToken ct)
    {
        var moduli = new HashSet<int>();
        await using (var cmd = new NpgsqlCommand(SqlCatalog.Get("stage.leaves").Text, control) { CommandTimeout = 0 })
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                moduli.Add(reader.GetInt32(2));
        if (moduli.Count != 1 || !System.Numerics.BitOperations.IsPow2(moduli.First()))
            throw new InvalidOperationException(
                "the staged load routes by one power-of-two hash modulus for every substrate table");
        return moduli.First();
    }

    private async Task<IReadOnlyList<LeafWork>> PlanLeavesAsync(NpgsqlConnection control, CancellationToken ct)
    {
        var leaves = new Dictionary<(string, int), (string Leaf, int Modulus, long Rows)>();
        await using (var cmd = new NpgsqlCommand(Sql("stage.leaves"), control) { CommandTimeout = 0 })
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                leaves[(reader.GetString(0), reader.GetInt32(3))] = (reader.GetString(1), reader.GetInt32(2), reader.GetInt64(4));
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(Sql("stage.table_columns"), control) { CommandTimeout = 0 })
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                columns[reader.GetString(0)] = reader.GetString(1);
        var counts = new List<(string Table, int Part, long Rows)>();
        await using (var cmd = new NpgsqlCommand(Sql("stage.part_counts"), control) { CommandTimeout = 0 })
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                counts.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        var work = new List<LeafWork>(counts.Count);
        foreach ((string table, int part, long rows) in counts)
        {
            (string leaf, int modulus, long existing) = leaves[(table, part)];
            var indexes = new List<(string, string, bool)>();
            await using (var cmd = new NpgsqlCommand(Sql("stage.leaf_indexes")
                .Replace("{leafname}", Quote(leaf)), control) { CommandTimeout = 0 })
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    indexes.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
            work.Add(new LeafWork(table, leaf, modulus, part, existing, rows, columns[table], indexes));
        }
        return work;
    }

    private string LeafSql(string name, LeafWork leaf, string replacement, string? target = null) => Sql(name)
        .Replace("{new}", replacement)
        .Replace("{table}", leaf.Table)
        .Replace("{leaf}", leaf.Leaf)
        .Replace("{target}", target ?? leaf.Columns)
        .Replace("{cols}", leaf.Columns)
        .Replace("{r}", leaf.Remainder.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private const string ConsensusColumns = "id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at";

    // The row streams a rebuilt leaf is filled from, in order, each with the columns it carries.
    private static (string Rows, string? Target)[] LeafRows(string table) => table switch
    {
        "entities" => [("stage.rows_entities_current", "id,tier,type_id,created_at,highway_mask"),
            ("stage.rows_entities", "id,tier,type_id,highway_mask")],
        "physicalities" => [("stage.rows_current", null), ("stage.rows_physicalities", null)],
        "attestations" => [("stage.rows_current", null), ("stage.rows_attestations", null)],
        "consensus" => [("stage.rows_consensus_current", ConsensusColumns), ("stage.rows_consensus", ConsensusColumns)],
        _ => throw new InvalidOperationException($"no leaf rows for {table}"),
    };

    private string Replacement(LeafWork leaf) => $"stg_{_stage[^8..]}_{leaf.Leaf}";

    // The replacement leaf: created in this transaction, so its rows and indexes are written
    // without WAL; it carries the leaf's own index definitions (the key as a constraint) and
    // its hash bound, so attaching it rebuilds nothing and scans nothing.
    private async Task BuildLeafAsync(LeafWork leaf, CancellationToken ct)
    {
        string replacement = Replacement(leaf);
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await BeginAsync(conn, ct).ConfigureAwait(false);
        await ExecuteAsync(conn, tx, LeafSql("stage.leaf_create", leaf, replacement), ct).ConfigureAwait(false);
        await using (var source = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false))
            foreach ((string rows, string? target) in LeafRows(leaf.Table))
            {
                await using var sink = await conn.BeginRawBinaryCopyAsync(
                    LeafSql("stage.leaf_fill", leaf, replacement, target), ct).ConfigureAwait(false);
                await using var reader = await source.BeginRawBinaryCopyAsync(
                    LeafSql(rows, leaf, replacement), ct).ConfigureAwait(false);
                await reader.CopyToAsync(sink, 1 << 20, ct).ConfigureAwait(false);
            }
        for (int k = 0; k < leaf.Indexes.Count; k++)
        {
            (string name, string definition, bool primary) = leaf.Indexes[k];
            string index = $"{replacement}_{k}";
            string create = definition.Replace($"INDEX {name} ON laplace.{leaf.Leaf} ",
                $"INDEX {index} ON laplace.{replacement} ", StringComparison.Ordinal);
            if (ReferenceEquals(create, definition) || create == definition)
                throw new InvalidOperationException($"index definition of {leaf.Leaf} is not in the expected form: {definition}");
            await ExecuteAsync(conn, tx, create, ct).ConfigureAwait(false);
            if (primary)
                await ExecuteAsync(conn, tx,
                    $"ALTER TABLE laplace.{replacement} ADD CONSTRAINT {index} PRIMARY KEY USING INDEX {index}", ct)
                    .ConfigureAwait(false);
        }
        await ExecuteAsync(conn, tx,
            $"ALTER TABLE laplace.{replacement} ADD CONSTRAINT {replacement}_bound CHECK "
            + $"(satisfies_hash_partition('laplace.{leaf.Table}'::regclass,{leaf.Modulus},{leaf.Remainder},{PartitionKey[leaf.Table]}))",
            ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        // Statistics belong to the table, so they carry through the swap.
        await ExecuteAsync(conn, null, $"ANALYZE laplace.{replacement}", ct).ConfigureAwait(false);
    }

    private async Task SwapAsync(NpgsqlConnection control, IReadOnlyList<LeafWork> rebuilt, CancellationToken ct)
    {
        if (rebuilt.Count == 0) return;
        await using var tx = await BeginAsync(control, ct).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(control, tx) { Timeout = 0 };
        void Add(string statement) => batch.BatchCommands.Add(new NpgsqlBatchCommand(statement));
        foreach (LeafWork leaf in rebuilt.OrderBy(static l => l.Table, StringComparer.Ordinal).ThenBy(static l => l.Remainder))
        {
            string replacement = Replacement(leaf);
            Add($"ALTER TABLE laplace.{leaf.Table} DETACH PARTITION laplace.{leaf.Leaf}");
            Add($"ALTER TABLE laplace.{leaf.Table} ATTACH PARTITION laplace.{replacement} "
                + $"FOR VALUES WITH (MODULUS {leaf.Modulus}, REMAINDER {leaf.Remainder})");
            // Leaves belong to the extension that defines the substrate; the replacement
            // takes the old leaf's membership as well as its name.
            Add($"ALTER EXTENSION laplace_substrate DROP TABLE laplace.{leaf.Leaf}");
            Add($"DROP TABLE laplace.{leaf.Leaf}");
            Add($"ALTER TABLE laplace.{replacement} RENAME TO {leaf.Leaf}");
            Add($"ALTER EXTENSION laplace_substrate ADD TABLE laplace.{leaf.Leaf}");
            Add($"ALTER TABLE laplace.{leaf.Leaf} DROP CONSTRAINT {replacement}_bound");
            for (int k = 0; k < leaf.Indexes.Count; k++)
                Add($"ALTER INDEX laplace.{replacement}_{k} RENAME TO {leaf.Indexes[k].Name}");
        }
        try
        {
            await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.BatchCommand is { } failed)
        {
            int at = batch.BatchCommands.IndexOf(failed);
            throw new InvalidOperationException(
                $"the leaf swap failed at statement {at + 1} of {batch.BatchCommands.Count}: {failed.CommandText}", ex);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // Replacement leaves a load never swapped in (the process ended before its swap) are
    // not partitions and hold nothing the substrate reads.
    private static async Task SweepOrphanLeavesAsync(NpgsqlConnection control, CancellationToken ct)
    {
        var orphans = new List<string>();
        await using (var cmd = new NpgsqlCommand(SqlCatalog.Get("stage.orphan_leaves").Text, control))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                orphans.Add(reader.GetString(0));
        foreach (string orphan in orphans)
            await ExecuteAsync(control, null, $"DROP TABLE laplace.{orphan}", ct).ConfigureAwait(false);
    }

    private static string Quote(string literal) => "'" + literal.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Reads one staged COPY stream through a native operation and writes each of its
    /// outputs to its COPY target. Every output is written in its own transaction and all
    /// commit only after the whole stream is processed, so a failed pass leaves nothing.
    /// </summary>
    private async Task<(ulong, ulong)> TransformAsync(StagedLoadOperation operation, string copyOut,
        string[] copyIns, CancellationToken ct)
    {
        using NativeStagedLoad native = NativeStagedLoad.Create(operation);
        await using var source = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connections = new NpgsqlConnection[copyIns.Length];
        var transactions = new NpgsqlTransaction[copyIns.Length];
        var sinks = new Stream[copyIns.Length];
        try
        {
            for (int i = 0; i < copyIns.Length; i++)
            {
                connections[i] = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
                transactions[i] = await connections[i].BeginTransactionAsync(ct).ConfigureAwait(false);
                sinks[i] = await connections[i].BeginRawBinaryCopyAsync(copyIns[i], ct).ConfigureAwait(false);
            }
            var output = new ArrayBufferWriter<byte>(1 << 20);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                await using var reader = await source.BeginRawBinaryCopyAsync(copyOut, ct).ConfigureAwait(false);
                while (true)
                {
                    int n = await reader.ReadAsync(buffer, ct).ConfigureAwait(false);
                    native.Feed(buffer.AsSpan(0, n), final: n == 0);
                    for (int i = 0; i < sinks.Length; i++)
                    {
                        output.ResetWrittenCount();
                        native.TakeOutput(i, output);
                        if (output.WrittenCount > 0)
                            await sinks[i].WriteAsync(output.WrittenMemory, ct).ConfigureAwait(false);
                    }
                    if (n == 0) break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            foreach (Stream sink in sinks) await sink.DisposeAsync().ConfigureAwait(false);
            foreach (NpgsqlTransaction tx in transactions) await tx.CommitAsync(ct).ConfigureAwait(false);
            return native.Counts;
        }
        finally
        {
            foreach (NpgsqlConnection? conn in connections)
                if (conn is not null) await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Lanes run as many at a time as the pool admits beside the control connection, so no
    // lane waits on a connection another lane holds.
    private async Task ForEachLaneAsync(Func<int, Task> lane, int connectionsPerLane, CancellationToken ct)
        => await ForEachAsync(Enumerable.Range(0, _lanes).ToArray(), lane, ct, connectionsPerLane).ConfigureAwait(false);

    private async Task ForEachAsync<T>(IReadOnlyList<T> items, Func<T, Task> body, CancellationToken ct,
        int connectionsPerItem = 1)
    {
        int pool = new NpgsqlConnectionStringBuilder(_ds.ConnectionString).MaxPoolSize;
        using var admitted = new SemaphoreSlim(Math.Max(1, (pool - 1) / connectionsPerItem));
        await Task.WhenAll(items.Select(async item =>
        {
            await admitted.WaitAsync(ct).ConfigureAwait(false);
            try { await RetryAsync(_ => body(item), ct).ConfigureAwait(false); }
            finally { admitted.Release(); }
        })).ConfigureAwait(false);
    }

    private async Task DropStageAsync(CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(conn, null, Sql("stage.drop"), ct).ConfigureAwait(false);
        _staged = false;
    }

    private string Sql(string name) => SqlCatalog.Get(name).Text
        .Replace("{stage}", _stage)
        .Replace("{partmask}", (_modulus - 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{partadd}", ((long)(HashPartitionCombine & (ulong)(_modulus - 1)))
            .ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{zero}", "'\\x00000000000000000000000000000000'::bytea")
        .Replace("{lanes}", _lanes.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{excluded}", Excluded);

    private static string Lane(string sql, int lane)
        => sql.Replace("{lane}", lane.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // Operational edges are stored but never scored or masked.
    private static readonly string Excluded =
        $"ARRAY['\\x{Convert.ToHexStringLower(FileEntity.MetadataRelationTypeId.ToBytes())}'::bytea]";

    private async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _epochRoute) < 0)
            {
                await using var probe = new NpgsqlCommand(
                    "SELECT to_regclass('laplace.apply_write_epoch') IS NOT NULL", conn, tx);
                bool present = (bool)(await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? false);
                Interlocked.CompareExchange(ref _epochRoute, present ? 1 : 0, -1);
            }
            // Every write transaction advances the write epoch before it writes.
            await using var guc = new NpgsqlCommand(
                NpgsqlSubstrateWriter.TransactionGucs(_durability)
                + (Volatile.Read(ref _epochRoute) == 1 ? SqlCatalog.Get("write.advance_epoch").Text : ""),
                conn, tx);
            await guc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return tx;
        }
        catch
        {
            await tx.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<long> ExecuteAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 0 };
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> WalAsync(NpgsqlConnection conn, CancellationToken ct)
        => await ScalarAsync(conn, "SELECT pg_wal_lsn_diff(pg_current_wal_lsn(), '0/0')::bigint", ct)
            .ConfigureAwait(false);

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    // A lane is its own transaction: a deadlock or serialization failure rolls back only
    // that lane, which then runs again from unchanged staging.
    private static async Task RetryAsync(Func<CancellationToken, Task> lane, CancellationToken ct)
    {
        var policy = Laplace.Ingestion.TransientErrorRetryPolicy.ConcurrencyRetry;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await lane(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt + 1 < policy.MaxAttempts && policy.IsTransient(ex))
            {
                TimeSpan delay = policy.DelayBeforeAttempt(attempt, Random.Shared);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }
}
