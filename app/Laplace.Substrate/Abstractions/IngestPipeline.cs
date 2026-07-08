using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public interface IRecordStream<TRecord>
{
    IAsyncEnumerable<TRecord> RecordsAsync(CancellationToken ct = default);
}

public interface IMultiFileRecordStream<TRecord>
{
    IAsyncEnumerable<(string FileLabel, TRecord Record)> RecordsAsync(CancellationToken ct = default);
}

public interface IIngestDeferredUnit : IDisposable
{
    TierTree? TreeForBatchProbe { get; }

    Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct = default);

    Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap);
}

public interface IMultiTreeIngestDeferredUnit : IIngestDeferredUnit
{
    IReadOnlyList<TierTree?> AllProbeTrees { get; }

    Hash128 DrainInto(
        SubstrateChangeBuilder builder, double witnessWeight, ReadOnlySpan<byte[]?> perTreeBitmaps);
}

/// <summary>
/// Record whose content-addressed trunk root is known before expensive compose work
/// (chess GameId, content hash, grammar row root, …). Enables the existence gate to
/// bulk-probe and short-circuit present roots without a deferred unit.
/// </summary>
public interface ITrunkRootRecord
{
    Hash128 TrunkRootId { get; }
}

public interface IIngestRecordHandler<TRecord>
{
    ValueTask<bool> TryTrunkShortcircuitAsync(
        TRecord record,
        SubstrateChangeBuilder builder,
        ISubstrateReader reader,
        double witnessWeight,
        CancellationToken ct = default);

    IIngestDeferredUnit CreateDeferredUnit(TRecord record);

    void WalkWitness(TRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit);

    long UnitsPerRecord(TRecord record) => 1;
}

/// <summary>Handler with per-batch dedup state that must reset after each yielded change.</summary>
public interface IIngestBatchScopedHandler
{
    void ResetBatchState();
}

public sealed class IngestBatchConfig
{
    public required Hash128 SourceId { get; init; }
    public required string BatchLabelPrefix { get; init; }
    public int BatchSize { get; init; } = 256;
    public int ProbeChunkSize { get; init; } = 1024;
    public double WitnessWeight { get; init; } = 1.0;

    /// <summary>
    /// Rule #8 working-set mode (06_Engineering_Ruleset.txt). One builder spans
    /// the record stream; O(tiers) existence runs every flush interval (at most
    /// five tier rounds per batch); one SubstrateChange per working set unless
    /// the memory budget valve splits it. BatchSize is ignored in this mode.
    /// </summary>
    public bool WorkingSet { get; init; }

    /// <summary>Records per O(tiers) existence interval in working-set mode.</summary>
    public int? WorkingSetProbeInterval { get; init; }

    /// <summary>
    /// Max records accumulated in one working set before descent/apply/yield.
    /// Sized from <see cref="IngestSizing.ResolveWorkingSetRecordCap"/> for the source
    /// profile — closes the set when deferred tier trees would exceed the RAM budget
    /// even if <see cref="SubstrateChangeBuilder.StagedBytesEstimate"/> is still low.
    /// </summary>
    public int? WorkingSetRecordCap { get; init; }

    /// <summary>Per-source byte model for the working-set memory estimate valve.</summary>
    public IngestSourceProfile? WorkingSetProfile { get; init; }
    public int CommitEpoch { get; init; }
    public ISubstrateReader? ContainmentReader { get; init; }
    public Action<long>? ReportUnits { get; init; }
    public long MaxInputUnits { get; init; }

    public bool EnableDeferredContentOnBuilder { get; init; } = false;

    public int? EntityCapacity { get; init; }
    public int? PhysicalityCapacity { get; init; }
    public int? AttestationCapacity { get; init; }

    public SubstrateChangeBuilder NewBuilder(int batchNumber)
    {
        var b = new SubstrateChangeBuilder(SourceId, $"{BatchLabelPrefix}/{batchNumber}", null,
            entityCapacity: EntityCapacity ?? BatchSize,
            physicalityCapacity: PhysicalityCapacity ?? BatchSize,
            attestationCapacity: AttestationCapacity ?? BatchSize * 4)
            .SetCommitEpoch(CommitEpoch);
        if (EnableDeferredContentOnBuilder && ContainmentReader is not null)
            b.EnableDeferredContent(ContainmentReader);
        return b;
    }

    internal ISubstrateReader? EffectiveReader => ContainmentReader;

    public IngestBatchConfig WithMaxInputUnits(long max) =>
        new()
        {
            SourceId = SourceId,
            BatchLabelPrefix = BatchLabelPrefix,
            BatchSize = BatchSize,
            ProbeChunkSize = ProbeChunkSize,
            WitnessWeight = WitnessWeight,
            CommitEpoch = CommitEpoch,
            ContainmentReader = ContainmentReader,
            ReportUnits = ReportUnits,
            MaxInputUnits = max,
            EnableDeferredContentOnBuilder = EnableDeferredContentOnBuilder,
            EntityCapacity = EntityCapacity,
            PhysicalityCapacity = PhysicalityCapacity,
            AttestationCapacity = AttestationCapacity,
            WorkingSet = WorkingSet,
            WorkingSetProbeInterval = WorkingSetProbeInterval,
            WorkingSetRecordCap = WorkingSetRecordCap,
            WorkingSetProfile = WorkingSetProfile,
        };
}

public static class IngestBatchPipeline
{
    public const string PeriodBoundaryUnitPrefix = "period-boundary/";

    /// <summary>Consensus fold trigger + ingest file-progress marker (see IngestRunner.TrackIntent).</summary>
    public static SubstrateChange BuildPeriodBoundary(Hash128 sourceId, string fileLabel)
    {
        string stem = fileLabel.Contains('/', StringComparison.Ordinal)
            ? fileLabel[(fileLabel.LastIndexOf('/') + 1)..]
            : fileLabel;
        return new SubstrateChangeBuilder(
            sourceId, $"{PeriodBoundaryUnitPrefix}{stem}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0).Build();
    }

    internal sealed class AllAbsentSubstrateReader : ISubstrateReader
    {
        internal static readonly AllAbsentSubstrateReader Instance = new();

        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default) =>
            Task.FromResult(0L);

        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default) =>
            Task.FromResult(new byte[(candidates.Count + 7) / 8]);

        public Task<byte[]> ContentDescentBitmapAsync(
            IReadOnlyList<Hash128> ids, IReadOnlyList<int> parents, CancellationToken ct = default) =>
            Task.FromResult(new byte[(ids.Count + 7) / 8]);
    }

    public static async IAsyncEnumerable<SubstrateChange> RunAsync<TRecord>(
        IRecordStream<TRecord> stream,
        IIngestRecordHandler<TRecord> handler,
        IngestBatchConfig config,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var reader = config.EffectiveReader ?? AllAbsentSubstrateReader.Instance;

        // Working-set mode: compose every WorkingSetProbeInterval records; O(tiers)
        // existence runs once per working set in FinalizeWorkingSetAsync (06 L93-94a).
        // Legacy batch mode uses ProbeChunkSize and probes every flush.
        int probeInterval = config.WorkingSet
            ? (config.WorkingSetProbeInterval
               ?? IngestSizing.ResolveWorkingSetProbeInterval(
                   config.BatchSize, config.WorkingSetProfile ?? IngestSourceProfile.Default))
            : config.ProbeChunkSize;
        var pending = new List<TRecord>(Math.Min(probeInterval, 65_536));
        var probedAbsent = config.WorkingSet ? new HashSet<Hash128>() : null;

        // Working sets yield nothing mid-stream, which starves every
        // yield-driven progress counter — a monolithic source otherwise
        // composes in total silence until the budget valve. When the caller
        // wired no reporter, heartbeat the console directly.
        var reportUnits = config.ReportUnits;
        if (config.WorkingSet && reportUnits is null)
        {
            string wsLabel = config.BatchLabelPrefix;
            var wsSw = System.Diagnostics.Stopwatch.StartNew();
            reportUnits = n =>
            {
                if (n % 524_288 == 0)
                    Console.WriteLine(
                        $"WS_COMPOSE {wsLabel}: {n:N0} records composed "
                        + $"({n / Math.Max(1e-3, wsSw.Elapsed.TotalSeconds):N0} rec/s)");
            };
        }

        var state = new BatchState(config.NewBuilder(0), resetBankOnBuild: !config.WorkingSet);
        long rowsTotal = 0;
        long unitsConsumed = 0;

        await foreach (var record in stream.RecordsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            rowsTotal++;
            long units = handler.UnitsPerRecord(record);

            if (config.MaxInputUnits > 0 && unitsConsumed >= config.MaxInputUnits)
            {
                await foreach (var change in FlushPending(pending, handler, reader, state, config, probedAbsent, ct))
                {
                    yield return change;
                    ResetBatchScope(handler);
                }
                if (state.InBatch > 0)
                {
                    await state.FinalizeWorkingSetAsync(handler, reader, config, probedAbsent, ct);
                    yield return await state.BuildRemainingAsync(ct);
                    ResetBatchScope(handler);
                }
                yield break;
            }

            pending.Add(record);

            if (pending.Count >= probeInterval)
            {
                await foreach (var change in FlushPending(pending, handler, reader, state, config, probedAbsent, ct))
                {
                    yield return change;
                    ResetBatchScope(handler);
                }
            }

            if (config.WorkingSet && pending.Count > 0
                && ShouldCloseWorkingSet(state, config))
            {
                await foreach (var change in FlushPending(pending, handler, reader, state, config, probedAbsent, ct))
                {
                    yield return change;
                    ResetBatchScope(handler);
                }

                if (state.InBatch > 0)
                {
                    await state.FinalizeWorkingSetAsync(handler, reader, config, probedAbsent, ct);
                    yield return await state.YieldBatchAsync(ct);
                    ResetBatchScope(handler);
                    state.ResetBuilder(config.NewBuilder(state.BatchNumber));
                }
            }
            unitsConsumed += units;

            reportUnits?.Invoke(rowsTotal);

            if (!config.WorkingSet && state.InBatch >= config.BatchSize)
            {
                yield return await state.YieldBatchAsync(ct);
                ResetBatchScope(handler);
                state.ResetBuilder(config.NewBuilder(state.BatchNumber));
            }
        }

        if (pending.Count > 0)
        {
            await foreach (var change in FlushPending(pending, handler, reader, state, config, probedAbsent, ct))
            {
                yield return change;
                ResetBatchScope(handler);
            }
        }

        if (state.InBatch > 0)
        {
            await state.FinalizeWorkingSetAsync(handler, reader, config, probedAbsent, ct);
            yield return await state.BuildRemainingAsync(ct);
            ResetBatchScope(handler);
        }
    }

    public static async IAsyncEnumerable<SubstrateChange> RunMultiFileAsync<TRecord>(
    IMultiFileRecordStream<TRecord> stream,
    Func<string, IIngestRecordHandler<TRecord>> handlerFactory,
    Func<string, IngestBatchConfig> configFactory,
    long maxTotalUnits = 0,
    [EnumeratorCancellation] CancellationToken ct = default)
    {
        long unitsConsumed = 0;
        await using var e = stream.RecordsAsync(ct).GetAsyncEnumerator(ct);
        if (!await e.MoveNextAsync())
            yield break;

        while (true)
        {
            string label = e.Current.FileLabel;
            var handler = handlerFactory(label);
            var config = configFactory(label);
            bool streamEnded = false;

            async IAsyncEnumerable<TRecord> RecordsForFile(
                [EnumeratorCancellation] CancellationToken token)
            {
                yield return e.Current.Record;
                while (true)
                {
                    if (!await e.MoveNextAsync())
                    {
                        streamEnded = true;
                        yield break;
                    }
                    if (e.Current.FileLabel != label)
                        yield break;
                    yield return e.Current.Record;
                }
            }

            long fileCap = maxTotalUnits > 0 ? maxTotalUnits - unitsConsumed : 0;
            var runConfig = fileCap > 0 ? config.WithMaxInputUnits(fileCap) : config;
            bool hitCap = false;

            await foreach (var change in RunAsync(
                new AsyncEnumerableRecordStream<TRecord>(RecordsForFile(ct)), handler, runConfig, ct))
            {
                unitsConsumed += change.Metadata.InputUnitsConsumed;
                yield return change;
                if (maxTotalUnits > 0 && unitsConsumed >= maxTotalUnits)
                {
                    hitCap = true;
                    break;
                }
            }

            yield return BuildPeriodBoundary(config.SourceId, label);

            if (hitCap || streamEnded)
                yield break;
        }
    }

    private static void ResetBatchScope<TRecord>(IIngestRecordHandler<TRecord> handler)
    {
        if (handler is IIngestBatchScopedHandler scoped)
            scoped.ResetBatchState();
    }

    private static bool ShouldCloseWorkingSet(BatchState state, IngestBatchConfig config)
    {
        int recordCap = config.WorkingSetRecordCap
            ?? IngestSizing.ResolveWorkingSetRecordCap(IngestSourceProfile.Default);
        if (recordCap > 0 && state.InBatch >= recordCap)
            return true;

        long staged = state.Builder.StagedBytesEstimate;
        if (staged >= WorkingSetMode.BudgetBytes)
            return true;

        if (state.InBatch > 0)
        {
            var profile = config.WorkingSetProfile ?? IngestSourceProfile.Default;
            long est = IngestSizing.EstimateWorkingSetBytes(state.InBatch, staged, profile);
            if (est >= WorkingSetMode.BudgetBytes)
                return true;
        }

        return false;
    }

    private static async IAsyncEnumerable<SubstrateChange> FlushPending<TRecord>(
        List<TRecord> pending,
        IIngestRecordHandler<TRecord> handler,
        ISubstrateReader reader,
        BatchState state,
        IngestBatchConfig config,
        ISet<Hash128>? probedAbsent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (pending.Count == 0) yield break;

        var batch = pending.ToList();
        pending.Clear();

        if (config.WorkingSet)
        {
            var deferred = (WorkingSetDeferredBatch<TRecord>)(state.WorkingSetDeferred
                ??= new WorkingSetDeferredBatch<TRecord>());
            var composed = await IngestDescentFlush.ComposeBatchAsync(
                batch, handler, reader, state.Builder, probedAbsent, ct).ConfigureAwait(false);
            deferred.Shortcircuited.AddRange(composed.Shortcircuited);
            deferred.Pending.AddRange(composed.Pending);
            foreach (var (_, units) in composed.Shortcircuited)
                state.AddUnits(units);
            foreach (var (record, _) in composed.Pending)
                state.AddUnits(handler.UnitsPerRecord(record));
        }
        else
        {
            var drained = await IngestDescentFlush.ProbeAndDrainAsync(
                batch, handler, reader, state.Builder, config, probedAbsent, ct).ConfigureAwait(false);

            foreach (var (_, units) in drained)
            {
                state.AddUnits(units);
                if (state.InBatch >= config.BatchSize)
                {
                    yield return await state.YieldBatchAsync(ct);
                    state.ResetBuilder(config.NewBuilder(state.BatchNumber));
                }
            }
        }
    }

    private sealed class BatchState(SubstrateChangeBuilder builder, bool resetBankOnBuild = true)
    {
        public SubstrateChangeBuilder Builder { get; private set; } = builder;
        public int InBatch { get; private set; }
        public int BatchNumber { get; private set; }
        internal object? WorkingSetDeferred { get; set; }
        private long _rowsInBatch;

        public async Task FinalizeWorkingSetAsync<TRecord>(
            IIngestRecordHandler<TRecord> handler,
            ISubstrateReader reader,
            IngestBatchConfig config,
            ISet<Hash128>? probedAbsent,
            CancellationToken ct)
        {
            if (WorkingSetDeferred is not WorkingSetDeferredBatch<TRecord> deferred || !deferred.HasWork)
                return;
            await IngestDescentFlush.FinalizeWorkingSetAsync(
                deferred, handler, reader, Builder, config, probedAbsent, ct).ConfigureAwait(false);
            WorkingSetDeferred = null;
        }

        public void AddUnits(long units)
        {
            InBatch++;
            _rowsInBatch += units;
        }

        public void ResetBuilder(SubstrateChangeBuilder next) => Builder = next;

        public async Task<SubstrateChange> YieldBatchAsync(CancellationToken ct)
        {
            var change = await Builder.SetInputUnitsConsumed(_rowsInBatch).BuildAsync(ct);
            // Working-set mode keeps the content bank across builds: content
            // already witnessed by an earlier (committed-first) stage must
            // not re-stage in later ones. The runner resets it at the
            // working-set boundary, after the apply commits.
            if (resetBankOnBuild) IntentStage.ResetContentBank();
            InBatch = 0;
            _rowsInBatch = 0;
            BatchNumber++;
            return change;
        }

        public async Task<SubstrateChange> BuildRemainingAsync(CancellationToken ct)
        {
            var change = await Builder.SetInputUnitsConsumed(_rowsInBatch).BuildAsync(ct);
            if (resetBankOnBuild) IntentStage.ResetContentBank();
            return change;
        }
    }

    internal sealed class ListRecordStream<TRecord>(IReadOnlyList<TRecord> records) : IRecordStream<TRecord>
    {
        public async IAsyncEnumerable<TRecord> RecordsAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < records.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return records[i];
                await Task.Yield();
            }
        }
    }
}
