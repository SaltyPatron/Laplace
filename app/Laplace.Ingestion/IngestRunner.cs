using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

public sealed class IngestRunner
{
    private readonly ISubstrateWriter _writer;
    private readonly ISubstrateReader _reader;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IIngestObservability _obs;

    public IngestRunner(
        ISubstrateWriter writer,
        ISubstrateReader reader,
        ILoggerFactory? loggerFactory = null,
        IIngestObservability? observability = null)
    {
        _writer        = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader        = reader ?? throw new ArgumentNullException(nameof(reader));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _obs           = observability ?? NoOpObservability.Instance;
    }

    public async Task<IngestRunResult> RunAsync(
        IDecomposer decomposer,
        IngestRunOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decomposer);
        ArgumentNullException.ThrowIfNull(options);

        var log = _loggerFactory.CreateLogger($"Ingest:{decomposer.SourceName}");
        var sw = Stopwatch.StartNew();
        var failures = new List<IngestFailure>();
        long unitsAttempted = 0, unitsApplied = 0, unitsFailed = 0;
        long entitiesInserted = 0, physicalitiesInserted = 0, attestationsInserted = 0;
        long totalRoundTrips = 0;

        if (!options.SkipLayerOrderingCheck)
        {
            for (int layer = 0; layer < decomposer.LayerOrder; layer++)
            {
                if (!await _reader.HasSourceEverCompletedAsync(layer, ct))
                {
                    throw new LayerOrderingViolationException(decomposer.LayerOrder, layer);
                }
            }
        }

        if (await _reader.HasSourceCompletedAsync(decomposer.SourceId, decomposer.LayerOrder, ct))
        {
            log.LogInformation(
                "{Source}: already ingested (completion marker present) — short-circuiting; "
                + "a re-ingest would double-count testimony into consensus. "
                + "To re-run: per-source eviction first.",
                decomposer.SourceName);
            sw.Stop();
            return new IngestRunResult(
                decomposer.SourceId, decomposer.SourceName,
                UnitsAttempted: 0, UnitsApplied: 0, UnitsFailed: 0,
                EntitiesInserted: 0, PhysicalitiesInserted: 0, AttestationsInserted: 0,
                TotalRoundTrips: 0, WallClock: sw.Elapsed,
                Failures: Array.Empty<IngestFailure>());
        }

        var ctx = new InternalContext(
            EcosystemPath: ResolveEcosystemPath(decomposer, options),
            Writer: _writer,
            Reader: _reader,
            Logger: _loggerFactory.CreateLogger($"Decomposer:{decomposer.SourceName}"),
            SubstrateVersion: "v1");

        await decomposer.InitializeAsync(ctx, ct);

        long? estimatedTotal = await decomposer.EstimateUnitCountAsync(ctx, ct);
        _obs.OnRunStart(decomposer.SourceName, decomposer.LayerOrder, estimatedTotal);

        var rng = new Random(unchecked((int)decomposer.SourceId.Lo));
        var counters = new RunCounters { Sw = sw, EstimatedTotal = estimatedTotal };

        int batchSize  = Math.Max(1, options.BatchSize);
        int commitRows = Math.Max(0, options.CommitRows);

        static int RowsOf(SubstrateChange c) =>
            c.Entities.Length + c.Physicalities.Length + c.Attestations.Length;
        bool ShouldFlush(int intents, int rows) =>
            commitRows > 0
                ? (rows >= commitRows || intents >= batchSize)
                : intents >= batchSize;

        if (options.ParallelWorkers <= 1)
        {
            // Pipelined serial path: a single producer task decomposes continuously
            // into an unbounded channel while THIS thread (the single consumer) batches
            // and commits in the exact order produced. One consumer preserves the same
            // strict commit ordering as a fully serial loop, so the cross-batch
            // referential dependency that breaks parallel workers is unaffected — but
            // decompose (CPU) now overlaps with the per-batch DB commit (I/O) instead of
            // the two stages alternating on one thread.
            //
            // Backpressure is row-based, not intent-based: intents vary enormously in row
            // count (UD ~40k rows/intent vs. a 1-row gloss), so an intent-count bound
            // would either starve overlap for big intents or balloon memory for them.
            // The producer pauses once the buffered row count exceeds the budget and is
            // woken as the consumer drains.
            long rowBudget = Math.Max((long)commitRows, batchSize) * 3L;
            long bufferedRows = 0;
            var drained = new SemaphoreSlim(0, 1);

            var channel = Channel.CreateUnbounded<SubstrateChange>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

            var producer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var intent in decomposer
                        .DecomposeAsync(ctx, options.DecomposerOptions, ct).WithCancellation(ct))
                    {
                        Interlocked.Increment(ref counters._unitsProduced);
                        int r = RowsOf(intent);
                        while (Interlocked.Read(ref bufferedRows) + r > rowBudget
                               && Volatile.Read(ref bufferedRows) > 0)
                        {
                            await drained.WaitAsync(ct);
                        }
                        Interlocked.Add(ref bufferedRows, r);
                        await channel.Writer.WriteAsync(intent, ct);
                    }
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            }, ct);

            var batch = new List<SubstrateChange>(batchSize);
            int batchRows = 0;
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                while (channel.Reader.TryRead(out var intent))
                {
                    ct.ThrowIfCancellationRequested();
                    Interlocked.Add(ref bufferedRows, -RowsOf(intent));
                    try { drained.Release(); } catch (SemaphoreFullException) { }

                    if (batchSize == 1 && commitRows == 0)
                    {
                        await ProcessOneIntentAsync(intent, decomposer, options, rng,
                                                     counters, failures, log, ct);
                        continue;
                    }
                    batch.Add(intent);
                    batchRows += RowsOf(intent);
                    if (ShouldFlush(batch.Count, batchRows))
                    {
                        await ProcessBatchAsync(batch, decomposer, options, rng,
                                                counters, failures, log, ct);
                        batch.Clear();
                        batchRows = 0;
                    }
                }
            }
            if (batch.Count > 0)
                await ProcessBatchAsync(batch, decomposer, options, rng,
                                        counters, failures, log, ct);

            // Surface any decompose-side exception (channel completed with error).
            await producer;
        }
        else
        {
            var channel = Channel.CreateBounded<SubstrateChange>(
                new BoundedChannelOptions(options.ParallelWorkers * batchSize * 4)
                {
                    SingleWriter = true,
                    SingleReader = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });

            var producer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var intent in decomposer.DecomposeAsync(ctx, options.DecomposerOptions, ct)
                                                            .WithCancellation(ct))
                    {
                        Interlocked.Increment(ref counters._unitsProduced);
                        await channel.Writer.WriteAsync(intent, ct);
                    }
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, ct);

            var consumers = new Task[options.ParallelWorkers];
            for (int w = 0; w < options.ParallelWorkers; w++)
            {
                consumers[w] = Task.Run(async () =>
                {
                    var localRng = new Random(unchecked((int)decomposer.SourceId.Lo) ^ Environment.CurrentManagedThreadId);
                    var batch = new List<SubstrateChange>(batchSize);
                    int batchRows = 0;
                    while (await channel.Reader.WaitToReadAsync(ct))
                    {
                        while (channel.Reader.TryRead(out var intent))
                        {
                            if (batchSize == 1 && commitRows == 0)
                            {
                                await ProcessOneIntentAsync(intent, decomposer, options,
                                                            localRng, counters, failures, log, ct);
                                continue;
                            }
                            batch.Add(intent);
                            batchRows += RowsOf(intent);
                            if (ShouldFlush(batch.Count, batchRows))
                            {
                                await ProcessBatchAsync(batch, decomposer, options,
                                                        localRng, counters, failures, log, ct);
                                batch.Clear();
                                batchRows = 0;
                            }
                        }
                    }
                    if (batch.Count > 0)
                        await ProcessBatchAsync(batch, decomposer, options,
                                                localRng, counters, failures, log, ct);
                }, ct);
            }
            await Task.WhenAll(producer);
            await Task.WhenAll(consumers);
        }

        unitsAttempted        = counters.UnitsAttempted;
        unitsApplied          = counters.UnitsApplied;
        unitsFailed           = counters.UnitsFailed;
        entitiesInserted      = counters.EntitiesInserted;
        physicalitiesInserted = counters.PhysicalitiesInserted;
        attestationsInserted  = counters.AttestationsInserted;
        totalRoundTrips       = counters.RoundTrips;

        if (counters.UnitsFailed == 0 && failures.Count == 0)
            await _writer.ApplyAsync(LayerCompletion.BuildMarker(decomposer), ct);

        sw.Stop();

        var result = new IngestRunResult(
            SourceId: decomposer.SourceId,
            SourceName: decomposer.SourceName,
            UnitsAttempted: unitsAttempted,
            UnitsApplied: unitsApplied,
            UnitsFailed: unitsFailed,
            EntitiesInserted: entitiesInserted,
            PhysicalitiesInserted: physicalitiesInserted,
            AttestationsInserted: attestationsInserted,
            TotalRoundTrips: totalRoundTrips,
            WallClock: sw.Elapsed,
            Failures: failures);
        _obs.OnRunFinished(decomposer.SourceName, result);
        return result;
    }

    private async Task ProcessOneIntentAsync(
        SubstrateChange intent,
        IDecomposer decomposer,
        IngestRunOptions options,
        Random rng,
        RunCounters counters,
        List<IngestFailure> failures,
        ILogger log,
        CancellationToken ct)
    {
        Interlocked.Increment(ref counters._unitsAttempted);

        Exception? lastEx = null;
        int attempt = 0;
        for (; attempt < options.RetryPolicy.MaxAttempts; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    var delay = options.RetryPolicy.DelayBeforeAttempt(attempt - 1, rng);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                }
                var apply = await _writer.ApplyAsync(intent, ct);

                Interlocked.Increment(ref counters._unitsApplied);
                Interlocked.Add(ref counters._entitiesInserted, apply.EntitiesInserted);
                Interlocked.Add(ref counters._physicalitiesInserted, apply.PhysicalitiesInserted);
                Interlocked.Add(ref counters._attestationsInserted, apply.AttestationsInserted);
                Interlocked.Add(ref counters._roundTrips, apply.RoundTrips);

                _obs.OnIntentApplied(decomposer.SourceName, apply);
                options.Progress?.Report(MakeProgress(counters));
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (!options.RetryPolicy.IsTransient(ex))
                {
                    var fatal = new IngestFailure(
                        intent.Metadata.IntentId,
                        intent.Metadata.SourceContentUnitName,
                        ex.GetType().FullName ?? "Exception",
                        ex.Message,
                        WasTransient: false,
                        RetryAttempts: attempt,
                        OccurredAt: DateTimeOffset.UtcNow);
                    lock (failures) failures.Add(fatal);
                    Interlocked.Increment(ref counters._unitsFailed);
                    _obs.OnIntentFailed(decomposer.SourceName, fatal);
                    log.LogError(ex, "Fatal ingest error on intent {IntentId} (unit {Unit}); aborting run.",
                        intent.Metadata.IntentId, intent.Metadata.SourceContentUnitName);
                    throw;
                }
                log.LogWarning(ex, "Transient ingest error on intent {IntentId} (attempt {Attempt}); will retry.",
                    intent.Metadata.IntentId, attempt + 1);
            }
        }

        var failure = new IngestFailure(
            intent.Metadata.IntentId,
            intent.Metadata.SourceContentUnitName,
            lastEx?.GetType().FullName ?? "TransientExhaustion",
            lastEx?.Message ?? "transient retry exhausted",
            WasTransient: true,
            RetryAttempts: attempt,
            OccurredAt: DateTimeOffset.UtcNow);
        lock (failures) failures.Add(failure);
        Interlocked.Increment(ref counters._unitsFailed);
        _obs.OnIntentFailed(decomposer.SourceName, failure);
        if (options.AbortOnTransientExhaustion && lastEx is not null) throw lastEx;
    }

    private async Task ProcessBatchAsync(
        List<SubstrateChange> batch,
        IDecomposer decomposer,
        IngestRunOptions options,
        Random rng,
        RunCounters counters,
        List<IngestFailure> failures,
        ILogger log,
        CancellationToken ct)
    {
        if (batch.Count == 0) return;

        Interlocked.Add(ref counters._unitsAttempted, batch.Count);

        Exception? lastEx = null;
        int attempt = 0;
        for (; attempt < options.RetryPolicy.MaxAttempts; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    var delay = options.RetryPolicy.DelayBeforeAttempt(attempt - 1, rng);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                }
                var apply = await _writer.ApplyManyAsync(batch, ct);

                Interlocked.Add(ref counters._unitsApplied,           batch.Count);
                Interlocked.Add(ref counters._entitiesInserted,       apply.EntitiesInserted);
                Interlocked.Add(ref counters._physicalitiesInserted,  apply.PhysicalitiesInserted);
                Interlocked.Add(ref counters._attestationsInserted,   apply.AttestationsInserted);
                Interlocked.Add(ref counters._roundTrips,             apply.RoundTrips);

                long batchRows = (long)apply.EntitiesAttempted + apply.PhysicalitiesAttempted + apply.AttestationsAttempted;
                double secs = Math.Max(1e-3, apply.WallClock.TotalSeconds);
                log.LogInformation(
                    "batch: {Intents} intents / {Rows} rows → {Ent}e+{Phys}p+{Att}a new in {Ms:N0}ms "
                    + "({Rps:N0} rows/s, {RT} round-trips)",
                    batch.Count, batchRows, apply.EntitiesInserted, apply.PhysicalitiesInserted,
                    apply.AttestationsInserted, apply.WallClock.TotalMilliseconds, batchRows / secs, apply.RoundTrips);

                _obs.OnIntentApplied(decomposer.SourceName, apply);
                options.Progress?.Report(MakeProgress(counters));
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (!options.RetryPolicy.IsTransient(ex))
                {
                    RecordBatchFailure(batch, decomposer.SourceName, ex,
                                       wasTransient: false, attempt, failures, counters);
                    log.LogError(ex, "Fatal ingest error in batch of {Count} intents "
                        + "(first unit {Unit}); aborting run.",
                        batch.Count, batch[0].Metadata.SourceContentUnitName);
                    throw;
                }
                log.LogWarning(ex, "Transient ingest error in batch of {Count} intents "
                    + "(attempt {Attempt}); will retry whole batch.",
                    batch.Count, attempt + 1);
            }
        }

        RecordBatchFailure(batch, decomposer.SourceName, lastEx,
                           wasTransient: true, attempt, failures, counters);
        if (options.AbortOnTransientExhaustion && lastEx is not null) throw lastEx;
    }

    private void RecordBatchFailure(
        List<SubstrateChange> batch,
        string sourceName,
        Exception? ex,
        bool wasTransient,
        int attempts,
        List<IngestFailure> failures,
        RunCounters counters)
    {
        var now = DateTimeOffset.UtcNow;
        var typeName = ex?.GetType().FullName ?? (wasTransient ? "TransientExhaustion" : "Exception");
        var msg = ex?.Message ?? "transient retry exhausted";
        var batchFailures = new IngestFailure[batch.Count];
        for (int i = 0; i < batch.Count; i++)
            batchFailures[i] = new IngestFailure(
                batch[i].Metadata.IntentId,
                batch[i].Metadata.SourceContentUnitName,
                typeName, msg, wasTransient, attempts, now);

        lock (failures) failures.AddRange(batchFailures);
        Interlocked.Add(ref counters._unitsFailed, batch.Count);
        foreach (var f in batchFailures)
            _obs.OnIntentFailed(sourceName, f);
    }

    private static string ResolveEcosystemPath(IDecomposer decomposer, IngestRunOptions options)
        => options.EcosystemPath ?? Directory.GetCurrentDirectory();

    private static IngestProgress MakeProgress(RunCounters c) =>
        new(c.UnitsAttempted, c.UnitsApplied, c.UnitsFailed,
            c.EstimatedTotal, c.Sw?.Elapsed ?? TimeSpan.Zero,
            c.EntitiesInserted, c.PhysicalitiesInserted, c.AttestationsInserted,
            c.RoundTrips, c.UnitsProduced);

    private sealed class RunCounters
    {
        internal long _unitsAttempted;
        internal long _unitsApplied;
        internal long _unitsFailed;
        internal long _entitiesInserted;
        internal long _physicalitiesInserted;
        internal long _attestationsInserted;
        internal long _roundTrips;
        internal long _unitsProduced;
        internal Stopwatch? Sw;
        internal long? EstimatedTotal;
        public long UnitsAttempted        => Interlocked.Read(ref _unitsAttempted);
        public long UnitsProduced         => Interlocked.Read(ref _unitsProduced);
        public long UnitsApplied          => Interlocked.Read(ref _unitsApplied);
        public long UnitsFailed           => Interlocked.Read(ref _unitsFailed);
        public long EntitiesInserted      => Interlocked.Read(ref _entitiesInserted);
        public long PhysicalitiesInserted => Interlocked.Read(ref _physicalitiesInserted);
        public long AttestationsInserted  => Interlocked.Read(ref _attestationsInserted);
        public long RoundTrips            => Interlocked.Read(ref _roundTrips);
    }

    private sealed record InternalContext(
        string EcosystemPath,
        ISubstrateWriter Writer,
        ISubstrateReader Reader,
        ILogger Logger,
        string SubstrateVersion) : IDecomposerContext;
}

public sealed class LayerOrderingViolationException : Exception
{
    public int DecomposerLayer { get; }
    public int MissingLayer { get; }
    public LayerOrderingViolationException(int decomposerLayer, int missingLayer)
        : base($"Layer {decomposerLayer} decomposer requires Layer {missingLayer} "
             + "to have completed at least once.")
    {
        DecomposerLayer = decomposerLayer;
        MissingLayer = missingLayer;
    }
}
