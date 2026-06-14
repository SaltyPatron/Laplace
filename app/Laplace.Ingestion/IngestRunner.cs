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

        if (!options.SkipSourceCompletion
            && await _reader.HasSourceCompletedAsync(decomposer.SourceId, decomposer.LayerOrder, ct))
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

        // Record-once is scoped to this ingest: clear the content witness's bank of
        // already-emitted ids so a node shared across this source's terms/sentences is
        // recorded once and referenced thereafter (the content-addressed / Merkle-DAG law).
        Laplace.Engine.Core.IntentStage.ResetContentBank();

        log.LogInformation(
            "INGEST_PATH source={Source} ecosystem_path={Path} exists={Exists}",
            decomposer.SourceName, ctx.EcosystemPath, Directory.Exists(ctx.EcosystemPath));

        var inventory = await ResolveInventoryAsync(decomposer, ctx, options, ct);
        _obs.OnRunStart(decomposer.SourceName, decomposer.LayerOrder, inventory);
        log.LogInformation(
            "INGEST_START source={Source} layer={Layer} unit_type={UnitType} input_units={InputUnits} files={Files}",
            decomposer.SourceName, decomposer.LayerOrder,
            inventory?.UnitType ?? "units", inventory?.TotalInputUnits ?? 0, inventory?.FileCount ?? 0);

        var rng = new Random(unchecked((int)decomposer.SourceId.Lo));
        var counters = new RunCounters
        {
            Sw = sw,
            SourceName = decomposer.SourceName,
            LayerOrder = decomposer.LayerOrder,
            Inventory = inventory,
        };

        int batchSize  = Math.Max(1, options.BatchSize);
        int commitRows = Math.Max(0, options.CommitRows);

        // Content-witness rows live in the native ContentStage (c.IntentStages), NOT in
        // c.Entities/Physicalities — counting only the C# rows reported ~0 for a pass that
        // emits 100% through the witness, so the row-based flush (CommitRows) and the
        // producer backpressure (rowBudget) never fired: ALL of WordNet pass-1 accreted into
        // one 677,880-entity commit held wholly in RAM, and a single staged content entity
        // was transiently dropped from that oversized commit (the data-36 def ghost). Count
        // the prebuilt-stage rows so commits stay bounded (~CommitRows) and memory is capped.
        static int RowsOf(SubstrateChange c)
        {
            int rows = c.Entities.Length + c.Physicalities.Length + c.Attestations.Length;
            if (!c.IntentStages.IsDefaultOrEmpty)
                foreach (var s in c.IntentStages)
                    rows += s.EntityCount + s.PhysicalityCount + s.AttestationCount;
            return rows;
        }
        bool ShouldFlush(int intents, int rows) =>
            commitRows > 0
                ? (rows >= commitRows || intents >= batchSize)
                : intents >= batchSize;

        // Default StrictSerial: the record-once content witness emits each distinct node ONCE,
        // in the earliest batch that contains it, and LATER batches reference it (via trajectory /
        // attestation object_id). That is a cross-batch dependency — the emitting batch must commit
        // before the referencing one. Unordered/EpochBarrier parallel commit can run the referencing
        // batch first → referential failure (PropBank: batch-3 referenced `causative agent` emitted by
        // batch-0, committed out of order). Serial commit preserves producer order, so refs resolve.
        // A decomposer whose intents are genuinely self-contained may still opt into parallel.
        var commitPolicy = decomposer is IIngestCommitPolicy cp
            ? cp.CommitParallelism
            : IngestCommitParallelism.StrictSerial;

        if (options.ParallelWorkers > 1 && commitPolicy == IngestCommitParallelism.StrictSerial)
        {
            log.LogInformation(
                "{Source}: LAPLACE_INGEST_WORKERS={Workers} but commit policy is StrictSerial — "
                + "using pipelined serial commit (decompose overlaps commit); raise epoch barriers "
                + "or implement IIngestCommitPolicy.EpochBarrier to parallelize DB commits.",
                decomposer.SourceName, options.ParallelWorkers);
        }

        if (options.ParallelWorkers <= 1
            || commitPolicy == IngestCommitParallelism.StrictSerial)
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
        else if (commitPolicy == IngestCommitParallelism.Unordered)
        {
            await RunUnorderedParallelAsync(
                decomposer, ctx, options, batchSize, commitRows, ShouldFlush, RowsOf,
                counters, failures, log, rng, ct);
        }
        else
        {
            await RunEpochBarrierParallelAsync(
                decomposer, ctx, options, batchSize, commitRows, ShouldFlush, RowsOf,
                counters, failures, log, rng, ct);
        }

        unitsAttempted        = counters.UnitsAttempted;
        unitsApplied          = counters.UnitsApplied;
        unitsFailed           = counters.UnitsFailed;
        entitiesInserted      = counters.EntitiesInserted;
        physicalitiesInserted = counters.PhysicalitiesInserted;
        attestationsInserted  = counters.AttestationsInserted;
        totalRoundTrips       = counters.RoundTrips;

        if (!options.SkipSourceCompletion
            && counters.UnitsFailed == 0
            && failures.Count == 0
            && counters.UnitsApplied > 0)
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
        log.LogInformation(
            "INGEST_COMPLETE source={Source} layer={Layer} input_done={InputDone} input_total={InputTotal} "
            + "files_done={FilesDone} files_total={FilesTotal} intents={Applied}/{Produced} "
            + "rows_new={Ent}e+{Phys}p+{Att}a elapsed_s={Elapsed:F1} failed={Failed} status={Status}",
            decomposer.SourceName, decomposer.LayerOrder,
            counters.InputUnitsDone, inventory?.TotalInputUnits ?? 0,
            counters.FilesDone, inventory?.FileCount ?? 0,
            result.UnitsApplied, result.UnitsAttempted,
            result.EntitiesInserted, result.PhysicalitiesInserted, result.AttestationsInserted,
            result.WallClock.TotalSeconds, result.UnitsFailed,
            result.UnitsFailed > 0 ? "failed" : "ok");
        _obs.OnRunFinished(decomposer.SourceName, result);
        return result;
    }

    private async Task RunUnorderedParallelAsync(
        IDecomposer decomposer,
        InternalContext ctx,
        IngestRunOptions options,
        int batchSize,
        int commitRows,
        Func<int, int, bool> shouldFlush,
        Func<SubstrateChange, int> rowsOf,
        RunCounters counters,
        List<IngestFailure> failures,
        ILogger log,
        Random rng,
        CancellationToken ct)
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
                        batchRows += rowsOf(intent);
                        if (shouldFlush(batch.Count, batchRows))
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

    private async Task RunEpochBarrierParallelAsync(
        IDecomposer decomposer,
        InternalContext ctx,
        IngestRunOptions options,
        int batchSize,
        int commitRows,
        Func<int, int, bool> shouldFlush,
        Func<SubstrateChange, int> rowsOf,
        RunCounters counters,
        List<IngestFailure> failures,
        ILogger log,
        Random rng,
        CancellationToken ct)
    {
        var channel = Channel.CreateBounded<SubstrateChange>(
            new BoundedChannelOptions(options.ParallelWorkers * batchSize * 8)
            {
                SingleWriter = true,
                SingleReader = true,
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

        var epochBuffer = new List<SubstrateChange>(batchSize * options.ParallelWorkers);
        int currentEpoch = 0;
        long bufferedRows = 0;
        // An epoch is an UNORDERED parallel set — the barrier exists only BETWEEN epochs.
        // Flushing an epoch in bounded waves therefore preserves the fence law while capping
        // memory. Without this, a model-deposit epoch (~1.1B rows) buffered entirely in RAM
        // before the first apply (observed: zero INGEST_BATCH lines while RSS climbed to 30+ GB;
        // the prior run's "hang" was this buffer, not the ETL).
        long flushRows = Math.Max((long)Math.Max(commitRows, 1), 500_000L)
                         * Math.Max(1, options.ParallelWorkers);

        while (await channel.Reader.WaitToReadAsync(ct))
        {
            while (channel.Reader.TryRead(out var intent))
            {
                int intentEpoch = intent.Metadata.CommitEpoch;
                if (intentEpoch < currentEpoch)
                {
                    throw new InvalidOperationException(
                        $"Intent {intent.Metadata.SourceContentUnitName} commit epoch {intentEpoch} "
                        + $"is behind current epoch {currentEpoch}; epochs must be non-decreasing.");
                }
                if (intentEpoch > currentEpoch)
                {
                    await FlushEpochParallelAsync(
                        epochBuffer, decomposer, options, batchSize, commitRows,
                        shouldFlush, rowsOf, counters, failures, log, rng, ct);
                    epochBuffer.Clear();
                    bufferedRows = 0;
                    currentEpoch = intentEpoch;
                }
                epochBuffer.Add(intent);
                bufferedRows += rowsOf(intent);
                if (bufferedRows >= flushRows)
                {
                    await FlushEpochParallelAsync(
                        epochBuffer, decomposer, options, batchSize, commitRows,
                        shouldFlush, rowsOf, counters, failures, log, rng, ct);
                    epochBuffer.Clear();
                    bufferedRows = 0;
                }
            }
        }

        await FlushEpochParallelAsync(
            epochBuffer, decomposer, options, batchSize, commitRows,
            shouldFlush, rowsOf, counters, failures, log, rng, ct);
        await producer;
    }

    private async Task FlushEpochParallelAsync(
        List<SubstrateChange> intents,
        IDecomposer decomposer,
        IngestRunOptions options,
        int batchSize,
        int commitRows,
        Func<int, int, bool> shouldFlush,
        Func<SubstrateChange, int> rowsOf,
        RunCounters counters,
        List<IngestFailure> failures,
        ILogger log,
        Random rng,
        CancellationToken ct)
    {
        if (intents.Count == 0) return;

        int workers = Math.Min(options.ParallelWorkers, intents.Count);
        if (workers <= 1)
        {
            var batch = new List<SubstrateChange>(batchSize);
            int batchRows = 0;
            foreach (var intent in intents)
            {
                if (batchSize == 1 && commitRows == 0)
                {
                    await ProcessOneIntentAsync(intent, decomposer, options, rng,
                                                counters, failures, log, ct);
                    continue;
                }
                batch.Add(intent);
                batchRows += rowsOf(intent);
                if (shouldFlush(batch.Count, batchRows))
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
            return;
        }

        int chunkSize = Math.Max(1, (intents.Count + workers - 1) / workers);
        var tasks = new List<Task>(workers);
        for (int w = 0; w < workers; w++)
        {
            int start = w * chunkSize;
            if (start >= intents.Count) break;
            int end = Math.Min(start + chunkSize, intents.Count);
            int workerId = w;
            tasks.Add(Task.Run(async () =>
            {
                var localRng = new Random(unchecked((int)decomposer.SourceId.Lo) ^ workerId);
                var batch = new List<SubstrateChange>(batchSize);
                int batchRows = 0;
                for (int i = start; i < end; i++)
                {
                    var intent = intents[i];
                    if (batchSize == 1 && commitRows == 0)
                    {
                        await ProcessOneIntentAsync(intent, decomposer, options,
                                                    localRng, counters, failures, log, ct);
                        continue;
                    }
                    batch.Add(intent);
                    batchRows += rowsOf(intent);
                    if (shouldFlush(batch.Count, batchRows))
                    {
                        await ProcessBatchAsync(batch, decomposer, options,
                                                localRng, counters, failures, log, ct);
                        batch.Clear();
                        batchRows = 0;
                    }
                }
                if (batch.Count > 0)
                    await ProcessBatchAsync(batch, decomposer, options,
                                            localRng, counters, failures, log, ct);
            }, ct));
        }
        await Task.WhenAll(tasks);
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

                TrackIntent(counters, intent);

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
                foreach (var intent in batch)
                    TrackIntent(counters, intent);

                log.LogInformation(
                    "INGEST_BATCH source={Source} intents={Intents} rows={Rows} "
                    + "rows_new={Ent}e+{Phys}p+{Att}a elapsed_ms={Ms:N0} rate_rows_s={Rps:N0} round_trips={RT}",
                    decomposer.SourceName, batch.Count, batchRows, apply.EntitiesInserted,
                    apply.PhysicalitiesInserted, apply.AttestationsInserted,
                    apply.WallClock.TotalMilliseconds, batchRows / secs, apply.RoundTrips);

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

    private static async Task<IngestInventory?> ResolveInventoryAsync(
        IDecomposer decomposer,
        IDecomposerContext ctx,
        IngestRunOptions options,
        CancellationToken ct)
    {
        if (decomposer is IIngestInventoryProvider provider)
        {
            var inv = await provider.DescribeInputAsync(ctx, options.DecomposerOptions, ct);
            if (inv is not null) return inv;
        }
        long? est = await decomposer.EstimateUnitCountAsync(ctx, ct);
        return est is long n ? IngestInventory.Single(n) : null;
    }

    private static void TrackIntent(RunCounters c, SubstrateChange intent)
    {
        string unit = intent.Metadata.SourceContentUnitName;
        const string periodBoundary = "period-boundary/";
        if (unit.StartsWith(periodBoundary, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref c._filesDone);
            c._currentFile = unit[periodBoundary.Length..];
            return;
        }
        if (unit.StartsWith("layer-complete/", StringComparison.Ordinal)) return;

        long consumed = intent.Metadata.InputUnitsConsumed;
        if (consumed > 0)
            Interlocked.Add(ref c._inputUnitsDone, consumed);
        else
            c._currentFile = unit;
    }

    private static IngestProgress MakeProgress(RunCounters c)
    {
        var inv = c.Inventory;
        return new(
            c.SourceName ?? "",
            c.LayerOrder,
            c.UnitsAttempted,
            c.UnitsApplied,
            c.UnitsFailed,
            inv?.TotalInputUnits ?? 0,
            c.InputUnitsDone,
            inv?.FileCount ?? 0,
            c.FilesDone,
            c.CurrentFile,
            inv?.UnitType ?? "units",
            c.Sw?.Elapsed ?? TimeSpan.Zero,
            c.EntitiesInserted,
            c.PhysicalitiesInserted,
            c.AttestationsInserted,
            c.RoundTrips,
            c.UnitsProduced);
    }

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
        internal long _inputUnitsDone;
        internal int _filesDone;
        internal string? _currentFile;
        internal Stopwatch? Sw;
        internal string? SourceName;
        internal int LayerOrder;
        internal IngestInventory? Inventory;
        public long InputUnitsDone => Interlocked.Read(ref _inputUnitsDone);
        public int FilesDone => Volatile.Read(ref _filesDone);
        public string? CurrentFile => Volatile.Read(ref _currentFile);
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
