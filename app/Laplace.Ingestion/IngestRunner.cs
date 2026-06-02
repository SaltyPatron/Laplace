using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// The shared orchestration loop per ADR 0052 — composes
/// <see cref="IDecomposer"/> + <see cref="ISubstrateWriter"/> into the
/// canonical per-source ingest recipe. Every per-source decomposer
/// (Unicode, ISO, WordNet, ...) ingests through this same RunAsync.
///
/// <para>
/// Cross-cutting concerns owned here: layer-ordering enforcement (ADR
/// 0037), transient retry, parallel-worker variant, progress reporting,
/// structured logging, observability emission. Idempotency is the
/// substrate's own property — content-addressed identity + the writer's
/// existence-check + INSERT … ON CONFLICT DO NOTHING (RULES R5) — so a
/// re-run converges with no side journal and no resume state to keep coherent.
/// </para>
/// </summary>
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

    /// <summary>Run a decomposer end-to-end per ADR 0052.</summary>
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

        // 1. Layer-ordering prerequisite check (per ADR 0037).
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

        // 2. Build context + bootstrap.
        var ctx = new InternalContext(
            EcosystemPath: ResolveEcosystemPath(decomposer, options),
            Writer: _writer,
            Reader: _reader,
            Logger: _loggerFactory.CreateLogger($"Decomposer:{decomposer.SourceName}"),
            SubstrateVersion: "v1");

        await decomposer.InitializeAsync(ctx, ct);

        // 3. Estimate total units (best-effort).
        long? estimatedTotal = await decomposer.EstimateUnitCountAsync(ctx, ct);
        _obs.OnRunStart(decomposer.SourceName, decomposer.LayerOrder, estimatedTotal);

        // 4. Iterate the decomposer's stream. Serial or parallel.
        var rng = new Random(unchecked((int)decomposer.SourceId.Lo));
        var counters = new RunCounters { Sw = sw, EstimatedTotal = estimatedTotal };

        int batchSize  = Math.Max(1, options.BatchSize);
        int commitRows = Math.Max(0, options.CommitRows);

        // Flush trigger. When CommitRows is set, the COPY payload is pinned by row
        // count (intent fan-out varies wildly — one QK intent ≈ thousands of rows);
        // BatchSize still caps buffered intents so a single huge intent can't
        // overshoot unbounded. Otherwise fall back to pure intent-count batching.
        static int RowsOf(SubstrateChange c) =>
            c.Entities.Length + c.Physicalities.Length + c.Attestations.Length;
        bool ShouldFlush(int intents, int rows) =>
            commitRows > 0
                ? (rows >= commitRows || intents >= batchSize)
                : intents >= batchSize;

        if (options.ParallelWorkers <= 1)
        {
            var batch = new List<SubstrateChange>(batchSize);
            int batchRows = 0;
            await foreach (var intent in decomposer.DecomposeAsync(ctx, options.DecomposerOptions, ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();
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
            if (batch.Count > 0)
                await ProcessBatchAsync(batch, decomposer, options, rng,
                                        counters, failures, log, ct);
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

            // Producer: read from decomposer, push into channel
            var producer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var intent in decomposer.DecomposeAsync(ctx, options.DecomposerOptions, ct)
                                                            .WithCancellation(ct))
                    {
                        await channel.Writer.WriteAsync(intent, ct);
                    }
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, ct);

            // Consumers: dequeue + apply concurrently. Each worker fills and
            // flushes its OWN batch so N workers issue N concurrent batched
            // COPYs rather than N concurrent single-row applies.
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

        // 5. Layer completion marker (ADR 0037) when the run finished clean.
        if (counters.UnitsFailed == 0 && failures.Count == 0)
            await _writer.ApplyAsync(LayerCompletion.BuildMarker(decomposer), ct);

        // 6. Summary.
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
                    // Fatal — record failure, surface, abort run.
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
                // Transient — loop will retry
                log.LogWarning(ex, "Transient ingest error on intent {IntentId} (attempt {Attempt}); will retry.",
                    intent.Metadata.IntentId, attempt + 1);
            }
        }

        // All retries exhausted
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

    /// <summary>
    /// Apply a coalesced batch of intents through
    /// <see cref="ISubstrateWriter.ApplyManyAsync"/> — one existence pass and
    /// one COPY per table for the whole batch. The batch commits atomically; on
    /// a fatal error the whole batch rolls back (recorded + aborts, matching
    /// per-intent fatal semantics). Re-application is idempotent via
    /// content-addressed identity + INSERT … ON CONFLICT DO NOTHING.
    /// </summary>
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

                // Per-batch observability: where the time actually goes (write vs decompose).
                // Slow batches ⇒ write-bound (existence/COPY/INSERT); fast batches with gaps
                // between them ⇒ decompose-bound. Uses what ApplyManyAsync already returns.
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

        // All retries exhausted for the batch.
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

    // EcosystemPath is the decomposer's read-only source-data root (e.g.
    // /vault/Data/Unicode). Decomposers that need it set it explicitly via
    // options.EcosystemPath; the fallback is the process working directory —
    // never a shared /tmp path.
    private static string ResolveEcosystemPath(IDecomposer decomposer, IngestRunOptions options)
        => options.EcosystemPath ?? Directory.GetCurrentDirectory();

    private static IngestProgress MakeProgress(RunCounters c) =>
        new(c.UnitsAttempted, c.UnitsApplied, c.UnitsFailed,
            c.EstimatedTotal, c.Sw?.Elapsed ?? TimeSpan.Zero,
            c.EntitiesInserted, c.PhysicalitiesInserted, c.AttestationsInserted);

    private sealed class RunCounters
    {
        internal long _unitsAttempted;
        internal long _unitsApplied;
        internal long _unitsFailed;
        internal long _entitiesInserted;
        internal long _physicalitiesInserted;
        internal long _attestationsInserted;
        internal long _roundTrips;
        // Run clock + estimate so MakeProgress reports rows/s + % without threading
        // these through the per-intent/per-batch apply methods.
        internal Stopwatch? Sw;
        internal long? EstimatedTotal;
        public long UnitsAttempted        => Interlocked.Read(ref _unitsAttempted);
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

/// <summary>Thrown when <see cref="IngestRunner.RunAsync"/> is invoked
/// for a decomposer whose Layer N requires Layer 0..N-1 completion, but
/// some prerequisite layer hasn't completed.</summary>
public sealed class LayerOrderingViolationException : Exception
{
    public int DecomposerLayer { get; }
    public int MissingLayer { get; }
    public LayerOrderingViolationException(int decomposerLayer, int missingLayer)
        : base($"Layer {decomposerLayer} decomposer requires Layer {missingLayer} "
             + "to have completed at least once per ADR 0037.")
    {
        DecomposerLayer = decomposerLayer;
        MissingLayer = missingLayer;
    }
}
