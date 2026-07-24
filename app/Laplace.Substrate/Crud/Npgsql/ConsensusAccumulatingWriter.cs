using System.Collections.Immutable;
using System.Runtime.InteropServices;
using global::Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Inline consensus fold: every apply batch writes its evidence AND folds its
/// consensus delta in the same flow — consensus is the fourth table of the
/// batched apply, not a deferred phase. Per batch: client-dedup the cell deltas
/// (already merged in RAM), forward evidence to the inner writer, then dispatch
/// the delta onto the per-type fold lanes — each running consensus_upsert
/// (server-side native Glicko fold inside each row's lock window, ordered by
/// partition keys) — plus the mask lane running highway_mask_deposit (bits OR'd
/// in from the pairs this batch touched).
/// Ingest completion IS fold completion — no accumulator epochs, no staging
/// tables, no walk journal, no terminal fold, no advisory-lock wall.
/// The Glicko rating period is the batch (ratified 2026-07-15).
/// </summary>
public sealed class ConsensusAccumulatingWriter : ISubstrateWriter, IAsyncDisposable
{
    public const string PeriodBoundaryUnitPrefix = IngestBatchPipeline.PeriodBoundaryUnitPrefix;

    private readonly ISubstrateWriter _inner;
    private readonly NpgsqlDataSource _ds;
    private readonly bool _persistEvidence;
    private readonly ILogger _log;

    // PER-TYPE FOLD LANES (2026-07-21), replacing a process-wide
    // SemaphoreSlim(1,1) that let no two deltas overlap for any reason. Consensus
    // is LIST-partitioned by type_id, so two types never share a row: lanes keyed
    // by type can run concurrently without contending, and because a cell has
    // exactly one type it stays on exactly one FIFO lane — which is what keeps the
    // non-commutative Glicko fold deterministic. See DispatchDeltaAsync.
    private readonly object _laneLock = new();
    private readonly Dictionary<Hash128, Task> _typeLanes = new();

    // Masks ride their own lane: an entity accretes bits from many types, so mask
    // writes are the one thing that CAN collide across type lanes. OR-accumulate
    // is commutative, so serializing them costs no ordering guarantee.
    private Task _maskLane = Task.CompletedTask;

    // Fold pipeline (bulk runs only): the fold of batch N runs in the background
    // so the apply lane starts probing/COPYing batch N+1 immediately — the fold
    // leaves the critical path (it was the serial tail of every batch: 188s on an
    // 11.9M-cell document delta). Ordering is now owned by the per-type lanes
    // above, not by one global chain, so deltas whose types are disjoint overlap;
    // this semaphore is purely RAM backpressure on how many deltas may be alive.
    // Drained at FinalizeSource/CompleteBulkRun/Dispose so ingest completion is
    // still fold completion. A fold failure poisons its lane and surfaces at the
    // next apply call or at the drain — never silently. OUTSIDE a bulk run the
    // fold is awaited inline: online lanes (feedback → immediate fold → next
    // walk) require read-your-writes consensus.
    private const int FoldPipelineDepth = 2;
    private readonly SemaphoreSlim _foldDepth = new(FoldPipelineDepth, FoldPipelineDepth);
    private readonly object _foldChainLock = new();
    private volatile bool _bulkRun;

    private long _observations;
    private long _cellsFolded;
    private int _inflightApplies;
    private volatile bool _disposing;

    private const int UpsertChunkCells = 65_536;

    // Fold fan-out width: per-type segments are row-disjoint under the
    // LIST(type_id) partitioning, so they ride parallel connections exactly
    // like the 12-way COPY apply above them. One connection per segment.
    private static readonly int FoldConnections = Math.Clamp(Environment.ProcessorCount, 1, 12);

    // GLOBAL connection budget for the fold, shared by every type lane and the
    // mask lane (2026-07-21). FoldConnections is a per-Parallel.ForEachAsync
    // width; once per-type lanes could run concurrently, that width stopped
    // bounding anything — 4 type lanes + a mask lane across 2 in-flight deltas
    // is up to 120 simultaneous connections against a 12-core server, on top of
    // the apply path's own id-range COPY connections. The single gate before the
    // lanes existed (SemaphoreSlim(1,1) over the whole delta) had been holding
    // that number down as a side effect. This makes the bound explicit and
    // independent of how many lanes happen to be live.
    private readonly SemaphoreSlim _foldConnections = new(FoldConnections, FoldConnections);

    // Run-scoped mask-pair dedup: masks only ACCRETE, so a pair deposited once
    // this run never needs resending — without this, every flush re-verifies
    // every earlier flush's pairs server-side (~6 leaf probes per pair) for
    // zero writes. Touched only from the mask lane (a single FIFO chain), so it
    // needs no lock. Cleared at the cap as a memory valve: clearing costs
    // re-verification, never correctness.
    private readonly HashSet<(Hash128 Ent, Hash128 Typ)> _depositedMaskPairs = new();
    private const int DepositedMaskPairsCap = 8_388_608;

    // There is NO deferred mask phase (2026-07-21). Masks deposit inline in every
    // lane, bulk included — see UpsertDeltaAsync. Both former deferral schemes are
    // gone: the client-side touched-entity HashSet (capped, and any ingest past the
    // cap discarded it and fell back to a full-substrate highway_mask_rebuild) and
    // the server-side highway_mask_dirty queue drained at CompleteBulkRunAsync
    // (exact and uncapped, but still an O(touched entities x consensus probes)
    // recompute parked at the end of the run).

    public ConsensusAccumulatingWriter(
        ISubstrateWriter inner, NpgsqlDataSource dataSource,
        bool? persistEvidence = null,
        ILogger<ConsensusAccumulatingWriter>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _persistEvidence = persistEvidence ?? true;
        _log = logger ?? (ILogger)NullLogger<ConsensusAccumulatingWriter>.Instance;
        if (!_persistEvidence)
            _log.LogInformation(
                "consensus-only deposit: folding relations inline; laplace.attestations writes skipped");
    }

    public bool PersistEvidence => _persistEvidence;

    public long ObservationsAccumulated => Interlocked.Read(ref _observations);

    /// <summary>Total consensus cells folded (inserted or updated) this run.</summary>
    public long CellsFolded => Interlocked.Read(ref _cellsFolded);

    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyManyAsync(new[] { change }, ct);

    public async Task<ApplyResult> ApplyManyAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        => await ApplyCoreAsync(changes, workingSet: false, append: false, default, ct);

    public Task<ApplyResult> ApplyWorkingSetAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyWorkingSetAsync(new[] { change }, ct);

    public async Task<ApplyResult> ApplyWorkingSetAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        => await ApplyCoreAsync(changes, workingSet: true, append: false, default, ct);

    public async Task<ApplyResult> AppendAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128 sourceId, CancellationToken ct = default)
        => await ApplyCoreAsync(changes, workingSet: false, append: true, sourceId, ct);

    /// <summary>
    /// STRUCT, not a class (2026-07-21). One 32-byte heap allocation per merged
    /// cell meant millions of Gen0 objects per working set, purely to hold four
    /// longs the dictionary could store inline. Mutation happens through
    /// <see cref="CollectionsMarshal.GetValueRefOrAddDefault"/>, which hands back a
    /// ref INTO the dictionary's own storage — so the merge stays in-place and
    /// allocation-free, with one hash lookup per attestation instead of two.
    /// </summary>
    private struct Delta
    {
        public long PhiFp1e9;
        public long Games;
        public long SumScoreFp1e9;
        public long MaxTsUnixUs;
    }

    private async Task<ApplyResult> ApplyCoreAsync(
        IReadOnlyList<SubstrateChange> changes, bool workingSet, bool append,
        Hash128 sourceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));
        Interlocked.Increment(ref _inflightApplies);
        try
        {
            if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));

            var delta = BuildDelta(changes);

            // A fold that already failed in the background poisons the run
            // NOW, before any more evidence lands.
            await ObserveFoldFailureAsync();

            // Evidence lands FIRST; the fold runs only after it succeeds, so a
            // retried batch folds exactly once (a throw below leaves consensus
            // untouched for this batch).
            var forwarded = ForwardChanges(changes);
            var result = append
                ? await _inner.AppendAsync(forwarded, sourceId, ct)
                : workingSet
                    ? await _inner.ApplyWorkingSetAsync(forwarded, ct)
                    : await _inner.ApplyManyAsync(forwarded, ct);

            // INVARIANT: one fold per claimed flush-journal token. A journal
            // hit means a prior apply of this exact working set committed —
            // and that apply's own flow folded this same delta right after its
            // evidence landed. The fold is additive, not idempotent, so
            // folding a replay would double-count the batch's testimony in
            // consensus; a journal hit must no-op evidence AND fold. The
            // guard sits OUTSIDE the bulk/inline split: an enqueued fold is
            // still a fold, so a replay must not reach the queue either.
            if (delta is { Count: > 0 } && !result.JournalReplayHit)
            {
                if (_bulkRun) await EnqueueFoldAsync(delta, ct);
                else await UpsertDeltaAsync(delta, ct);
            }

            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _inflightApplies);
        }
    }

    /// <summary>
    /// Cells below which the parallel merge's shard + combine overhead outweighs
    /// the scan it saves. Above it the merge shards across P-cores.
    /// </summary>
    private const int ParallelDeltaMinAttestations = 65_536;

    private Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta>? BuildDelta(
        IReadOnlyList<SubstrateChange> changes)
    {
        // Flatten to the attestation arrays that actually carry testimony. The
        // merge below is over a contiguous index space across those arrays, so
        // sharding never has to care about change boundaries (one 512 MiB working
        // set is often ONE change — splitting per change would leave every core
        // but one idle).
        List<ImmutableArray<AttestationRow>>? blocks = null;
        long total = 0;
        foreach (var c in changes)
        {
            if (!c.TestimonyWalks.IsDefaultOrEmpty)
                throw new InvalidOperationException(
                    "testimony walks are no longer journaled — the consensus fold is inline; "
                    + "emit aggregated attestations (observation_count/sum_score) instead");
            if (c.Metadata.SourceContentUnitName.StartsWith("layer-complete/", StringComparison.Ordinal)
                || c.Metadata.SourceContentUnitName.StartsWith(PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
                continue;
            if (c.Attestations.IsEmpty) continue;
            (blocks ??= new()).Add(c.Attestations);
            total += c.Attestations.Length;
        }
        if (blocks is null || total == 0) return null;

        // MERGE IS ORDER-INDEPENDENT, SO IT PARALLELIZES EXACTLY (2026-07-21).
        // Every combine op is integer: SafeAddGames / SafeAddScores over
        // fixed-point 1e9 longs, and max on the timestamp. Integer add and max
        // are associative and commutative, so any shard split and any combine
        // order yields the BIT-IDENTICAL delta a serial walk yields — this is a
        // pure speedup, not an approximation, and it keeps the fold
        // deterministic. (Float sums would NOT have this property; the
        // fixed-point representation is what makes it sound.)
        //
        // The per-row Interlocked.Add on _observations is gone: it was a locked
        // bus operation per attestation on what was a single-threaded loop.
        // Shards count locally and publish once.
        int workers = total >= ParallelDeltaMinAttestations
            ? Math.Clamp(CpuTopology.PerformanceCoreCount, 1, 16)
            : 1;

        if (workers == 1)
        {
            var single = NewDeltaMap((int)Math.Min(total, int.MaxValue));
            long obs = MergeRange(blocks, 0, total, single);
            Interlocked.Add(ref _observations, obs);
            return single.Count == 0 ? null : single;
        }

        var shards = new Dictionary<(Hash128, Hash128, Hash128?), Delta>[workers];
        var shardObs = new long[workers];
        long per = (total + workers - 1) / workers;
        Parallel.For(0, workers, w =>
        {
            long start = per * w;
            long end = Math.Min(total, start + per);
            var map = NewDeltaMap((int)Math.Max(0, Math.Min(end - start, int.MaxValue)));
            shardObs[w] = end > start ? MergeRange(blocks, start, end, map) : 0;
            shards[w] = map;
        });

        long observed = 0;
        for (int w = 0; w < workers; w++) observed += shardObs[w];
        Interlocked.Add(ref _observations, observed);

        // Combine into the largest shard so the fold-in walks the fewest cells.
        int into = 0;
        for (int w = 1; w < workers; w++)
            if (shards[w].Count > shards[into].Count) into = w;
        var delta = shards[into];
        for (int w = 0; w < workers; w++)
        {
            if (w == into) continue;
            foreach (var (key, src) in shards[w])
            {
                ref var d = ref CollectionsMarshal.GetValueRefOrAddDefault(delta, key, out bool existed);
                if (!existed) d = src;
                else FoldInto(ref d, src.PhiFp1e9, src.Games, src.SumScoreFp1e9, src.MaxTsUnixUs);
            }
        }
        return delta.Count == 0 ? null : delta;
    }

    private static Dictionary<(Hash128, Hash128, Hash128?), Delta> NewDeltaMap(int hint) =>
        new(Math.Clamp(hint, 16, 1 << 20));

    /// <summary>
    /// Ops-marker relation types that never fold into consensus: per-file completion
    /// markers and file-metadata edges ride inside ordinary working-set changes (unlike
    /// the source-level marker, whose whole change is skipped by unit-name prefix in
    /// BuildDelta), so they must be excluded row-by-row. They are recording metadata,
    /// not testimony — folding them would also mix marker φ with content φ in one batch.
    /// </summary>
    private static readonly HashSet<Hash128> OpsMarkerTypeIds = BuildOpsMarkerTypeIds();

    private static HashSet<Hash128> BuildOpsMarkerTypeIds()
    {
        var set = new HashSet<Hash128> { Laplace.Decomposers.Abstractions.FileEntity.MetadataRelationTypeId };
        for (int layer = 0; layer <= Laplace.Ingestion.LayerCompletion.MaxMarkedLayer; layer++)
            set.Add(Laplace.Ingestion.LayerCompletion.RelationTypeId(layer));
        return set;
    }

    /// <summary>Merges attestations [start, end) of the flattened block space into
    /// <paramref name="map"/>; returns the observation count it consumed.</summary>
    private static long MergeRange(
        List<ImmutableArray<AttestationRow>> blocks, long start, long end,
        Dictionary<(Hash128, Hash128, Hash128?), Delta> map)
    {
        long obs = 0;
        long pos = 0;
        foreach (var atts in blocks)
        {
            long blockEnd = pos + atts.Length;
            if (blockEnd <= start) { pos = blockEnd; continue; }
            if (pos >= end) break;

            int from = (int)Math.Max(0, start - pos);
            int to = (int)Math.Min(atts.Length, end - pos);
            for (int i = from; i < to; i++)
            {
                var a = atts[i];
                if (OpsMarkerTypeIds.Contains(a.TypeId)) continue;
                var key = (a.SubjectId, a.TypeId, a.ObjectId);
                ref var d = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out bool existed);
                if (!existed)
                {
                    d.PhiFp1e9 = a.OpponentRdFp1e9;
                    d.Games = a.ObservationCount;
                    d.SumScoreFp1e9 = AttestationMergeMath.RowScoreTotal(a);
                    d.MaxTsUnixUs = a.LastObservedAtUnixUs;
                }
                else
                {
                    FoldInto(ref d, a.OpponentRdFp1e9, a.ObservationCount,
                             AttestationMergeMath.RowScoreTotal(a), a.LastObservedAtUnixUs);
                }
                obs += a.ObservationCount;
            }
            pos = blockEnd;
        }
        return obs;
    }

    private static void FoldInto(ref Delta d, long phi, long games, long score, long tsUnixUs)
    {
        if (d.PhiFp1e9 != phi)
            throw new InvalidOperationException(
                $"fold invariant violated: cell observed with φ={phi} "
                + $"after φ={d.PhiFp1e9} in the same batch");
        d.Games = AttestationMergeMath.SafeAddGames(d.Games, games);
        d.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(d.SumScoreFp1e9, score);
        if (tsUnixUs > d.MaxTsUnixUs) d.MaxTsUnixUs = tsUnixUs;
    }

    private IReadOnlyList<SubstrateChange> ForwardChanges(IReadOnlyList<SubstrateChange> changes)
    {
        if (_persistEvidence) return changes;

        bool anyToStrip = false;
        foreach (var c in changes)
            if (!c.Attestations.IsEmpty) { anyToStrip = true; break; }
        if (!anyToStrip) return changes;

        var stripped = new SubstrateChange[changes.Count];
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (!c.Attestations.IsEmpty)
                c = c with { Attestations = ImmutableArray<AttestationRow>.Empty };
            stripped[i] = c;
        }
        return stripped;
    }

    /// <summary>
    /// Dispatches one delta onto the per-type fold lanes and the mask lane, and
    /// returns the task that completes when all of this delta's segments have
    /// committed.
    ///
    /// PER-TYPE LANES, NOT ONE GLOBAL GATE (2026-07-21). The fold used to hold a
    /// process-wide SemaphoreSlim(1,1) for the whole delta, so no two deltas
    /// could ever overlap. That was tolerable only while deltas were enormous
    /// (one delta held enough cells to saturate all FoldConnections by itself).
    /// Now that the apply commits at file/envelope grain, a delta can be smaller
    /// than one 65,536-cell chunk — under the old gate that meant ONE connection
    /// working while eleven idled, with every other delta blocked behind it.
    ///
    /// Lanes are keyed by relation type, which is exactly the safety boundary:
    /// consensus is LIST-partitioned by type_id, so two different types can never
    /// touch the same consensus row, and their transactions can neither contend
    /// nor deadlock.
    ///
    /// DETERMINISM IS PRESERVED, and this is the reason the split is by TYPE and
    /// not by count. Glicko-2 accumulation is NOT commutative: folding delta A
    /// then B into the same cell gives a different rating than B then A. A cell
    /// has exactly ONE type, so it lives in exactly one lane, and each lane is a
    /// strict FIFO chain — every cell therefore still sees its deltas in arrival
    /// order, and consensus does not depend on scheduling. Splitting by count
    /// would have broken that; splitting by type does not.
    /// </summary>
    private Task DispatchDeltaAsync(
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> delta, CancellationToken ct)
    {
        // Sort by the partition keys (type, subject) then edge id so every
        // writer locks rows in one global order.
        var cells = new ((Hash128 S, Hash128 T, Hash128? O) Key, Hash128 Cid, Delta D)[delta.Count];
        int n = 0;
        foreach (var (key, d) in delta)
            cells[n++] = (key, ConsensusKeys.EdgeId(key.S, key.T, key.O ?? default), d);
        Array.Sort(cells, static (x, y) =>
        {
            int c = x.Key.T.CompareToBytewise(y.Key.T);
            if (c != 0) return c;
            c = x.Key.S.CompareToBytewise(y.Key.S);
            return c != 0 ? c : x.Cid.CompareToBytewise(y.Cid);
        });

        // Mask pairs from the same delta: (subject, type) + (object, type).
        //
        // DEPOSIT IS THE POPULATION, IN EVERY LANE (2026-07-21). This used to be
        // built for online lanes only; bulk runs skipped the deposit entirely and
        // paid a terminal highway_mask_drain() instead. That trade was backwards.
        // highway_mask_deposit is O(pairs the fold already holds in RAM) — an
        // OR-accumulate with ZERO consensus re-reads. The deferred path replaced
        // it with highway_mask_refresh over every touched entity, which recomputes
        // each mask from that entity's full consensus edge set: the object-side
        // join whose leaf probes were MEASURED at 75s of a 118s fold. Deferring
        // therefore swapped an O(touched pairs) write for an
        // O(touched entities x consensus probes) recompute AND parked it at the
        // end of the run as one serial pass.
        //
        // The stated reason for deferring — "~2M entity UPDATEs contending with
        // the concurrent COPY" — is an argument against per-batch UPDATE CHURN,
        // not against deposit: the run-scoped pair dedup below means each pair is
        // written at most once per run, so the total UPDATE volume is strictly
        // LOWER than the drain's, and it is spread across the run instead of
        // landing in one lump at the end.
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long folded = 0, masks = 0;
        var completions = new List<Task>(runs.Count + 1);

        // The lane bodies are STARTED OUTSIDE THE LOCK (Task.Run), and only the
        // chain pointers are swapped under it (2026-07-21). Invoking an async
        // method inside the lock runs its synchronous prefix on the calling
        // thread while the lock is held — for these bodies that prefix reaches
        // Parallel.ForEachAsync and can open connections and dispatch the first
        // consensus_upsert before it ever suspends, so every other delta's
        // dispatch blocked behind one delta's first DB round trip. Starting the
        // body on the pool keeps the critical section to dictionary writes.
        lock (_laneLock)
        {
            foreach (var run in runs)
            {
                var prior = _typeLanes.TryGetValue(run.Type, out var p) ? p : Task.CompletedTask;
                var r = run;
                var next = Task.Run(() => FoldRunAfterAsync(prior, r), ct);
                _typeLanes[run.Type] = next;
                completions.Add(next);
            }

            // Masks ride ONE dedicated lane rather than the type lanes. An entity
            // accretes bits from many types, so a mask write for type A and one
            // for type B can hit the SAME entities row — across concurrent type
            // lanes that is exactly the cross-transaction row contention (and
            // deadlock risk) the type split otherwise eliminates. Serializing
            // deposits costs nothing in ordering terms: OR-accumulate is
            // commutative and idempotent, so the mask lane is order-free even
            // though the fold lanes are not.
            var priorMask = _maskLane;
            _maskLane = Task.Run(() => DepositAfterAsync(priorMask, maskPairs), ct);
            completions.Add(_maskLane);
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

        async Task FoldRunAfterAsync(Task prior, (Hash128 Type, int Off, int Len) run)
        {
            // A faulted predecessor rethrows here: the lane stays poisoned and
            // every later segment on it (and the drain) sees the failure.
            await prior.ConfigureAwait(false);

            // Fixed-size chunks WITHIN the type run, folded on PARALLEL
            // connections — the same width the COPY apply uses. Safety inside a
            // run does not come from partition boundaries: cells are
            // CLIENT-DEDUPED, so no two chunks can touch the same consensus row
            // — row locks are disjoint by construction, inserts are unique by
            // construction, and consensus_upsert's per-type loop still gives
            // every call runtime-pruned, type-major-ordered writes. Each chunk
            // commits its own transaction.
            // Segment size FANS OUT to the connection budget instead of a fixed
            // 65,536 (2026-07-23). The fixed chunk was sized for huge deltas
            // (documents, models) and silently degraded single-type-dominated,
            // file-grain workloads to ONE connection: a chess-eval census whose
            // deltas were ~90% HAS_EVAL folded at ~1.5k cells/s while eleven
            // connections idled — measured 14-20x under the 21-36k cells/s this
            // same fold recorded on OMW's multi-type deltas. Safety is unchanged
            // and was never the chunk boundary's job: cells are client-deduped,
            // so ANY chunking is row-disjoint. The 2,048 floor keeps a tiny run
            // from paying twelve transactions for a few hundred cells.
            int segLen = Math.Clamp(
                (run.Len + FoldConnections - 1) / FoldConnections, 2_048, UpsertChunkCells);
            var segments = new List<(int Off, int Len)>();
            for (int s = run.Off; s < run.Off + run.Len; s += segLen)
                segments.Add((s, Math.Min(segLen, run.Off + run.Len - s)));

            await Parallel.ForEachAsync(segments,
                new ParallelOptions { MaxDegreeOfParallelism = FoldConnections, CancellationToken = ct },
                async (seg, token) =>
            {
                // Global budget, not the per-loop width: see _foldConnections.
                await _foldConnections.WaitAsync(token).ConfigureAwait(false);
                try
                {
                await using var conn = await _ds.OpenConnectionAsync(token);
                await using var tx = await conn.BeginTransactionAsync(token);
                long segFolded = 0;
                for (int off = seg.Off; off < seg.Off + seg.Len; off += UpsertChunkCells)
                {
                    int m = Math.Min(UpsertChunkCells, seg.Off + seg.Len - off);
                    var subjects = new byte[m][];
                    var types = new byte[m][];
                    var objects = new byte[m][];
                    var phis = new long[m];
                    var games = new long[m];
                    var sums = new long[m];
                    var ts = new DateTime[m];
                    for (int i = 0; i < m; i++)
                    {
                        var cell = cells[off + i];
                        subjects[i] = cell.Key.S.ToBytes();
                        types[i] = cell.Key.T.ToBytes();
                        objects[i] = cell.Key.O?.ToBytes()!;
                        phis[i] = cell.D.PhiFp1e9;
                        games[i] = cell.D.Games;
                        sums[i] = cell.D.SumScoreFp1e9;
                        ts[i] = TsFromUnixUs(cell.D.MaxTsUnixUs);
                    }
                    await using var up = conn.CreateCommand();
                    up.Transaction = tx;
                    up.CommandTimeout = 0;
                    up.CommandText = "SELECT laplace.consensus_upsert($1, $2, $3, $4, $5, $6, $7)";
                    up.Parameters.Add(new NpgsqlParameter { Value = subjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    up.Parameters.Add(new NpgsqlParameter { Value = types, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    up.Parameters.Add(new NpgsqlParameter { Value = objects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    up.Parameters.Add(new NpgsqlParameter { Value = phis, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                    up.Parameters.Add(new NpgsqlParameter { Value = games, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                    up.Parameters.Add(new NpgsqlParameter { Value = sums, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                    up.Parameters.Add(new NpgsqlParameter { Value = ts, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                    segFolded += (long)(await up.ExecuteScalarAsync(token) ?? 0L);
                }
                await tx.CommitAsync(token);
                Interlocked.Add(ref folded, segFolded);
                }
                finally { _foldConnections.Release(); }
            });
        }

        async Task DepositAfterAsync(Task prior, HashSet<(Hash128 Ent, Hash128 Typ)> pairs)
        {
            await prior.ConfigureAwait(false);
            if (pairs.Count == 0) return;

            // Never resend a pair this run already deposited — masks only
            // ACCRETE, so a pair deposited once is permanently satisfied, and the
            // server-side no-op still costs ~6 tier-leaf probes per pair. Safe to
            // read/mutate unlocked: the mask lane is a single FIFO chain, so only
            // one deposit body touches this set at a time.
            pairs.ExceptWith(_depositedMaskPairs);
            if (pairs.Count == 0) return;

            // Mask deposits fan out BUCKETED BY ENTITY: one entity accretes
            // bits from many types, so the split axis must keep all of an
            // entity's pairs in ONE bucket — buckets then touch disjoint
            // entities rows and parallel deposit transactions cannot contend
            // or deadlock on a shared row. (A count-based split would put the
            // same entity row under two transactions.)
            int buckets = Math.Min(FoldConnections, 1 + pairs.Count / UpsertChunkCells);
            var bucketed = new List<(Hash128 Ent, Hash128 Typ)>[buckets];
            for (int b = 0; b < buckets; b++)
                bucketed[b] = new List<(Hash128 Ent, Hash128 Typ)>(pairs.Count / buckets + 16);
            foreach (var p in pairs)
                bucketed[(int)((uint)p.Ent.GetHashCode() % (uint)buckets)].Add(p);

            long maskTotal = 0;
            await Parallel.ForEachAsync(bucketed,
                new ParallelOptions { MaxDegreeOfParallelism = FoldConnections, CancellationToken = ct },
                async (bucket, token) =>
            {
                if (bucket.Count == 0) return;
                await _foldConnections.WaitAsync(token).ConfigureAwait(false);
                try
                {
                await using var conn = await _ds.OpenConnectionAsync(token);
                await using var tx = await conn.BeginTransactionAsync(token);
                long dep = 0;
                for (int off = 0; off < bucket.Count; off += UpsertChunkCells)
                {
                    int m = Math.Min(UpsertChunkCells, bucket.Count - off);
                    var pairEnts = new byte[m][];
                    var pairTypes = new byte[m][];
                    for (int i = 0; i < m; i++)
                    {
                        pairEnts[i] = bucket[off + i].Ent.ToBytes();
                        pairTypes[i] = bucket[off + i].Typ.ToBytes();
                    }
                    await using var mask = conn.CreateCommand();
                    mask.Transaction = tx;
                    mask.CommandTimeout = 0;
                    mask.CommandText = "SELECT laplace.highway_mask_deposit($1, $2)";
                    mask.Parameters.Add(new NpgsqlParameter { Value = pairEnts, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    mask.Parameters.Add(new NpgsqlParameter { Value = pairTypes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    dep += (long)(await mask.ExecuteScalarAsync(token) ?? 0L);
                }
                await tx.CommitAsync(token);
                Interlocked.Add(ref maskTotal, dep);
                }
                finally { _foldConnections.Release(); }
            });
            Interlocked.Add(ref masks, maskTotal);

            // Mark AFTER all buckets commit — pairs from any failed run
            // stay resendable; resends are no-ops server-side.
            if (_depositedMaskPairs.Count + pairs.Count > DepositedMaskPairsCap)
                _depositedMaskPairs.Clear();
            _depositedMaskPairs.UnionWith(pairs);
        }
    }

    /// <summary>
    /// Drops lanes whose chain has completed. Without this the map retains one
    /// entry per relation type ever folded — bounded (the governed type count) but
    /// pointlessly resident, and every drain would await hundreds of finished
    /// tasks. Faulted lanes are KEPT: the drain has to observe their exception.
    /// </summary>
    private void PruneCompletedLanes()
    {
        lock (_laneLock)
        {
            if (_typeLanes.Count < 64) return;
            foreach (var key in _typeLanes.Where(kv => kv.Value.IsCompletedSuccessfully)
                                          .Select(kv => kv.Key).ToList())
                _typeLanes.Remove(key);
        }
    }

    private static DateTime TsFromUnixUs(long unixUs)
        => DateTime.UnixEpoch.AddTicks(unixUs * 10);

    /// <summary>
    /// Inline fold: dispatch and AWAIT. Online lanes (feedback → immediate fold →
    /// next walk) require read-your-writes consensus.
    /// </summary>
    private Task UpsertDeltaAsync(
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> delta, CancellationToken ct)
        => DispatchDeltaAsync(delta, ct);

    /// <summary>
    /// Bulk fold: dispatch onto the per-type lanes and return as soon as the
    /// delta is QUEUED, so the apply lane starts probing/COPYing the next working
    /// set immediately — the fold leaves the critical path. Bounded to
    /// FoldPipelineDepth outstanding deltas as backpressure on RAM.
    /// </summary>
    private async Task EnqueueFoldAsync(
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> delta, CancellationToken ct)
    {
        await _foldDepth.WaitAsync(ct);
        Task dispatched;
        try
        {
            dispatched = DispatchDeltaAsync(delta, ct);
        }
        catch
        {
            _foldDepth.Release();
            throw;
        }

        var tracked = Release(dispatched);
        lock (_foldChainLock) _outstanding.Add(tracked);

        async Task Release(Task fold)
        {
            try { await fold.ConfigureAwait(false); }
            finally { _foldDepth.Release(); }
        }
    }

    /// <summary>
    /// Every fold dispatched and not yet observed — the type lanes plus the mask
    /// lane, per delta. Completed entries are swept on each snapshot; faulted ones
    /// are retained until a drain or the next apply observes them, so a background
    /// fold failure can never vanish silently.
    /// </summary>
    private readonly List<Task> _outstanding = new();

    private Task[] SnapshotFolds()
    {
        lock (_foldChainLock)
        {
            _outstanding.RemoveAll(t => t.IsCompletedSuccessfully);
            var lanes = new List<Task>(_outstanding);
            lock (_laneLock)
            {
                foreach (var lane in _typeLanes.Values) lanes.Add(lane);
                lanes.Add(_maskLane);
            }
            return lanes.ToArray();
        }
    }

    private async Task ObserveFoldFailureAsync()
    {
        foreach (var t in SnapshotFolds())
            if (t.IsFaulted || t.IsCanceled) await t;
    }

    /// <summary>Awaits every queued fold. Ingest completion IS fold
    /// completion: finalize/complete/dispose all pass through here.</summary>
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

    public Task BeginBulkRunAsync(CancellationToken ct = default)
    {
        _bulkRun = true;
        return _inner.BeginBulkRunAsync(ct);
    }

    public async Task CompleteBulkRunAsync(CancellationToken ct = default)
    {
        // Folds drain BEFORE the inner writer rebuilds the cycled consensus
        // secondaries — a live fold during the rebuild would pay live index
        // maintenance on every insert.
        await DrainFoldsAsync();
        bool wasBulk = _bulkRun;
        _bulkRun = false;
        await _inner.CompleteBulkRunAsync(ct);

        // NO terminal mask pass (2026-07-21). Masks are deposited inline by every
        // fold, in every lane — see UpsertDeltaAsync. There is nothing left to
        // defer: by the time the last fold drains above, every pair this run
        // touched has already had its bits OR'd in, spread across the run instead
        // of landing as one serial recompute after the loader finishes.
        //
        // highway_mask_dirty / highway_mask_drain() survive as the REPAIR verbs
        // (per-source evict has to CLEAR bits, which an OR-accumulate deposit
        // cannot do), alongside highway_mask_rebuild for highway bit renumbering.
        // Nothing on the ingest hot path populates or drains the queue.
        _ = wasBulk;
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
            _log.LogError(ex, "dispose: queued consensus fold failed");
        }
        _foldDepth.Dispose();
        _foldConnections.Dispose();
    }
}
