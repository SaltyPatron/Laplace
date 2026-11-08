using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using global::Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class ConsensusAccumulatingWriter
{
    /// <summary>
    /// Dispatches one delta onto the per-type fold lanes and the entity-sharded mask
    /// lanes, and returns the task that completes when all of this delta's segments
    /// have committed.
    ///
    /// Lanes are keyed by relation type, which is the safety boundary: consensus is
    /// LIST-partitioned by type_id, so two types never touch the same consensus row
    /// and their transactions neither contend nor deadlock. Glicko-2 accumulation is
    /// not commutative (folding A then B into a cell differs from B then A); a cell
    /// has exactly one type, so it lives on one lane, and each lane is a strict FIFO
    /// chain, so every cell sees its deltas in arrival order and consensus does not
    /// depend on scheduling.
    /// </summary>
    private Task DispatchDeltaAsync(
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> delta, CancellationToken ct)
    {
        // Sort by (type, edge id, subject): one total order, so every writer locks
        // rows in the same order and no lock cycle can form. Type leads because the
        // lanes take type runs. Edge id follows because consensus_pkey is btree
        // (id, type_id, subject_id) with id leading while consensus is HASH
        // (subject_id): each subpartition is then walked in ascending PK order
        // instead of at random pages. Glicko-2 order within a cell comes from the
        // per-type FIFO chain, never from position within a delta.
        var cells = new ((Hash128 S, Hash128 T, Hash128? O) Key, Hash128 Cid, Delta D)[delta.Count];
        int n = 0;
        foreach (var (key, d) in delta)
            cells[n++] = (key, ConsensusKeys.EdgeId(key.S, key.T, key.O ?? default), d);
        Array.Sort(cells, static (x, y) =>
        {
            int c = x.Key.T.CompareToBytewise(y.Key.T);
            if (c != 0) return c;
            c = x.Cid.CompareToBytewise(y.Cid);
            return c != 0 ? c : x.Key.S.CompareToBytewise(y.Key.S);
        });

        // Mask pairs from the same delta: (subject, type) + (object, type). Every
        // lane deposits as it folds. highway_mask_deposit OR-accumulates pairs the
        // fold already holds, with no consensus re-read, and the run-scoped pair
        // dedup writes each pair at most once per run.
        var maskPairs = new HashSet<(Hash128 Ent, Hash128 Typ)>(n * 2);
        foreach (var cell in cells)
        {
            maskPairs.Add((cell.Key.S, cell.Key.T));
            if (cell.Key.O is { } obj) maskPairs.Add((obj, cell.Key.T));
        }

        // Type runs over the (type-major) sorted cells: one lane segment each.
        var runs = new List<(Hash128 Type, int Off, int Len)>();
        for (int i = 0; i < cells.Length;)
        {
            int j = i + 1;
            while (j < cells.Length && cells[j].Key.T.Equals(cells[i].Key.T)) j++;
            runs.Add((cells[i].Key.T, i, j - i));
            i = j;
        }
        int[] runWidths = IngestSizing.AllocateFoldRunWidths(
            runs.Select(static r => r.Len).ToArray(), FoldConnections);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long folded = 0, masks = 0;
        var completions = new List<Task>(runs.Count + 1);

        // Lane bodies start on the pool (Task.Run); only the chain pointers are
        // swapped under the lock. Invoking an async body inside the lock would run
        // its synchronous prefix, which reaches Parallel.ForEachAsync and can open
        // connections and issue the first upsert, while the lock is held.
        lock (_laneLock)
        {
            for (int runIndex = 0; runIndex < runs.Count; runIndex++)
            {
                var run = runs[runIndex];
                var prior = _typeLanes.TryGetValue(run.Type, out var p) ? p : Task.CompletedTask;
                var r = run;
                int width = runWidths[runIndex];
                var next = Task.Run(() => FoldRunAfterAsync(prior, r, width));
                _typeLanes[run.Type] = next;
                completions.Add(next);
            }

            // Partition by a fixed entity hash: an entity lands in the same shard in
            // every delta, so shards stay disjoint across the whole run.
            var buckets = new List<(Hash128 Ent, Hash128 Typ)>?[MaskShards];
            foreach (var pair in maskPairs)
            {
                int shard = MaskShard(pair.Ent);
                (buckets[shard] ??= new()).Add(pair);
            }
            for (int shard = 0; shard < buckets.Length; shard++)
            {
                if (buckets[shard] is not { Count: > 0 } bucket) continue;
                Task prior = _maskLanes[shard];
                int ownedShard = shard;
                var ownedBucket = bucket;
                var next = Task.Run(async () =>
                {
                    await prior.ConfigureAwait(false);
                    await DepositAsync(ownedBucket, ownedShard).ConfigureAwait(false);
                });
                _maskLanes[shard] = next;
                completions.Add(next);
            }
        }

        return CompleteAsync();

        async Task CompleteAsync()
        {
            try
            {
                await Task.WhenAll(completions).ConfigureAwait(false);
            }
            finally
            {
                PruneCompletedLanes();
            }
            Interlocked.Add(ref _cellsFolded, folded);
            _log.LogInformation(
                "consensus fold: {Cells:N0} cells folded across {Lanes} type lanes, "
                + "{Masks:N0} masks deposited in {Ms:N0}ms ({Rate:N0} cells/s)",
                folded, runs.Count, masks, sw.ElapsedMilliseconds,
                folded / Math.Max(1e-3, sw.Elapsed.TotalSeconds));
        }

        async Task FoldRunAfterAsync(
            Task prior, (Hash128 Type, int Off, int Len) run, int connectionWidth)
        {
            // A faulted predecessor rethrows here: the lane stays poisoned and
            // every later segment on it (and the drain) sees the failure.
            await prior.ConfigureAwait(false);

            // Segments within the type run fold on parallel connections. Cells are
            // client-deduplicated, so no two segments touch the same consensus row:
            // row locks are disjoint and inserts unique by construction. Each segment
            // commits its own transaction. Connection width is allocated across all
            // type runs in this delta by run size; ChunkCells bounds only the
            // parameter-array residency of one command.
            int segLen = Math.Min(
                FoldSizing.ChunkCells,
                (run.Len + connectionWidth - 1) / connectionWidth);
            var segments = new List<(int Off, int Len)>();
            for (int s = run.Off; s < run.Off + run.Len; s += segLen)
                segments.Add((s, Math.Min(segLen, run.Off + run.Len - s)));

            await Parallel.ForEachAsync(segments,
                new ParallelOptions { MaxDegreeOfParallelism = connectionWidth, CancellationToken = ct },
                async (seg, token) =>
            {
                // Global budget, not the per-loop width: see _foldConnections.
                await _foldConnections.WaitAsync(token).ConfigureAwait(false);
                long backendStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                await using var conn = await _ds.OpenConnectionAsync(token);
                bool directRoute = await SupportsDirectConsensusRouteAsync(conn, token);
                bool epochBump = await SupportsApplyWriteEpochAsync(conn, token);
                await using var tx = await conn.BeginTransactionAsync(token);
                string foldQuery = directRoute
                    ? (epochBump ? "consensus.upsert_type_epoch" : "consensus.upsert_type")
                    : (epochBump ? "consensus.upsert_epoch" : "consensus.upsert");
                // Each catalog contract preserves its exact first parameter type
                // and keeps the epoch bump and fold in one preparable statement.
                await using var up = NpgsqlCatalog.Command(conn, tx, SqlCatalog.Get(foldQuery), parameters =>
                {
                    parameters.Add(new NpgsqlParameter
                    {
                        Value = directRoute ? Array.Empty<byte>() : Array.Empty<byte[]>(),
                        NpgsqlDbType = directRoute
                            ? NpgsqlDbType.Bytea
                            : NpgsqlDbType.Array | NpgsqlDbType.Bytea
                });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<byte[]>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<byte[]>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<DateTime>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                });
                up.CommandTimeout = 0;
                await up.PrepareAsync(token);
                long segFolded = 0;
                for (int off = seg.Off; off < seg.Off + seg.Len; off += FoldSizing.ChunkCells)
                {
                    int m = Math.Min(FoldSizing.ChunkCells, seg.Off + seg.Len - off);
                    var subjects = new byte[m][];
                    var objects = new byte[m][];
                    var phis = new long[m];
                    var opps = new long[m];
                    var games = new long[m];
                    var sums = new long[m];
                    var ts = new DateTime[m];
                    int periodCount = 0;
                    for (int i = 0; i < m; i++)
                        periodCount = checked(periodCount + PeriodCount(in cells[off + i].D));
                    var periodOffsets = new long[m + 1];
                    var periodOpps = new long[periodCount];
                    var periodPhis = new long[periodCount];
                    var periodGames = new long[periodCount];
                    var periodSums = new long[periodCount];
                    int periodAt = 0;
                    for (int i = 0; i < m; i++)
                    {
                        var cell = cells[off + i];
                        subjects[i] = cell.Key.S.ToBytes();
                        objects[i] = cell.Key.O?.ToBytes()!;
                        phis[i] = cell.D.FirstPeriod.PhiFp1e9;
                        opps[i] = cell.D.FirstPeriod.OpponentRatingFp1e9;
                        games[i] = cell.D.Games;
                        sums[i] = cell.D.SumScoreFp1e9;
                        ts[i] = TsFromUnixUs(cell.D.MaxTsUnixUs);
                        periodOffsets[i] = periodAt;
                        WritePeriods(in cell.D, periodOpps, periodPhis,
                                     periodGames, periodSums, ref periodAt);
                    }
                    periodOffsets[m] = periodAt;
                    if (directRoute)
                    {
                        up.Parameters[0].Value = run.Type.ToBytes();
                        up.Parameters[1].Value = subjects;
                        up.Parameters[2].Value = objects;
                        up.Parameters[3].Value = phis;
                        up.Parameters[4].Value = games;
                        up.Parameters[5].Value = sums;
                        up.Parameters[6].Value = ts;
                        up.Parameters[7].Value = opps;
                        up.Parameters[8].Value = periodOffsets;
                        up.Parameters[9].Value = periodOpps;
                        up.Parameters[10].Value = periodPhis;
                        up.Parameters[11].Value = periodGames;
                        up.Parameters[12].Value = periodSums;
                    }
                    else
                    {
                        var legacyTypes = new byte[m][];
                        Array.Fill(legacyTypes, run.Type.ToBytes());
                        up.Parameters[0].Value = subjects;
                        up.Parameters[1].Value = legacyTypes;
                        up.Parameters[2].Value = objects;
                        up.Parameters[3].Value = phis;
                        up.Parameters[4].Value = games;
                        up.Parameters[5].Value = sums;
                        up.Parameters[6].Value = ts;
                        up.Parameters[7].Value = opps;
                        up.Parameters[8].Value = periodOffsets;
                        up.Parameters[9].Value = periodOpps;
                        up.Parameters[10].Value = periodPhis;
                        up.Parameters[11].Value = periodGames;
                        up.Parameters[12].Value = periodSums;
                    }
                    segFolded += (long)(await up.ExecuteScalarAsync(token) ?? 0L);
                }
                await tx.CommitAsync(token);
                Interlocked.Add(ref folded, segFolded);
                }
                finally
                {
                    Interlocked.Add(ref _consensusBackendTicks,
                        System.Diagnostics.Stopwatch.GetTimestamp() - backendStarted);
                    Interlocked.Increment(ref _consensusUpsertCalls);
                    _foldConnections.Release();
                }
            });
        }

        async Task DepositAsync(
            List<(Hash128 Ent, Hash128 Typ)> pairs, int shard)
        {
            if (pairs.Count == 0) return;

            // Never resend a pair this run already deposited: masks only accrete, so
            // a deposited pair stays satisfied, and a server-side no-op still costs
            // index probes. This shard's FIFO is the synchronization; no pair in
            // another shard shares an entity.
            var deposited = _depositedMaskPairs[shard];
            lock (deposited)
            {
                pairs.RemoveAll(deposited.Contains);
            }
            if (pairs.Count == 0) return;
            var todo = pairs;

            // Sort by entity id before chunking. highway_mask_deposit acquires rows
            // ascending within one statement, but a transaction holding earlier
            // chunks' locks while running later chunks would form an AB/BA cycle with
            // a concurrent deposit if chunks interleaved id ranges. Sorted, every
            // deposit acquires ascending across its whole chunk sequence, and two
            // ascending acquirers cannot cycle. CompareToBytewise is memcmp order,
            // the same as the server-side bytea ORDER BY.
            todo.Sort(static (a, b) => a.Ent.CompareToBytewise(b.Ent));

            // One statement per chunk, one transaction per chunk. highway_mask_deposit
            // reduces pairs, relation bits and entity masks in native memory before
            // its indexed probe and keyed update; entity shards make concurrent calls
            // row-disjoint, and native code locks any externally-overlapping rows in
            // deterministic order.
            //
            // Committing per chunk bounds a concurrent deposit's wait on a shared row
            // to one chunk's work while acquisition stays globally ascending (chunk
            // k+1's ids all sort after chunk k's). OR-accumulate is idempotent: a
            // failure mid-sequence leaves earlier chunks committed and this deposit's
            // pairs unmarked below, so a resend re-runs each chunk as a server-side
            // no-op.
            long dep = 0;
            for (int off = 0; off < todo.Count; off += FoldSizing.ChunkCells)
            {
                int m = Math.Min(FoldSizing.ChunkCells, todo.Count - off);
                var pairEnts = new byte[m][];
                var pairTypes = new byte[m][];
                for (int i = 0; i < m; i++)
                {
                    pairEnts[i] = todo[off + i].Ent.ToBytes();
                    pairTypes[i] = todo[off + i].Typ.ToBytes();
                }

                await _foldConnections.WaitAsync(ct).ConfigureAwait(false);
                long backendStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                Interlocked.Add(ref _highwayMaskPairs, m);
                try
                {
                    await using var conn = await _ds.OpenConnectionAsync(ct);
                    bool epochBump = await SupportsApplyWriteEpochAsync(conn, ct);
                    await using var tx = await conn.BeginTransactionAsync(ct);
                    await using var mask = NpgsqlCatalog.Command(conn, tx,
                        epochBump ? "consensus.deposit_mask_epoch" : "consensus.deposit_mask", pairEnts, pairTypes);
                    mask.CommandTimeout = 0;
                    await mask.PrepareAsync(ct);
                    dep += (long)(await mask.ExecuteScalarAsync(ct) ?? 0L);
                    await tx.CommitAsync(ct);
                }
                finally
                {
                    Interlocked.Add(ref _highwayMaskBackendTicks,
                        System.Diagnostics.Stopwatch.GetTimestamp() - backendStarted);
                    Interlocked.Increment(ref _highwayMaskCalls);
                    _foldConnections.Release();
                }
            }

            long maskTotal = dep;
            Interlocked.Add(ref masks, maskTotal);

            // Mark AFTER every chunk commits. A failed lane stays poisoned and its
            // pairs stay resendable on a new writer/run.
            lock (deposited)
            {
                if (deposited.Count + todo.Count > DepositedMaskPairsCap / MaskShards)
                    deposited.Clear();
                deposited.UnionWith(todo);
            }
        }
    }

    private static int MaskShard(Hash128 entity)
        => (int)((uint)entity.GetHashCode() % (uint)MaskShards);

    /// <summary>
    /// Drops lanes whose chain completed successfully, so the map holds only live
    /// relation-type lanes. Faulted lanes are kept: the drain has to observe their
    /// exception.
    /// </summary>
    private void PruneCompletedLanes()
    {
        lock (_laneLock)
        {
            foreach (var key in _typeLanes.Where(kv => kv.Value.IsCompletedSuccessfully)
                                          .Select(kv => kv.Key).ToList())
                _typeLanes.Remove(key);
        }
    }

    private static DateTime TsFromUnixUs(long unixUs)
        => DateTime.UnixEpoch.AddTicks(unixUs * 10);

    private static TimeSpan StopwatchTime(long ticks)
        => TimeSpan.FromSeconds(ticks / (double)System.Diagnostics.Stopwatch.Frequency);

    /// <summary>
    /// Inline fold: dispatch and await, so the next read after an online apply sees
    /// the standing it produced.
    /// </summary>
    private Task UpsertDeltaAsync(
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> delta, CancellationToken ct)
        => DispatchDeltaAsync(delta, ct);

    /// <summary>
    /// Bulk fold: dispatch onto the lanes and return once the delta is queued, so the
    /// apply lane moves to the next working set. <c>_foldDepth</c> (from the fold
    /// plan) bounds outstanding deltas as memory backpressure. Each dispatched fold
    /// is tracked under every file label in <paramref name="changes"/> for
    /// <see cref="CompleteFileAsync"/>.
    /// </summary>
    private async Task EnqueueFoldAsync(
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> delta,
        IReadOnlyList<SubstrateChange> changes,
        CancellationToken ct)
    {
        await _foldDepth.WaitAsync(ct);
        Task dispatched;
        try
        {
            Interlocked.CompareExchange(
                ref _foldSpanStarted,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                comparand: 0);
            dispatched = DispatchDeltaAsync(delta, ct);
        }
        catch
        {
            _foldDepth.Release();
            throw;
        }

        var tracked = Release(dispatched);
        lock (_foldChainLock)
        {
            _outstanding.Add(tracked);
            foreach (string owner in changes
                         .Select(static change => change.Metadata.FileLabel)
                         .Where(static label => !string.IsNullOrWhiteSpace(label))
                         .Select(static label => label!)
                         .Distinct(StringComparer.Ordinal))
            {
                if (!_fileFolds.TryGetValue(owner, out var owned))
                    _fileFolds.Add(owner, owned = new List<Task>());
                owned.Add(tracked);
            }
        }

        async Task Release(Task fold)
        {
            try { await fold.ConfigureAwait(false); }
            finally { _foldDepth.Release(); }
        }
    }

    /// <summary>
    /// Every fold dispatched and not yet observed. Completed entries are swept on
    /// each snapshot; faulted ones are retained until a drain or the next apply
    /// observes them, so a background fold failure always surfaces.
    /// </summary>
    private readonly List<Task> _outstanding = new();
    private readonly Dictionary<string, List<Task>> _fileFolds = new(StringComparer.Ordinal);

    public async Task CompleteFileAsync(string fileLabel, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileLabel);
        Task[] owned;
        lock (_foldChainLock)
        {
            if (!_fileFolds.TryGetValue(fileLabel, out var tasks))
                return;
            tasks.RemoveAll(static task => task.IsCompletedSuccessfully);
            if (tasks.Count == 0)
            {
                _fileFolds.Remove(fileLabel);
                return;
            }
            owned = tasks.ToArray();
        }
        await Task.WhenAll(owned).WaitAsync(ct).ConfigureAwait(false);
        lock (_foldChainLock)
        {
            if (!_fileFolds.TryGetValue(fileLabel, out var tasks))
                return;
            tasks.RemoveAll(static task => task.IsCompletedSuccessfully);
            if (tasks.Count == 0)
                _fileFolds.Remove(fileLabel);
        }
    }

    private Task[] SnapshotFolds()
    {
        lock (_foldChainLock)
        {
            _outstanding.RemoveAll(t => t.IsCompletedSuccessfully);
            var lanes = new List<Task>(_outstanding);
            lock (_laneLock)
            {
                foreach (var lane in _typeLanes.Values) lanes.Add(lane);
                lanes.AddRange(_maskLanes);
            }
            return lanes.ToArray();
        }
    }

    private async Task ObserveFoldFailureAsync()
    {
        foreach (var t in SnapshotFolds())
            if (t.IsFaulted || t.IsCanceled) await t;
    }

    /// <summary>
    /// Awaits consensus work already dispatched per working set, then rethrows any
    /// lane failure. Starts no fold of its own.
    /// </summary>
    public async Task DrainFoldsAsync()
    {
        // Re-snapshot until quiet: awaiting a lane can let a queued delta dispatch
        // further segments onto lanes that were not in the first snapshot.
        while (true)
        {
            var pending = SnapshotFolds().Where(t => !t.IsCompleted).ToArray();
            if (pending.Length == 0)
            {
                foreach (var t in SnapshotFolds())
                    if (t.IsFaulted || t.IsCanceled) await t;
                return;
            }
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    public async Task<(int Entities, int Physicalities, int Attestations)> FinalizeSourceAsync(
        Hash128 sourceId, CancellationToken ct = default)
    {
        await DrainFoldsAsync();
        return await _inner.FinalizeSourceAsync(sourceId, ct);
    }

    public async Task BeginBulkRunAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _consensusBackendTicks, 0);
        Interlocked.Exchange(ref _highwayMaskBackendTicks, 0);
        Interlocked.Exchange(ref _consensusUpsertCalls, 0);
        Interlocked.Exchange(ref _highwayMaskCalls, 0);
        Interlocked.Exchange(ref _highwayMaskPairs, 0);
        LastFoldDrainWallClock = TimeSpan.Zero;
        LastWriterMaintenanceWallClock = TimeSpan.Zero;
        LastFoldSpanWallClock = TimeSpan.Zero;
        Interlocked.Exchange(ref _foldSpanStarted, 0);
        _bulkRun = true;
        FoldSizing.Log();
        await _inner.BeginBulkRunAsync(ct).ConfigureAwait(false);
    }

    public async Task AbortBulkRunAsync(CancellationToken ct = default)
    {
        try { await DrainFoldsAsync().ConfigureAwait(false); }
        finally
        {
            _bulkRun = false;
            await _inner.AbortBulkRunAsync(ct).ConfigureAwait(false);
        }
    }

    public Task CompleteBulkRunAsync(CancellationToken ct = default)
        => CompleteBulkRunAsync(null, ct);

    public async Task CompleteBulkRunAsync(
        Action<BulkRunCompletionPhase>? onPhase,
        CancellationToken ct = default)
    {
        // Folds drain before the inner writer releases its run-scoped state.
        Exception? foldFailure = null;
        var phaseSw = System.Diagnostics.Stopwatch.StartNew();
        onPhase?.Invoke(BulkRunCompletionPhase.ConsensusDrain);
        try
        {
            await DrainFoldsAsync();
        }
        catch (Exception ex)
        {
            foldFailure = ex;
        }
        LastFoldDrainWallClock = phaseSw.Elapsed;
        LastFoldSpanWallClock = _foldSpanStarted == 0
            ? TimeSpan.Zero
            : System.Diagnostics.Stopwatch.GetElapsedTime(_foldSpanStarted);
        bool wasBulk = _bulkRun;
        _bulkRun = false;
        Exception? completionFailure = null;
        phaseSw.Restart();
        onPhase?.Invoke(BulkRunCompletionPhase.WriterMaintenance);
        try
        {
            await _inner.CompleteBulkRunAsync(ct);
        }
        catch (Exception ex)
        {
            completionFailure = ex;
        }
        LastWriterMaintenanceWallClock = phaseSw.Elapsed;

        // No mask pass here: every fold deposited its pairs' bits inline, so once
        // the drain above completes every pair this run touched is set.
        // highway_mask_dirty / highway_mask_drain() and highway_mask_rebuild are
        // repair verbs (evict must clear bits; renumbering rewrites them) and are
        // not on the ingest path.
        _ = wasBulk;

        if (foldFailure is not null && completionFailure is not null)
            throw new AggregateException(foldFailure, completionFailure);
        if (completionFailure is not null)
            ExceptionDispatchInfo.Capture(completionFailure).Throw();
        if (foldFailure is not null)
            ExceptionDispatchInfo.Capture(foldFailure).Throw();
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        var waitSw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _inflightApplies) > 0)
        {
            await Task.Delay(25);
            if (waitSw.Elapsed >= TimeSpan.FromSeconds(30))
            {
                _log.LogWarning(
                    "dispose: still waiting on {N} in-flight apply call(s)",
                    Volatile.Read(ref _inflightApplies));
                waitSw.Restart();
            }
        }
        try
        {
            await DrainFoldsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "dispose: active consensus fold failed");
        }
        _foldDepth.Dispose();
        _foldConnections.Dispose();
    }
}
