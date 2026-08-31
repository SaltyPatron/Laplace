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

/// <summary>
/// Inline consensus fold: every apply batch writes its evidence AND folds its
/// consensus delta in the same flow — consensus is the fourth table of the
/// batched apply, not a deferred phase. Per batch: client-dedup the cell deltas
/// (already merged in RAM), forward evidence to the inner writer, then dispatch
/// the delta onto the per-type fold lanes — each running consensus_upsert
/// (server-side native Glicko fold inside each row's lock window, ordered by
/// partition keys) — plus the mask lane running consensus.highway_mask_deposit(bits OR'd
/// in from the pairs this batch touched).
/// Ingest completion IS fold completion — no accumulator epochs, no staging
/// tables, no walk journal, no terminal fold, no advisory-lock wall.
/// The Glicko rating period is the batch (ratified 2026-07-15).
/// </summary>
public sealed class ConsensusAccumulatingWriter : ISubstrateWriter, IConsensusFoldMetrics, IAsyncDisposable
{
    public const string PeriodBoundaryUnitPrefix = IngestBatchPipeline.PeriodBoundaryUnitPrefix;

    private readonly ISubstrateWriter _inner;
    private readonly NpgsqlDataSource _ds;
    private readonly bool _persistEvidence;
    private readonly ILogger _log;
    private int _directConsensusRoute = -1;

    private async ValueTask<bool> SupportsDirectConsensusRouteAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        int known = Volatile.Read(ref _directConsensusRoute);
        if (known >= 0) return known == 1;

        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT to_regprocedure("
            + "'consensus.upsert_type(bytea,bytea[],bytea[],bigint[],bigint[],bigint[],timestamptz[],bigint[])') "
            + "IS NOT NULL";
        bool supported = (bool)(await probe.ExecuteScalarAsync(ct) ?? false);
        Interlocked.CompareExchange(ref _directConsensusRoute, supported ? 1 : 0, -1);
        return Volatile.Read(ref _directConsensusRoute) == 1;
    }

    // Write-epoch bump gate (PR1 of the trusted-novelty series). Fold segment
    // and highway-mask deposit transactions are write-lane transactions, so
    // each bumps laplace.apply_write_epoch BEFORE its writes — the epoch is
    // what lets a later apply prove "no writer intervened" instead of
    // re-probing. Probed once and cached exactly like the direct-route gate
    // above: an older installed extension has no sequence and every lane must
    // degrade to the pre-epoch behavior, never fail the fold.
    private int _applyWriteEpochRoute = -1;

    private async ValueTask<bool> SupportsApplyWriteEpochAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        int known = Volatile.Read(ref _applyWriteEpochRoute);
        if (known >= 0) return known == 1;

        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT to_regclass('laplace.apply_write_epoch') IS NOT NULL";
        bool supported = (bool)(await probe.ExecuteScalarAsync(ct) ?? false);
        Interlocked.CompareExchange(ref _applyWriteEpochRoute, supported ? 1 : 0, -1);
        return Volatile.Read(ref _applyWriteEpochRoute) == 1;
    }

    // PER-TYPE FOLD LANES (2026-07-21), replacing a process-wide
    // SemaphoreSlim(1,1) that let no two deltas overlap for any reason. Consensus
    // is LIST-partitioned by type_id, so two types never share a row: lanes keyed
    // by type can run concurrently without contending, and because a cell has
    // exactly one type it stays on exactly one FIFO lane — which is what keeps the
    // non-commutative Glicko fold deterministic. See DispatchDeltaAsync.
    private readonly object _laneLock = new();
    private readonly Dictionary<Hash128, Task> _typeLanes = new();

    // Stable entity-sharded mask lanes. An entity always maps to exactly one lane,
    // across every delta in the run: lanes are row-disjoint and run in parallel,
    // while deposits that can touch the same entity are FIFO. The former unchained
    // per-delta tasks let six large deposits update the same hot entity rows at once;
    // Atomic2020 measured fold throughput collapsing from 6,880 to 401-779 cells/s
    // while the producer had already finished. PostgreSQL correctly serialized those
    // row locks, but only after we had manufactured the contention.
    private readonly Task[] _maskLanes =
        Enumerable.Repeat(Task.CompletedTask, MaskShards).ToArray();

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
    // One sizing authority for the whole fold. The retired implementation fixed
    // chunk=65,536, pipeline depth=6, mask cap=8,388,608 and segment floor=2,048
    // independently, so none of them tracked RAM, row width, or connection fanout.
    private static readonly IngestSizing.ConsensusFoldPlan FoldSizing =
        IngestSizing.ResolveConsensusFold(IngestTopology.Current.ApplyPartitions);
    private readonly SemaphoreSlim _foldDepth =
        new(FoldSizing.PipelineDepth, FoldSizing.PipelineDepth);
    private readonly object _foldChainLock = new();
    private volatile bool _bulkRun;

    private long _observations;
    private long _cellsFolded;
    private long _consensusBackendTicks;
    private long _highwayMaskBackendTicks;
    private long _consensusUpsertCalls;
    private long _highwayMaskCalls;
    private long _highwayMaskPairs;
    private long _foldSpanStarted;
    private int _inflightApplies;
    private volatile bool _disposing;

    // Fold fan-out width: inherited from the topology plan, never re-clamped here.
    private static int FoldConnections => FoldSizing.Connections;

    // One stable entity shard per fold connection. Shards are ownership lanes, not
    // reserved connections: each byte-derived chunk leases the shared connection gate
    // independently, so masks and consensus remain work-conserving.
    private static readonly int MaskShards = FoldConnections;

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

    // Run-scoped pair dedup, owned by the same stable shard as the entity. Each set
    // is touched only by its FIFO lane. Clearing is a memory valve: it costs a
    // server-side idempotent recheck, never correctness.
    private readonly HashSet<(Hash128 Ent, Hash128 Typ)>[] _depositedMaskPairs =
        Enumerable.Range(0, MaskShards)
            .Select(_ => new HashSet<(Hash128 Ent, Hash128 Typ)>()).ToArray();
    private static int DepositedMaskPairsCap => FoldSizing.MaskPairCapacity;

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

    public TimeSpan LastFoldDrainWallClock { get; private set; }

    public TimeSpan LastWriterMaintenanceWallClock { get; private set; }

    public TimeSpan LastFoldSpanWallClock { get; private set; }

    public TimeSpan ConsensusUpsertBackendWallClock => StopwatchTime(
        Interlocked.Read(ref _consensusBackendTicks));

    public TimeSpan HighwayMaskBackendWallClock => StopwatchTime(
        Interlocked.Read(ref _highwayMaskBackendTicks));

    public long ConsensusUpsertCalls => Interlocked.Read(ref _consensusUpsertCalls);

    public long HighwayMaskCalls => Interlocked.Read(ref _highwayMaskCalls);

    public long HighwayMaskPairs => Interlocked.Read(ref _highwayMaskPairs);

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
        // The opponent this witness presents (GH #1321). Pinned per cell for the
        // same reason PhiFp1e9 is: attestation identity fixes (subject, type,
        // object, source, context), so every row merging into a cell in one batch
        // came from the same source under the same relation and therefore the same
        // witness_weight — which produces both halves.
        public long OpponentRatingFp1e9;
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
                // Evidence has committed and its replay journal will suppress this delta
                // forever. From here the fold is an owed continuation, not cancellable
                // speculative work; completion/drain owns surfacing any failure.
                if (_bulkRun) await EnqueueFoldAsync(delta, CancellationToken.None);
                else await UpsertDeltaAsync(delta, CancellationToken.None);
            }

            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _inflightApplies);
        }
    }

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
        int workers = _bulkRun
            ? (int)Math.Min(total, Math.Max(1, CpuTopology.PerformanceCoreCount))
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
                else FoldInto(ref d, src.PhiFp1e9, src.OpponentRatingFp1e9,
                              src.Games, src.SumScoreFp1e9, src.MaxTsUnixUs);
            }
        }
        return delta.Count == 0 ? null : delta;
    }

    private static Dictionary<(Hash128, Hash128, Hash128?), Delta> NewDeltaMap(int hint) =>
        new(Math.Clamp(hint, 1, FoldSizing.DeltaCapacityCells));

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
                    d.OpponentRatingFp1e9 = a.OpponentRatingFp1e9;
                    d.Games = a.ObservationCount;
                    d.SumScoreFp1e9 = AttestationMergeMath.RowScoreTotal(a);
                    d.MaxTsUnixUs = a.LastObservedAtUnixUs;
                }
                else
                {
                    FoldInto(ref d, a.OpponentRdFp1e9, a.OpponentRatingFp1e9,
                             a.ObservationCount,
                             AttestationMergeMath.RowScoreTotal(a), a.LastObservedAtUnixUs);
                }
                obs += a.ObservationCount;
            }
            pos = blockEnd;
        }
        return obs;
    }

    private static void FoldInto(ref Delta d, long phi, long oppRating, long games, long score, long tsUnixUs)
    {
        if (d.PhiFp1e9 != phi)
            throw new InvalidOperationException(
                $"fold invariant violated: cell observed with φ={phi} "
                + $"after φ={d.PhiFp1e9} in the same batch");
        if (d.OpponentRatingFp1e9 != oppRating)
            throw new InvalidOperationException(
                $"fold invariant violated: cell observed with opponent={oppRating} "
                + $"after opponent={d.OpponentRatingFp1e9} in the same batch");
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
        // Sort by type, then edge id, then subject, so every writer locks rows in
        // one global order.
        //
        // TYPE LEADS because the per-type lanes below require cells grouped by
        // type; that is unchanged. What changed is the tiebreak ORDER, and it is
        // a physical-locality fix, not a cosmetic one.
        //
        // consensus_pkey is btree (id, type_id, subject_id) -- id LEADS -- while
        // consensus_rdefault is HASH (subject_id) mod 8. Sorting (type, subject,
        // id) put the PK's leading column LAST, so a 65,536-cell chunk descended
        // the PK btree in an order uncorrelated with the btree, touching ~65,536
        // distinct random pages across 88GB of consensus against a 31GB
        // shared_buffers. MEASURED 2026-08-18 on a live UD run: 25.2k random read
        // IOPS and 13.2k full-page images/s, 11.1% of WAL records carrying an 8KB
        // FPI, ~2,300x write amplification against a <4GB corpus.
        //
        // Sorting by id after type puts each hash subpartition's accesses in PK
        // order. Subject stays in the key as the final tiebreak: hash(subject_id)
        // decides the subpartition and is not monotonic in subject, so the eight
        // subpartitions still interleave -- but each is now walked ASCENDING
        // instead of randomly, which is what the buffer cache can actually hold.
        //
        // The deadlock invariant is untouched: any TOTAL order avoids the lock
        // cycle as long as every writer uses the same one, and (type, id, subject)
        // is total exactly as (type, subject, id) was. Lane determinism is also
        // untouched -- Glicko-2's non-commutativity is ordered by the per-type
        // FIFO chain below, never by position within a delta.
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
        int[] runWidths = IngestSizing.AllocateFoldRunWidths(
            runs.Select(static r => r.Len).ToArray(), FoldConnections);

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

            // Partition by a FIXED entity hash. The earlier bucketed implementation
            // changed bucket count with each delta, so the same entity moved between
            // lanes and the buckets were only disjoint within one call. Fixed shards
            // preserve disjointness across the whole run.
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

            // Fixed-size chunks WITHIN the type run, folded on PARALLEL
            // connections — the same width the COPY apply uses. Safety inside a
            // run does not come from partition boundaries: cells are
            // CLIENT-DEDUPED, so no two chunks can touch the same consensus row
            // — row locks are disjoint by construction, inserts are unique by
            // construction, and consensus_upsert's per-type loop still gives
            // every call runtime-pruned, type-major-ordered writes. Each chunk
            // commits its own transaction.
            // Connection width is allocated across ALL type runs in this delta by
            // actual run size. There is no minimum-cell threshold: a single-type
            // delta receives the live topology, while many independent type runs
            // each receive a lane and share the global gate. ChunkCells remains
            // only the byte-derived maximum parameter-array residency per command.
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
                // The write-epoch bump rides the same batch — no extra round trip
                // (this command carries no positional parameters, which Npgsql
                // forbids in multi-statement commands), nextval before the fold's
                // writes as the sequence's law requires.
                if (epochBump)
                {
                    await using var epoch = conn.CreateCommand();
                    epoch.Transaction = tx;
                    epoch.CommandText = "SELECT nextval('laplace.apply_write_epoch')";
                    await epoch.ExecuteNonQueryAsync(token);
                }
                await using var up = conn.CreateCommand();
                up.Transaction = tx;
                up.CommandTimeout = 0;
                up.CommandText = directRoute
                    ? "SELECT consensus.upsert_type($1, $2, $3, $4, $5, $6, $7, $8)"
                    : "SELECT consensus.upsert($1, $2, $3, $4, $5, $6, $7, $8)";
                up.Parameters.Add(new NpgsqlParameter
                {
                    Value = directRoute ? Array.Empty<byte>() : Array.Empty<byte[]>(),
                    NpgsqlDbType = directRoute
                        ? NpgsqlDbType.Bytea
                        : NpgsqlDbType.Array | NpgsqlDbType.Bytea
                });
                up.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<byte[]>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                up.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<byte[]>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                up.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                up.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                up.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                up.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<DateTime>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                up.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
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
                    for (int i = 0; i < m; i++)
                    {
                        var cell = cells[off + i];
                        subjects[i] = cell.Key.S.ToBytes();
                        objects[i] = cell.Key.O?.ToBytes()!;
                        phis[i] = cell.D.PhiFp1e9;
                        opps[i] = cell.D.OpponentRatingFp1e9;
                        games[i] = cell.D.Games;
                        sums[i] = cell.D.SumScoreFp1e9;
                        ts[i] = TsFromUnixUs(cell.D.MaxTsUnixUs);
                    }
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

            // Never resend a pair this run already deposited — masks only ACCRETE,
            // so a pair deposited once is permanently satisfied, and the server-side
            // no-op still costs ~6 tier-leaf probes per pair. This shard's FIFO is
            // the synchronization; no cross-shard pair can share an entity.
            var deposited = _depositedMaskPairs[shard];
            pairs.RemoveAll(deposited.Contains);
            if (pairs.Count == 0) return;
            var todo = pairs;

            // SORT BY ENTITY ID BEFORE CHUNKING — the transaction-scope half of the
            // deadlock fix, and the half #729 missed. The SQL-side ordered locking
            // (highway_mask_deposit's `locked` CTE) makes acquisition ascending
            // WITHIN one statement, but this transaction runs a SEQUENCE of chunk
            // statements while holding every prior chunk's locks — and chunks cut
            // from an unordered set interleave id ranges arbitrarily between
            // concurrent deposits, which is an AB/BA cycle across statements.
            // Measured on the Wiktionary seed 2026-07-29 (post-#729): 40P01 with
            // BOTH parties inside highway_mask_deposit, ten retries lost.
            // Sorted, every deposit transaction acquires ascending across its
            // WHOLE chunk sequence; two ascending acquirers cannot form a cycle.
            // CompareToBytewise is the native memcmp order — identical to the
            // bytea ordering the server-side ORDER BY uses, one comparator, not two.
            todo.Sort(static (a, b) => a.Ent.CompareToBytewise(b.Ent));

            // ONE statement per chunk, on ONE connection, ONE TRANSACTION PER CHUNK.
            //
            // highway_mask_deposit already does the whole job set-based: DISTINCT
            // over the pairs, one probe per DISTINCT type, GROUP BY entity, then a
            // single UPDATE ... FROM. This writer now partitions work by a stable
            // entity shard BEFORE statements are built, so concurrent shard calls are
            // row-disjoint. SQL acquires any externally-overlapping target rows in a
            // deterministic order; it no longer collapses the disjoint ingest calls
            // behind one global advisory lock.
            //
            // WHY PER-CHUNK COMMITS, measured 2026-07-29 on the Wiktionary seed:
            // deposits from different deltas always overlap on hot words, so with
            // ordered acquisition (the deadlock fix) concurrent deposits QUEUE on
            // the first shared row -- and under one all-or-nothing transaction the
            // waiter waits for the holder's ENTIRE remaining chunk sequence. That
            // convoy took the epoch fold from 177s (3,305 masks/s, the crashing
            // run) to 1,238s (333 masks/s): correct, forty minutes of it per hour.
            // Committing per chunk caps every wait at one chunk's work while
            // keeping the acquisition order global (todo is sorted; chunk k+1's
            // ids all sort after chunk k's), so the no-cycle proof is unchanged --
            // stronger, even: one statement per transaction.
            //
            // All-or-nothing was never load-bearing: OR-accumulate is idempotent,
            // a failure mid-sequence leaves earlier chunks committed (bits on,
            // correct) and the WHOLE deposit's pairs unmarked below, so the resend
            // re-runs every chunk as a server-side no-op.
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
                    // Deposit transactions are write-lane transactions too: bump the
                    // epoch before the OR-accumulate. Its own command, unlike the
                    // fold segment's piggyback, because the deposit statement uses
                    // positional parameters and Npgsql forbids those in
                    // multi-statement commands — one cheap extra round trip per
                    // deposit transaction (the support probe itself is cached).
                    if (epochBump)
                    {
                        await using var bump = conn.CreateCommand();
                        bump.Transaction = tx;
                        bump.CommandText = "SELECT nextval('laplace.apply_write_epoch')";
                        await bump.ExecuteNonQueryAsync(ct);
                    }
                    await using var mask = conn.CreateCommand();
                    mask.Transaction = tx;
                    mask.CommandTimeout = 0;
                    mask.CommandText = "SELECT consensus.highway_mask_deposit($1, $2)";
                    mask.Parameters.Add(new NpgsqlParameter { Value = pairEnts, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    mask.Parameters.Add(new NpgsqlParameter { Value = pairTypes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
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
            if (deposited.Count + todo.Count > DepositedMaskPairsCap / MaskShards)
                deposited.Clear();
            deposited.UnionWith(todo);
        }
    }

    private static int MaskShard(Hash128 entity)
        => (int)((uint)entity.GetHashCode() % (uint)MaskShards);

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
    /// The machine-sized fold plan's outstanding deltas act as backpressure on RAM.
    /// </summary>
    private async Task EnqueueFoldAsync(
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> delta, CancellationToken ct)
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
        return _inner.BeginBulkRunAsync(ct);
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
            _log.LogError(ex, "dispose: queued consensus fold failed");
        }
        _foldDepth.Dispose();
        _foldConnections.Dispose();
    }
}
