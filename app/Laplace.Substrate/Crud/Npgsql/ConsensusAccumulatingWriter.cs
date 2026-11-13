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
/// Folds admitted testimony into consensus: the "fold in sets" step of the shared
/// ingest recipe. Over the Npgsql writer, each working set's evidence COPY, the fold of
/// exactly the attestations that transaction admitted, the highway-mask deposit, any
/// conversation append and any pre-commit verifier run in one transaction, so a durable
/// journal token implies current standing for the testimony it carries. Over any other
/// inner writer the delta folds after the apply: inline outside a bulk run, or on the
/// background per-type lanes during one, drained before the run completes. Evidence is
/// always persisted; consensus is folded testimony, never a substitute for it.
/// </summary>
public sealed partial class ConsensusAccumulatingWriter : ISubstrateWriter, IConsensusFoldMetrics, IPhysicalityClosure, IAsyncDisposable
{
    public const string PeriodBoundaryUnitPrefix = IngestBatchPipeline.PeriodBoundaryUnitPrefix;

    public (long Written, long Unplaced) TakePhysicalityClosure() =>
        _inner is IPhysicalityClosure closure ? closure.TakePhysicalityClosure() : (0, 0);

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

        await using var probe = NpgsqlCatalog.Command(connection, null, "consensus.supports_type_upsert");
        bool supported = (bool)(await probe.ExecuteScalarAsync(ct) ?? false);
        Interlocked.CompareExchange(ref _directConsensusRoute, supported ? 1 : 0, -1);
        return Volatile.Read(ref _directConsensusRoute) == 1;
    }

    // Fold-segment and mask-deposit transactions bump laplace.apply_write_epoch
    // before their writes, so a later apply can prove no writer intervened instead
    // of re-probing. Probed once per writer, like the direct-route gate above; an
    // installation without the sequence folds without the bump.
    private int _applyWriteEpochRoute = -1;

    private async ValueTask<bool> SupportsApplyWriteEpochAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        int known = Volatile.Read(ref _applyWriteEpochRoute);
        if (known >= 0) return known == 1;

        await using var probe = NpgsqlCatalog.Command(connection, null, "write.supports_epoch");
        bool supported = (bool)(await probe.ExecuteScalarAsync(ct) ?? false);
        Interlocked.CompareExchange(ref _applyWriteEpochRoute, supported ? 1 : 0, -1);
        return Volatile.Read(ref _applyWriteEpochRoute) == 1;
    }

    // Per-type fold lanes. Consensus is LIST-partitioned by type_id, so lanes keyed
    // by type never share a row and run concurrently without contending. A cell has
    // exactly one type, so it stays on one FIFO lane, which keeps the
    // non-commutative Glicko-2 fold deterministic. See DispatchDeltaAsync.
    private readonly object _laneLock = new();
    private readonly Dictionary<Hash128, Task> _typeLanes = new();

    // Stable entity-sharded mask lanes. An entity maps to exactly one lane for the
    // whole run, so lanes are row-disjoint and run in parallel, while deposits that
    // can touch the same entity are FIFO.
    private readonly Task[] _maskLanes =
        Enumerable.Repeat(Task.CompletedTask, MaskShards).ToArray();

    // Non-atomic bulk runs fold each delta in the background so the apply lane
    // persists the next working set meanwhile. Ordering is the per-type lanes;
    // _foldDepth bounds only how many deltas are alive in memory. Lanes are drained
    // at FinalizeSource/CompleteBulkRun/Dispose, so ingest completion is fold
    // completion, and a failed fold poisons its lane and surfaces at the next apply
    // or at the drain. Outside a bulk run the fold is awaited inline so the next
    // read sees it. FoldSizing is the one sizing authority for chunk width,
    // pipeline depth, mask-pair capacity and connection fan-out.
    private static readonly IngestSizing.ConsensusFoldPlan FoldSizing =
        IngestSizing.ResolveConsensusFold(IngestTopology.Current.ApplyPartitions);
    // The atomic evidence+consensus path runs on one backend in one transaction, so
    // its chunk width is sized for one partition, not divided by ApplyPartitions.
    private static readonly int AtomicFoldChunkCells =
        IngestSizing.ResolveConsensusFold(1).ChunkCells;
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

    // Global connection budget for the fold, shared by every type lane and every
    // mask lane. FoldConnections bounds one Parallel.ForEachAsync; this gate bounds
    // the total across all lanes and deltas live at once.
    private readonly SemaphoreSlim _foldConnections = new(FoldConnections, FoldConnections);

    // Run-scoped pair dedup, owned by the same stable shard as the entity. Each set
    // is touched only by its FIFO lane. Clearing is a memory valve: it costs a
    // server-side idempotent recheck, never correctness.
    private readonly HashSet<(Hash128 Ent, Hash128 Typ)>[] _depositedMaskPairs =
        Enumerable.Range(0, MaskShards)
            .Select(_ => new HashSet<(Hash128 Ent, Hash128 Typ)>()).ToArray();
    private static int DepositedMaskPairsCap => FoldSizing.MaskPairCapacity;

    // Masks deposit inline in every lane, bulk included; nothing is deferred to the
    // end of the run. See DispatchDeltaAsync.

    public ConsensusAccumulatingWriter(
        ISubstrateWriter inner, NpgsqlDataSource dataSource,
        bool? persistEvidence = null,
        ILogger<ConsensusAccumulatingWriter>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        if (persistEvidence == false)
            throw new ArgumentException(
                "Consensus is folded testimony. A deposit cannot fold standing and drop laplace.attestations.");
        _persistEvidence = true;
        _log = logger ?? (ILogger)NullLogger<ConsensusAccumulatingWriter>.Instance;
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
        => await ApplyCoreAsync(
            changes, workingSet: false, append: false, default,
            precommitVerifier: null, ct);

    public Task<ApplyResult> ApplyWorkingSetAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyWorkingSetAsync(new[] { change }, ct);

    public async Task<ApplyResult> ApplyWorkingSetAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        => await ApplyCoreAsync(
            changes, workingSet: true, append: false, default,
            precommitVerifier: null, ct);

    public async Task<ApplyResult> ApplyWorkingSetAsync(
        IReadOnlyList<SubstrateChange> changes,
        Func<CancellationToken, ValueTask> precommitVerifier,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(precommitVerifier);
        return await ApplyCoreAsync(
            changes, workingSet: true, append: false, default,
            precommitVerifier, ct);
    }

    public async Task<ApplyResult> AppendAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128 sourceId, CancellationToken ct = default)
        => await ApplyCoreAsync(
            changes, workingSet: false, append: true, sourceId,
            precommitVerifier: null, ct);

    /// <summary>
    /// One rating period within a cell: the opponent rating, opponent deviation
    /// (phi), and exact unit score, fixed-point 1e9. Ordered so a cell's periods
    /// serialize stably and match the SQL evidence grouping in consensus.evidence_rows_batch.
    /// </summary>
    private readonly record struct PeriodKey(
        long OpponentRatingFp1e9, long PhiFp1e9, long UnitScoreFp1e9)
        : IComparable<PeriodKey>
    {
        public int CompareTo(PeriodKey other)
        {
            int c = OpponentRatingFp1e9.CompareTo(other.OpponentRatingFp1e9);
            if (c != 0) return c;
            c = PhiFp1e9.CompareTo(other.PhiFp1e9);
            return c != 0 ? c : UnitScoreFp1e9.CompareTo(other.UnitScoreFp1e9);
        }
    }

    private struct PeriodAggregate
    {
        public long Games;
        public long SumScoreFp1e9;
    }

    private struct Delta
    {
        // Held inline in the delta map and mutated through a ref from
        // CollectionsMarshal.GetValueRefOrAddDefault, so a merge is in place with one
        // lookup. The single-period shape is inline; a dictionary is allocated only
        // when a cell holds a second (opponent rating, opponent deviation) pair.
        public PeriodKey FirstPeriod;
        public PeriodAggregate FirstAggregate;
        public Dictionary<PeriodKey, PeriodAggregate>? AdditionalPeriods;
        public long Games;
        public long SumScoreFp1e9;
        public long MaxTsUnixUs;
    }

    private async Task<ApplyResult> ApplyCoreAsync(
        IReadOnlyList<SubstrateChange> changes, bool workingSet, bool append,
        Hash128 sourceId,
        Func<CancellationToken, ValueTask>? precommitVerifier,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));
        Interlocked.Increment(ref _inflightApplies);
        try
        {
            if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));

            bool atomicWorkingSet = _persistEvidence && _inner is NpgsqlSubstrateWriter;
            bool hasEphemeralFolds = changes.Any(c => !c.EphemeralFoldInputs.IsDefaultOrEmpty);

            // File completions commit in the same evidence transaction as the file's
            // testimony and its fold, so a durable completion implies current
            // consensus for that file without a second post-commit apply.
            IReadOnlyList<SubstrateChange> evidenceChanges = changes;

            // A fold that already failed in the background poisons the run before any
            // more evidence lands.
            await ObserveFoldFailureAsync().ConfigureAwait(false);

            var forwarded = ForwardChanges(evidenceChanges);
            if (precommitVerifier is not null && !atomicWorkingSet)
                throw new InvalidOperationException(
                    "source integrity verification requires the atomic evidence-and-consensus writer");
            if (hasEphemeralFolds && !atomicWorkingSet)
                throw new InvalidOperationException(
                    "ephemeral fold inputs require the journaled atomic writer; "
                    + "a categorical receipt cannot be score-replayed after a separate commit");

            var delta = atomicWorkingSet ? null : BuildDelta(changes);
            AtomicFoldStats atomicStats = default;
            List<(Hash128 Entity, Hash128 Type)>? committedMaskPairs = null;
            ApplyResult result;
            if (atomicWorkingSet)
            {
                result = await ((NpgsqlSubstrateWriter)_inner).ApplyWorkingSetAtomicAsync(
                    forwarded,
                    async (connection, transaction, acceptance, token) =>
                    {
                        if (_bulkRun)
                            Interlocked.CompareExchange(
                                ref _foldSpanStarted,
                                System.Diagnostics.Stopwatch.GetTimestamp(),
                                comparand: 0);

                        // The working set's consensus delta is exactly the novel evidence
                        // this transaction admitted, managed and native alike. It folds on
                        // this connection before commit, so a durable journal token always
                        // implies current standing for the testimony it carries.
                        var acceptedDelta = BuildDelta(
                            changes, acceptance.AttestationIds, acceptance.Rows);
                        if (acceptedDelta is { Count: > 0 })
                            (atomicStats, committedMaskPairs) = await UpsertDeltaInTransactionAsync(
                                acceptedDelta, connection, transaction, token).ConfigureAwait(false);

                        // Source-integrity verification runs inside the same transaction, after
                        // the fold, so a failed check rolls back evidence and standing together.
                        if (precommitVerifier is not null)
                            await precommitVerifier(token).ConfigureAwait(false);
                    },
                    ct).ConfigureAwait(false);

                if (!result.JournalReplayHit)
                {
                    RecordCommittedMaskPairs(committedMaskPairs);
                    Interlocked.Add(ref _cellsFolded, atomicStats.Cells);
                    Interlocked.Add(ref _consensusBackendTicks, atomicStats.ConsensusBackendTicks);
                    Interlocked.Add(ref _highwayMaskBackendTicks, atomicStats.MaskBackendTicks);
                    Interlocked.Add(ref _consensusUpsertCalls, atomicStats.FoldCalls);
                    Interlocked.Add(ref _highwayMaskCalls, atomicStats.MaskCalls);
                    Interlocked.Add(ref _highwayMaskPairs, atomicStats.MaskPairs);
                }
            }
            else
            {
                result = append
                    ? await _inner.AppendAsync(forwarded, sourceId, ct)
                    : workingSet
                        ? await _inner.ApplyWorkingSetAsync(forwarded, ct)
                        : await _inner.ApplyManyAsync(forwarded, ct);
            }

            // A non-atomic inner writer has already committed; its delta folds now,
            // on the background lanes during a bulk run and inline otherwise.
            if (!atomicWorkingSet && delta is { Count: > 0 } && !result.JournalReplayHit)
            {
                if (_bulkRun) await EnqueueFoldAsync(delta, changes, CancellationToken.None);
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
        IReadOnlyList<SubstrateChange> changes, IReadOnlySet<Hash128>? admittedAttestations = null,
        IReadOnlyList<AttestationRow>? acceptedRows = null)
    {
        // Flatten to the attestation arrays that carry testimony. The merge below
        // runs over one contiguous index space across them, so sharding is
        // independent of change boundaries (a working set is often one change).
        var ephemeralByReceipt = new Dictionary<Hash128, EphemeralFoldInput>();
        foreach (var c in changes)
        {
            if (c.EphemeralFoldInputs.IsDefaultOrEmpty) continue;
            foreach (var input in c.EphemeralFoldInputs)
            {
                if (admittedAttestations is not null && !admittedAttestations.Contains(input.AttestationId))
                    continue;
                if (input.ScoreFp1e9 < 0 || input.ScoreFp1e9 > 1_000_000_000
                    || input.CalculationReceiptId == default
                    || !ephemeralByReceipt.TryAdd(input.AttestationId, input))
                    throw new InvalidOperationException("invalid or duplicate ephemeral fold input");
            }
        }
        List<ImmutableArray<AttestationRow>>? blocks = null;
        long total = 0;
        foreach (var c in changes)
            if (!c.TestimonyWalks.IsDefaultOrEmpty)
                throw new InvalidOperationException(
                    "testimony walks are no longer journaled — the consensus fold is inline; "
                    + "emit aggregated attestations (observation_count/sum_score) instead");
        if (acceptedRows is not null)
        {
            // The writer's accepted set is the complete admitted testimony of this
            // working set, collapsed per attestation id exactly as it was persisted.
            // Layer and file completion changes are operational boundaries whose rows
            // are persisted but never folded.
            HashSet<Hash128>? boundaryIds = null;
            foreach (var c in changes)
                if (c.Metadata.SourceContentUnitName.StartsWith("layer-complete/", StringComparison.Ordinal)
                    || c.Metadata.SourceContentUnitName.StartsWith(PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
                    foreach (var a in c.Attestations) (boundaryIds ??= new()).Add(a.Id);
            var folded = boundaryIds is null ? ImmutableArray.CreateRange(acceptedRows)
                : acceptedRows.Where(a => !boundaryIds.Contains(a.Id)).ToImmutableArray();
            if (folded.Length > 0)
            {
                (blocks = new()).Add(folded);
                total = folded.Length;
            }
        }
        else foreach (var c in changes)
        {
            if (c.Metadata.SourceContentUnitName.StartsWith("layer-complete/", StringComparison.Ordinal)
                || c.Metadata.SourceContentUnitName.StartsWith(PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
                continue;
            if (c.Attestations.IsEmpty) continue;
            var accepted = admittedAttestations is null ? c.Attestations
                : c.Attestations.Where(a => admittedAttestations.Contains(a.Id)).ToImmutableArray();
            if (accepted.IsEmpty) continue;
            (blocks ??= new()).Add(accepted);
            total += accepted.Length;
        }
        if (blocks is null || total == 0)
        {
            if (ephemeralByReceipt.Count != 0)
                throw new InvalidOperationException(
                    "ephemeral fold input has no foldable staged receipt");
            return null;
        }

        // A receipt key in the transient side channel is meaningful only when
        // it resolves to the exact non-replayable evidence row that will join
        // this fold.  Silently ignoring a typo, an ops marker, or a row omitted
        // by a completion boundary would commit a categorical receipt without
        // its calibrated observation.
        var resolvedEphemeralReceipts = new HashSet<Hash128>();
        foreach (var atts in blocks)
        foreach (var a in atts)
        {
            bool hasEphemeral = ephemeralByReceipt.ContainsKey(a.Id);
            if (!a.FoldReplayable && !hasEphemeral)
                throw new InvalidOperationException(
                    $"non-replayable receipt {a.Id} has no atomic ephemeral fold input");
            if (!hasEphemeral) continue;
            if (a.FoldReplayable || OpsMarkerTypeIds.Contains(a.TypeId)
                || !resolvedEphemeralReceipts.Add(a.Id))
                throw new InvalidOperationException(
                    $"ephemeral fold receipt {a.Id} is not one unique foldable non-replayable attestation");
        }
        if (resolvedEphemeralReceipts.Count != ephemeralByReceipt.Count)
            throw new InvalidOperationException(
                "ephemeral fold input has no matching foldable staged receipt");

        // The merge is order-independent: every combine is integer add over
        // fixed-point 1e9 longs or max on the timestamp, both associative and
        // commutative, so any shard split and combine order yields the
        // bit-identical delta a serial walk yields. Shards count observations
        // locally and publish once.
        int workers = _bulkRun
            ? (int)Math.Min(total, Math.Max(1, CpuTopology.PerformanceCoreCount))
            : 1;

        if (workers == 1)
        {
            var single = NewDeltaMap((int)Math.Min(total, int.MaxValue));
            long obs = MergeRange(blocks, 0, total, single, ephemeralByReceipt);
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
            shardObs[w] = end > start ? MergeRange(blocks, start, end, map, ephemeralByReceipt) : 0;
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
                else FoldDelta(ref d, in src);
            }
        }
        return delta.Count == 0 ? null : delta;
    }

    private static Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> NewDeltaMap(int hint) =>
        new(Math.Clamp(hint, 1, FoldSizing.DeltaCapacityCells));

}
