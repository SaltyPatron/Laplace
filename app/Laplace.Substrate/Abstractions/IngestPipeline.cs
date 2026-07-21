using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public interface IRecordStream<TRecord>
{
    IAsyncEnumerable<TRecord> RecordsAsync(CancellationToken ct = default);
}

/// <summary>
/// One file of a multi-file source as an independently-openable record source. Opening is
/// LAZY: <see cref="RecordsAsync"/> does the read+parse, and it runs inside the worker that
/// claims this source — never in the dispatcher. So the expensive parse is parallel across
/// files and no file is materialized into a list.
/// </summary>
public interface IFileRecordSource<TRecord>
{
    string FileLabel { get; }
    IAsyncEnumerable<TRecord> RecordsAsync(CancellationToken ct = default);
}

public interface IMultiFileRecordStream<TRecord>
{
    /// <summary>
    /// The source's files as independently-openable record sources. Enumeration is CHEAP — it
    /// yields file handles/specs and reads NOTHING; each worker opens and streams ONE source
    /// end-to-end (read + parse + compose + apply all in the worker). Files are order-independent
    /// (references resolve content-addressed), so there is no ordering contract on the sources.
    /// </summary>
    IAsyncEnumerable<IFileRecordSource<TRecord>> FilesAsync(CancellationToken ct = default);
}

/// <summary>A file source whose reader is a lazy factory — the common "I have a path, open it on demand" case.</summary>
public sealed class DelegateFileRecordSource<TRecord>(
    string fileLabel, Func<CancellationToken, IAsyncEnumerable<TRecord>> open) : IFileRecordSource<TRecord>
{
    public string FileLabel => fileLabel;
    public IAsyncEnumerable<TRecord> RecordsAsync(CancellationToken ct = default) => open(ct);
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

    /// <summary>Ingest file-progress marker (see IngestRunner.TrackIntent). The fold is inline
    /// per batch (ConsensusAccumulatingWriter → consensus_upsert) — this marker carries no fold
    /// semantics; the writer skips it as an empty change.</summary>
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

    /// <summary>
    /// Drives a multi-file source, PARALLEL BY DEFAULT. Up to <paramref name="fileWorkers"/> files are
    /// ingested concurrently across a bounded pool — each file its own handler/config/working-set/
    /// commit; the native compose is lock-free and each builder owns its intent stage; probes use
    /// pooled connections; changes merge into one stream the caller's single apply consumer drains
    /// serially, with channel backpressure. No phase/ordering concept: references resolve content-
    /// addressed (hash of the canonical key), so files are order-independent. A
    /// <paramref name="maxTotalUnits"/> cap forces the sequential path (it needs the exact cross-file
    /// stop point). One serial file at a time was the "files=0/201, 10 idle cores" gate.
    /// </summary>
    public static IAsyncEnumerable<SubstrateChange> RunMultiFileAsync<TRecord>(
        IMultiFileRecordStream<TRecord> stream,
        Func<string, IIngestRecordHandler<TRecord>> handlerFactory,
        Func<string, IngestBatchConfig> configFactory,
        long maxTotalUnits = 0,
        int fileWorkers = 0,
        CancellationToken ct = default)
    {
        int workers = maxTotalUnits > 0 ? 1 : Math.Max(1, fileWorkers);
        return workers <= 1
            ? RunMultiFileSequentialAsync(stream, handlerFactory, configFactory, maxTotalUnits, ct)
            : RunMultiFileParallelAsync(stream, handlerFactory, configFactory, workers, ct);
    }

    private static async IAsyncEnumerable<SubstrateChange> RunMultiFileSequentialAsync<TRecord>(
        IMultiFileRecordStream<TRecord> stream,
        Func<string, IIngestRecordHandler<TRecord>> handlerFactory,
        Func<string, IngestBatchConfig> configFactory,
        long maxTotalUnits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long unitsConsumed = 0;
        await foreach (var source in stream.FilesAsync(ct))
        {
            string label = source.FileLabel;
            var handler = handlerFactory(label);
            var config = configFactory(label);

            long fileCap = maxTotalUnits > 0 ? maxTotalUnits - unitsConsumed : 0;
            if (maxTotalUnits > 0 && fileCap <= 0)
                yield break;
            var runConfig = fileCap > 0 ? config.WithMaxInputUnits(fileCap) : config;
            bool hitCap = false;

            await foreach (var change in RunAsync(
                new AsyncEnumerableRecordStream<TRecord>(source.RecordsAsync(ct)), handler, runConfig, ct))
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

            if (hitCap)
                yield break;
        }
    }

    private static async IAsyncEnumerable<SubstrateChange> RunMultiFileParallelAsync<TRecord>(
        IMultiFileRecordStream<TRecord> stream,
        Func<string, IIngestRecordHandler<TRecord>> handlerFactory,
        Func<string, IngestBatchConfig> configFactory,
        int workers,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // The dispatcher enumerates FILE SOURCES — cheap handles, reading NOTHING — into a bounded
        // channel; N workers each claim one source and ingest it end-to-end: OPEN + read + parse +
        // compose + working-set + commit, all in the worker. The expensive parse is therefore
        // parallel across files, and no file is ever slammed into a list — records stream straight
        // from the file reader into the working-set spine. Files are order-independent (references
        // resolve content-addressed), so there is no phase/barrier — just the pool.
        var sources = Channel.CreateBounded<IFileRecordSource<TRecord>>(
            new BoundedChannelOptions(workers * 2)
            { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false });
        var outCh = Channel.CreateBounded<SubstrateChange>(
            new BoundedChannelOptions(workers * 4)
            { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = true });

        // FEW-BIG-FILES SOURCES SEGMENT INSIDE THE FILE (2026-07-21). One worker
        // per file is right when files outnumber workers, and badly wrong when
        // they don't: a source of two 400 MB files pinned two compose workers and
        // idled the other ten, with no intra-file parallelism anywhere because
        // DecomposerMultiFile seals the monolith path away.
        //
        // Peek at most workers+1 file sources (they are CHEAP handles that read
        // NOTHING). If the stream ends inside that peek, the whole source is
        // smaller than the pool, so each file is cut into record-aligned chunks
        // across MonolithSegmenter — the same machinery a single-file source
        // already uses — sized so total compose concurrency lands near the pool
        // width instead of the file count. Otherwise nothing changes.
        var peeked = new List<IFileRecordSource<TRecord>>(workers + 1);
        bool exhaustedInPeek = true;
        var fileEnumerator = stream.FilesAsync(ct).GetAsyncEnumerator(ct);
        while (peeked.Count <= workers)
        {
            if (!await fileEnumerator.MoveNextAsync())
                break;
            peeked.Add(fileEnumerator.Current);
            if (peeked.Count > workers) exhaustedInPeek = false;
        }

        int segmentsPerFile = 1;
        if (exhaustedInPeek && peeked.Count > 0)
            segmentsPerFile = Math.Max(1,
                Math.Max(1, IngestTopology.Current.ComposeWorkers) / peeked.Count);

        var dispatcher = Task.Run(async () =>
        {
            try
            {
                foreach (var source in peeked)
                    await sources.Writer.WriteAsync(source, ct);
                if (!exhaustedInPeek)
                    while (await fileEnumerator.MoveNextAsync())
                        await sources.Writer.WriteAsync(fileEnumerator.Current, ct);
                sources.Writer.Complete();
            }
            finally
            {
                await fileEnumerator.DisposeAsync();
            }
        }, ct);

        var workerTasks = new Task[workers];
        for (int w = 0; w < workers; w++)
            workerTasks[w] = Task.Run(async () =>
            {
                await foreach (var source in sources.Reader.ReadAllAsync(ct))
                {
                    var config = configFactory(source.FileLabel);
                    var records = new AsyncEnumerableRecordStream<TRecord>(source.RecordsAsync(ct));
                    int segments = segmentsPerFile > 1
                        ? MonolithSegmenter.ResolveSegments(config, segmentsPerFile)
                        : 1;

                    var changes = segments > 1
                        ? MonolithSegmenter.RunSegmentedAsync(
                            records,
                            _ => handlerFactory(source.FileLabel),
                            _ => configFactory(source.FileLabel),
                            segments,
                            MonolithSegmenter.ResolveChunkRecords(config),
                            source.FileLabel,
                            ct)
                        : RunAsync(records, handlerFactory(source.FileLabel), config, ct);

                    await foreach (var change in changes)
                        await outCh.Writer.WriteAsync(change, ct);
                    await outCh.Writer.WriteAsync(BuildPeriodBoundary(config.SourceId, source.FileLabel), ct);
                }
            }, ct);

        _ = Task.Run(async () =>
        {
            try { await dispatcher; await Task.WhenAll(workerTasks); outCh.Writer.Complete(); }
            catch (Exception ex) { outCh.Writer.Complete(ex); }
        }, ct);

        await foreach (var change in outCh.Reader.ReadAllAsync(ct))
            yield return change;
    }

    private static void ResetBatchScope<TRecord>(IIngestRecordHandler<TRecord> handler)
    {
        if (handler is IIngestBatchScopedHandler scoped)
            scoped.ResetBatchState();
    }

    private static bool ShouldCloseWorkingSet(BatchState state, IngestBatchConfig config)
    {
        var profile = config.WorkingSetProfile ?? IngestSourceProfile.Default;

        // Close the compose set at the small COMPOSE FLUSH ENVELOPE, not the large apply
        // COPY budget (WorkingSetMode.BudgetBytes ~ RAM/16, up to 4 GiB). Holding a set open
        // until the apply budget fills accumulates millions of deferred tier trees plus the
        // live content bank and collapses compose throughput (MEASURED 30k -> 1.8k rec/s as
        // a ~4 GiB set filled with ~3M records before flushing). The envelope (RAM/64,
        // <= 512 MiB) closes the set continuously in small memory-bounded batches so resident
        // memory stays flat and compose stays fast. It never explodes round-trips: the runner
        // re-coalesces these bounded changes back up to the apply budget before COPY, and the
        // content bank is preserved across compose closes (reset only after the apply).
        long envelope = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes();

        int recordCap = Math.Min(
            config.WorkingSetRecordCap ?? int.MaxValue,
            IngestSizing.ResolveFlushEnvelopeRecordCap(profile, envelope));
        if (recordCap > 0 && state.InBatch >= recordCap)
            return true;

        long staged = state.Builder.StagedBytesEstimate;
        if (staged >= envelope)
            return true;

        if (state.InBatch > 0)
        {
            long est = IngestSizing.EstimateWorkingSetBytes(state.InBatch, staged, profile);
            if (est >= envelope)
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
