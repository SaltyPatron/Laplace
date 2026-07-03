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

public sealed class IngestBatchConfig
{
    public required Hash128 SourceId { get; init; }
    public required string BatchLabelPrefix { get; init; }
    public int BatchSize { get; init; } = 256;
    public int ProbeChunkSize { get; init; } = 1024;
    public double WitnessWeight { get; init; } = 1.0;

    /// <summary>
    /// Rule #8 working-set mode (06_Engineering_Ruleset.txt). One builder
    /// spans the entire record stream: descent probes still run per
    /// ProbeChunkSize interval (each distinct id probed at most once per
    /// working set — proven-present via the reader's cache, probed-absent
    /// via a working-set cache), but NOTHING is yielded mid-stream; the
    /// pipeline emits exactly one SubstrateChange at the end, carrying the
    /// whole novel frontier (stage witness-dedup makes cross-interval
    /// duplicate emission a no-op). BatchSize is ignored in this mode.
    /// </summary>
    public bool WorkingSet { get; init; }

    /// <summary>Records per descent interval in working-set mode; tests pin
    /// this — production uses WorkingSetMode.ProbeIntervalRecords.</summary>
    public int? WorkingSetProbeInterval { get; init; }
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
        };
}

public static class IngestBatchPipeline
{
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

        // Working-set mode: descent runs at LARGE accumulation intervals
        // (probe round trips scale with distinct ids / interval, never rows);
        // one builder spans the stream; the only mid-stream yield is the
        // memory-budget valve, which closes one working set and opens the
        // next through the same path. probedAbsent spans this run only.
        int probeInterval = config.WorkingSet
            ? Math.Max(config.ProbeChunkSize,
                config.WorkingSetProbeInterval ?? WorkingSetMode.ProbeIntervalRecords)
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
                    yield return change;
                if (state.InBatch > 0)
                    yield return await state.BuildRemainingAsync(ct);
                yield break;
            }

            pending.Add(record);
            if (pending.Count >= probeInterval)
            {
                await foreach (var change in FlushPending(pending, handler, reader, state, config, probedAbsent, ct))
                    yield return change;

                if (config.WorkingSet && state.InBatch > 0
                    && state.Builder.StagedBytesEstimate >= WorkingSetMode.BudgetBytes)
                {
                    yield return await state.YieldBatchAsync(ct);
                    state.ResetBuilder(config.NewBuilder(state.BatchNumber));
                }
            }
            unitsConsumed += units;

            reportUnits?.Invoke(rowsTotal);

            if (!config.WorkingSet && state.InBatch >= config.BatchSize)
            {
                yield return await state.YieldBatchAsync(ct);
                state.ResetBuilder(config.NewBuilder(state.BatchNumber));
            }
        }

        if (pending.Count > 0)
        {
            await foreach (var change in FlushPending(pending, handler, reader, state, config, probedAbsent, ct))
                yield return change;
        }

        if (state.InBatch > 0)
            yield return await state.BuildRemainingAsync(ct);
    }

    public static async IAsyncEnumerable<SubstrateChange> RunMultiFileAsync<TRecord>(
    IMultiFileRecordStream<TRecord> stream,
    Func<string, IIngestRecordHandler<TRecord>> handlerFactory,
    Func<string, IngestBatchConfig> configFactory,
    long maxTotalUnits = 0,
    [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? currentLabel = null;
        IIngestRecordHandler<TRecord>? handler = null;
        IngestBatchConfig? config = null;
        var buffer = new List<TRecord>();
        long unitsConsumed = 0;

        async IAsyncEnumerable<SubstrateChange> FlushBuffer()
        {
            if (buffer.Count == 0 || handler is null || config is null)
                yield break;
            long fileCap = maxTotalUnits > 0 ? maxTotalUnits - unitsConsumed : 0;
            var runConfig = fileCap > 0 ? config.WithMaxInputUnits(fileCap) : config;
            await foreach (var change in RunAsync(new ListRecordStream<TRecord>(buffer), handler, runConfig, ct))
            {
                unitsConsumed += change.Metadata.InputUnitsConsumed;
                yield return change;
                if (maxTotalUnits > 0 && unitsConsumed >= maxTotalUnits)
                    yield break;
            }
            buffer.Clear();
        }

        await foreach (var (label, record) in stream.RecordsAsync(ct))
        {
            if (maxTotalUnits > 0 && unitsConsumed >= maxTotalUnits)
                yield break;

            if (label != currentLabel)
            {
                await foreach (var change in FlushBuffer())
                    yield return change;
                if (maxTotalUnits > 0 && unitsConsumed >= maxTotalUnits)
                    yield break;

                currentLabel = label;
                handler = handlerFactory(label);
                config = configFactory(label);
            }
            buffer.Add(record);
        }

        await foreach (var change in FlushBuffer())
            yield return change;
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

        var drained = await IngestDescentFlush.ProbeAndDrainAsync(
            batch, handler, reader, state.Builder, config, probedAbsent, ct).ConfigureAwait(false);

        foreach (var (_, units) in drained)
        {
            state.AddUnits(units);
            if (!config.WorkingSet && state.InBatch >= config.BatchSize)
            {
                yield return await state.YieldBatchAsync(ct);
                state.ResetBuilder(config.NewBuilder(state.BatchNumber));
            }
        }
    }

    private sealed class BatchState(SubstrateChangeBuilder builder, bool resetBankOnBuild = true)
    {
        public SubstrateChangeBuilder Builder { get; private set; } = builder;
        public int InBatch { get; private set; }
        public int BatchNumber { get; private set; }
        private long _rowsInBatch;

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
