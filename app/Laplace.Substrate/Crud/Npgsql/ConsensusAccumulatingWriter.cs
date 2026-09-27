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

        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT to_regprocedure("
            + "'consensus.upsert_type(bytea,bytea[],bytea[],bigint[],bigint[],bigint[],timestamptz[],bigint[],bigint[],bigint[],bigint[],bigint[],bigint[])') "
            + "IS NOT NULL";
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

        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT to_regclass('laplace.apply_write_epoch') IS NOT NULL";
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

    public async Task<ApplyResult> ApplyConversationTurnAsync(
        SubstrateChange change, Hash128 sessionId, IReadOnlyList<Hash128> turnIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(turnIds);
        if (sessionId == Hash128.Zero || turnIds.Count == 0)
            throw new ArgumentException("A conversation append requires a session and ordered turn ids.");
        var ids = turnIds.Select(id => id.ToBytes()).ToArray();
        var retry = Laplace.Ingestion.TransientErrorRetryPolicy.ConcurrencyRetry;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await ApplyCoreAsync(
                    [change], workingSet: true, append: false, default,
                    precommitVerifier: null, ct,
                    async (connection, transaction, token) =>
                    {
                        await using var command = new NpgsqlCommand(
                            SqlCatalog.Get("conversation.append_turns").Text,
                            connection, transaction);
                        command.Parameters.AddWithValue(NpgsqlDbType.Bytea, sessionId.ToBytes());
                        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, ids);
                        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz,
                            change.Metadata.BuiltAt.ToUniversalTime());
                        command.Parameters.AddWithValue(NpgsqlDbType.Bytea, change.Metadata.IntentId.ToBytes());
                        long physicalityBudget = IngestSizing.ResolveWorkingSetBudgetBytes();
                        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, physicalityBudget);
                        command.Parameters.AddWithValue(NpgsqlDbType.Integer, 512);
                        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, physicalityBudget / MemoryTopology.Hash128Bytes);
                        void OnSessionNotice(object sender, NpgsqlNoticeEventArgs notice)
                        {
                            const string prefix = "session descriptor view unavailable; transaction pending: ";
                            if (notice.Notice.MessageText.StartsWith(prefix, StringComparison.Ordinal))
                                _log.LogInformation("SESSION_PHYSICALITY_VIEW transaction_pending=true receipt={Receipt}",
                                    notice.Notice.MessageText[prefix.Length..]);
                        }
                        connection.Notice += OnSessionNotice;
                        try { await command.ExecuteScalarAsync(token).ConfigureAwait(false); }
                        finally { connection.Notice -= OnSessionNotice; }
                    }).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt + 1 < retry.MaxAttempts && retry.IsTransient(ex))
            {
                TimeSpan delay = retry.DelayBeforeAttempt(attempt, Random.Shared);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

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
    /// One rating period within a cell: the opponent rating and opponent deviation
    /// (phi), fixed-point 1e9. Ordered so a cell's periods serialize stably.
    /// </summary>
    private readonly record struct PeriodKey(long OpponentRatingFp1e9, long PhiFp1e9)
        : IComparable<PeriodKey>
    {
        public int CompareTo(PeriodKey other)
        {
            int c = OpponentRatingFp1e9.CompareTo(other.OpponentRatingFp1e9);
            return c != 0 ? c : PhiFp1e9.CompareTo(other.PhiFp1e9);
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
        CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? appendConversation = null)
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
            if (appendConversation is not null && !atomicWorkingSet)
                throw new InvalidOperationException(
                    "Conversation history requires the atomic evidence-and-consensus writer.");
            if (hasEphemeralFolds && !atomicWorkingSet)
                throw new InvalidOperationException(
                    "ephemeral fold inputs require the journaled atomic writer; "
                    + "a categorical receipt cannot be score-replayed after a separate commit");

            var delta = atomicWorkingSet ? null : BuildDelta(changes);
            AtomicFoldStats atomicStats = default;
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
                            atomicStats = await UpsertDeltaInTransactionAsync(
                                acceptedDelta, connection, transaction, token).ConfigureAwait(false);

                        if (appendConversation is not null)
                            await appendConversation(connection, transaction, token).ConfigureAwait(false);

                        // Source-integrity verification runs inside the same transaction, after
                        // the fold, so a failed check rolls back evidence and standing together.
                        if (precommitVerifier is not null)
                            await precommitVerifier(token).ConfigureAwait(false);
                    },
                    ct).ConfigureAwait(false);

                if (!result.JournalReplayHit)
                {
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

    /// <summary>
    /// Operational relation types that never fold into consensus: file-metadata edges
    /// ride inside ordinary working-set changes, so they are excluded row-by-row.
    /// Ingest completion is not here: it is operational state in its own tables and
    /// never reaches the attestation stream.
    /// </summary>
    private static readonly HashSet<Hash128> OpsMarkerTypeIds =
        [Laplace.Decomposers.Abstractions.FileEntity.MetadataRelationTypeId];

    /// <summary>Merges attestations [start, end) of the flattened block space into
    /// <paramref name="map"/>; returns the observation count it consumed.</summary>
    private static long MergeRange(
        List<ImmutableArray<AttestationRow>> blocks, long start, long end,
        Dictionary<(Hash128, Hash128, Hash128?), Delta> map,
        IReadOnlyDictionary<Hash128, EphemeralFoldInput> ephemeralByReceipt)
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
                bool hasEphemeral = ephemeralByReceipt.TryGetValue(a.Id, out var ephemeral);
                if (!a.FoldReplayable && !hasEphemeral)
                    throw new InvalidOperationException(
                        $"non-replayable receipt {a.Id} has no atomic ephemeral fold input");
                var key = (a.SubjectId, a.TypeId, a.ObjectId);
                long score = hasEphemeral
                    ? checked(ephemeral!.ScoreFp1e9 * a.ObservationCount)
                    : AttestationMergeMath.RowScoreTotal(a);
                ref var d = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out bool existed);
                if (!existed)
                {
                    d.FirstPeriod = new(a.OpponentRatingFp1e9, a.OpponentRdFp1e9);
                    d.FirstAggregate = new()
                    {
                        Games = a.ObservationCount,
                        SumScoreFp1e9 = score,
                    };
                    d.Games = d.FirstAggregate.Games;
                    d.SumScoreFp1e9 = d.FirstAggregate.SumScoreFp1e9;
                    d.MaxTsUnixUs = a.LastObservedAtUnixUs;
                }
                else
                {
                    FoldInto(ref d, a.OpponentRdFp1e9, a.OpponentRatingFp1e9,
                             a.ObservationCount,
                             score, a.LastObservedAtUnixUs);
                }
                obs += a.ObservationCount;
            }
            pos = blockEnd;
        }
        return obs;
    }

    private static void FoldInto(ref Delta d, long phi, long oppRating, long games, long score, long tsUnixUs)
    {
        AddPeriod(ref d, new(oppRating, phi), games, score);
        d.Games = AttestationMergeMath.SafeAddGames(d.Games, games);
        d.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(d.SumScoreFp1e9, score);
        if (tsUnixUs > d.MaxTsUnixUs) d.MaxTsUnixUs = tsUnixUs;
    }

    private static void FoldDelta(ref Delta d, in Delta src)
    {
        AddPeriod(ref d, src.FirstPeriod, src.FirstAggregate.Games,
                  src.FirstAggregate.SumScoreFp1e9);
        if (src.AdditionalPeriods is not null)
            foreach (var (key, value) in src.AdditionalPeriods)
                AddPeriod(ref d, key, value.Games, value.SumScoreFp1e9);
        d.Games = AttestationMergeMath.SafeAddGames(d.Games, src.Games);
        d.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(
            d.SumScoreFp1e9, src.SumScoreFp1e9);
        if (src.MaxTsUnixUs > d.MaxTsUnixUs) d.MaxTsUnixUs = src.MaxTsUnixUs;
    }

    private static void AddPeriod(
        ref Delta d, PeriodKey key, long games, long sumScoreFp1e9)
    {
        if (key == d.FirstPeriod)
        {
            d.FirstAggregate.Games = AttestationMergeMath.SafeAddGames(
                d.FirstAggregate.Games, games);
            d.FirstAggregate.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(
                d.FirstAggregate.SumScoreFp1e9, sumScoreFp1e9);
            return;
        }

        d.AdditionalPeriods ??= new();
        ref var aggregate = ref CollectionsMarshal.GetValueRefOrAddDefault(
            d.AdditionalPeriods, key, out bool existed);
        if (!existed)
            aggregate = new() { Games = games, SumScoreFp1e9 = sumScoreFp1e9 };
        else
        {
            aggregate.Games = AttestationMergeMath.SafeAddGames(aggregate.Games, games);
            aggregate.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(
                aggregate.SumScoreFp1e9, sumScoreFp1e9);
        }
    }

    private static int PeriodCount(in Delta d) => 1 + (d.AdditionalPeriods?.Count ?? 0);

    private static void WritePeriods(
        in Delta d, long[] opponentRatings, long[] phis, long[] games,
        long[] sums, ref int at)
    {
        if (d.AdditionalPeriods is null)
        {
            opponentRatings[at] = d.FirstPeriod.OpponentRatingFp1e9;
            phis[at] = d.FirstPeriod.PhiFp1e9;
            games[at] = d.FirstAggregate.Games;
            sums[at++] = d.FirstAggregate.SumScoreFp1e9;
            return;
        }

        // Stable physical payload independent of input order, dictionary order,
        // which parallel shard became the merge target, and batch worker count.
        var periods = new KeyValuePair<PeriodKey, PeriodAggregate>[
            d.AdditionalPeriods.Count + 1];
        periods[0] = new(d.FirstPeriod, d.FirstAggregate);
        int copied = 1;
        foreach (var period in d.AdditionalPeriods)
            periods[copied++] = period;
        Array.Sort(periods, static (x, y) => x.Key.CompareTo(y.Key));
        foreach (var (key, aggregate) in periods)
        {
            opponentRatings[at] = key.OpponentRatingFp1e9;
            phis[at] = key.PhiFp1e9;
            games[at] = aggregate.Games;
            sums[at++] = aggregate.SumScoreFp1e9;
        }
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

    private readonly record struct AtomicFoldStats(
        long Cells,
        long Masks,
        long ConsensusBackendTicks,
        long MaskBackendTicks,
        long FoldCalls,
        long MaskCalls,
        long MaskPairs);

    /// <summary>
    /// Folds a journaled working set on the evidence writer's transaction. The
    /// journal replay claim, attestation merge, consensus merge and mask deposit
    /// commit or roll back together. Cells are sorted (type, edge id, subject) and
    /// mask pairs (entity, type), the same lock order the lanes use.
    /// </summary>
    private async Task<AtomicFoldStats> UpsertDeltaInTransactionAsync(
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> delta,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        var cells = new ((Hash128 S, Hash128 T, Hash128? O) Key, Hash128 Cid, Delta D)[delta.Count];
        int cellAt = 0;
        foreach (var (key, value) in delta)
            cells[cellAt++] = (key, ConsensusKeys.EdgeId(key.S, key.T, key.O ?? default), value);
        Array.Sort(cells, static (x, y) =>
        {
            int c = x.Key.T.CompareToBytewise(y.Key.T);
            if (c != 0) return c;
            c = x.Cid.CompareToBytewise(y.Cid);
            return c != 0 ? c : x.Key.S.CompareToBytewise(y.Key.S);
        });

        long folded = 0;
        long foldCalls = 0;
        long foldStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int off = 0; off < cells.Length; off += AtomicFoldChunkCells)
        {
            int count = Math.Min(AtomicFoldChunkCells, cells.Length - off);
            var subjects = new byte[count][];
            var types = new byte[count][];
            var objects = new byte[count][];
            var phis = new long[count];
            var opponents = new long[count];
            var games = new long[count];
            var sums = new long[count];
            var timestamps = new DateTime[count];
            int periodCount = 0;
            for (int i = 0; i < count; i++)
                periodCount = checked(periodCount + PeriodCount(in cells[off + i].D));
            var periodOffsets = new long[count + 1];
            var periodOpponents = new long[periodCount];
            var periodPhis = new long[periodCount];
            var periodGames = new long[periodCount];
            var periodSums = new long[periodCount];
            int periodAt = 0;
            for (int i = 0; i < count; i++)
            {
                var cell = cells[off + i];
                subjects[i] = cell.Key.S.ToBytes();
                types[i] = cell.Key.T.ToBytes();
                objects[i] = cell.Key.O?.ToBytes()!;
                phis[i] = cell.D.FirstPeriod.PhiFp1e9;
                opponents[i] = cell.D.FirstPeriod.OpponentRatingFp1e9;
                games[i] = cell.D.Games;
                sums[i] = cell.D.SumScoreFp1e9;
                timestamps[i] = TsFromUnixUs(cell.D.MaxTsUnixUs);
                periodOffsets[i] = periodAt;
                WritePeriods(
                    in cell.D, periodOpponents, periodPhis,
                    periodGames, periodSums, ref periodAt);
            }
            periodOffsets[count] = periodAt;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 0;
            // One call folds the whole mixed-type chunk; native code groups it by
            // type partition, locks and folds each type run.
            command.CommandText =
                "SELECT consensus.merge_evidence($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)";
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, subjects);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, types);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, objects);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, phis);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, games);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, sums);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, timestamps);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, opponents);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, periodOffsets);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, periodOpponents);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, periodPhis);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, periodGames);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, periodSums);
            folded += (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            foldCalls++;
        }
        long foldTicks = System.Diagnostics.Stopwatch.GetTimestamp() - foldStarted;

        var maskPairs = new HashSet<(Hash128 Entity, Hash128 Type)>(cells.Length * 2);
        foreach (var cell in cells)
        {
            maskPairs.Add((cell.Key.S, cell.Key.T));
            if (cell.Key.O is { } objectId) maskPairs.Add((objectId, cell.Key.T));
        }
        var orderedPairs = maskPairs.ToList();
        orderedPairs.Sort(static (x, y) =>
        {
            int c = x.Entity.CompareToBytewise(y.Entity);
            return c != 0 ? c : x.Type.CompareToBytewise(y.Type);
        });

        long masks = 0;
        long maskCalls = 0;
        long maskStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int off = 0; off < orderedPairs.Count; off += AtomicFoldChunkCells)
        {
            int count = Math.Min(AtomicFoldChunkCells, orderedPairs.Count - off);
            var entities = new byte[count][];
            var maskTypes = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                entities[i] = orderedPairs[off + i].Entity.ToBytes();
                maskTypes[i] = orderedPairs[off + i].Type.ToBytes();
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 0;
            command.CommandText = "SELECT consensus.highway_mask_deposit($1,$2)";
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, entities);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, maskTypes);
            masks += (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            maskCalls++;
        }
        long maskTicks = System.Diagnostics.Stopwatch.GetTimestamp() - maskStarted;
        return new AtomicFoldStats(
            folded, masks, foldTicks, maskTicks,
            foldCalls, maskCalls, orderedPairs.Count);
    }

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
                await using var up = conn.CreateCommand();
                up.Transaction = tx;
                up.CommandTimeout = 0;
                string foldSql = directRoute
                    ? "SELECT consensus.upsert_type($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)"
                    : "SELECT consensus.upsert($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)";
                // The epoch bump and the fold are one server statement. MATERIALIZED
                // makes nextval execute before the write function while keeping one
                // preparable statement with positional parameters.
                up.CommandText = epochBump
                    ? "WITH epoch AS MATERIALIZED (SELECT nextval('laplace.apply_write_epoch')) "
                        + foldSql + " FROM epoch"
                    : foldSql;
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
                up.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                up.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                up.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                up.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<long>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
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
            pairs.RemoveAll(deposited.Contains);
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
                    await using var mask = conn.CreateCommand();
                    mask.Transaction = tx;
                    mask.CommandTimeout = 0;
                    const string depositSql =
                        "SELECT consensus.highway_mask_deposit($1, $2)";
                    mask.CommandText = epochBump
                        ? "WITH epoch AS MATERIALIZED (SELECT nextval('laplace.apply_write_epoch')) "
                            + depositSql + " FROM epoch"
                        : depositSql;
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
