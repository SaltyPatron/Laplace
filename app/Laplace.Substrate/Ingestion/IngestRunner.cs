using System.Collections.Immutable;
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
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _obs = observability ?? NoOpObservability.Instance;
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

        NativeRuntimeEnv.ApplyFromTopologyIfUnset();
        IngestTopology.EnsureReady();
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

        int batchSize = Math.Max(1, options.BatchSize);
        int commitRows = Math.Max(0, options.CommitRows);
        var topo = IngestTopology.Current;
        var sizing = IngestSizing.Resolve(
            topo.PerformanceCoreCount,
            topo.FileWorkers,
            topo.ApplyPartitions,
            recordBatchOverride: batchSize,
            commitRowsOverride: commitRows > 0 ? commitRows : null);
        int maxIntentsPerCommit = commitRows > 0
            ? sizing.MaxIntentsPerCommit
            : batchSize;
        static int RowsOf(SubstrateChange c)
        {
            int rows = c.Entities.Length + c.Physicalities.Length + c.Attestations.Length;
            if (!c.IntentStages.IsDefaultOrEmpty)
                foreach (var s in c.IntentStages)
                    rows += s.EntityCount + s.PhysicalityCount + s.AttestationCount;
            return rows;
        }
        static long BytesOf(SubstrateChange c)
        {
            long bytes = ((long)c.Entities.Length + c.Physicalities.Length + c.Attestations.Length) * 152;
            // Trajectory payloads dwarf the fixed tuple estimate (a factor
            // deposit is tens-to-hundreds of MB in one row); count them or the
            // byte gates never fire and the working set buffers the whole run.
            foreach (var p in c.Physicalities)
                if (p.TrajectoryXyzm is { Length: > 0 } t) bytes += (long)t.Length * 8;
            if (!c.IntentStages.IsDefaultOrEmpty)
                foreach (var s in c.IntentStages)
                    if (!s.IsInvalid) bytes += s.TotalTupleBytes;
            return bytes;
        }

        // Rule #8: the working set is the unit of write. Yielded changes
        // accumulate until the memory budget closes the set with ONE
        // journaled apply; batch/commit-row caps only govern the retired
        // per-batch lane (LAPLACE_WORKING_SET=0).
        bool workingSet = Laplace.Decomposers.Abstractions.WorkingSetMode.Enabled;
        long wsBytes = 0;
        bool ShouldFlush(int intents, int rows) =>
            commitRows > 0
                ? (rows >= commitRows || intents >= batchSize)
                : intents >= batchSize;

        // COMMIT GRANULARITY (2026-07-21). The apply gate used to be the 4 GiB
        // COPY-buffer CEILING alone, so a source whose whole output is smaller
        // than that composed the ENTIRE run into RAM and wrote once, at the end:
        // OMW showed composed=1.6M / committed=0 / files=0/1226 / round_trips=0
        // for its whole run, then one terminal COPY with compose stalled behind
        // it. The ceiling is a memory SAFETY bound, not a batching policy —
        // using it as the batch size is what globbed every source.
        //
        // Commit at the same granularity compose already closes at (the flush
        // envelope, RAM/64 <= 512 MiB), and at every file boundary. Same total
        // COPY volume, same O(partitions) round-trips per apply, ~8x more
        // applies of 1/8 the size: the loader stays busy, files=n/N advances
        // live, and a cancelled run keeps every committed file instead of
        // losing the whole source.
        long applyEnvelope = Math.Min(
            IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(),
            Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes);

        // A file boundary is a commit OPPORTUNITY, not a commit requirement
        // (2026-07-21). Flushing on EVERY boundary shreds a many-small-files
        // source: OMW's 1226 files each yielded one working-set change plus one
        // boundary, so every apply was "intents=2 rows=~1,200" paying 10-12 round
        // trips and running at 1.5-9k rows/s, against 23,498 rows/s for the one
        // apply in that run that actually reached the envelope
        // (intents=3 rows=90,426, 29 round trips). It also produced
        // "intents=1 rows=0" applies — a full apply cycle for a lone boundary
        // carrying nothing.
        //
        // So a boundary commits only once the batch is worth a COPY. Below the
        // floor it rides along and commits with the next group, which still
        // advances files=n/N live (in steps of several files) and still bounds
        // what a cancelled run loses. Per-FILE visibility does not depend on
        // this: INGEST_FILE_COMPOSED/COMMITTED name every file individually.
        long boundaryCommitFloor = applyEnvelope / 8;

        static bool IsPeriodBoundary(SubstrateChange c) =>
            c.Metadata.SourceContentUnitName.StartsWith(
                IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal);

        bool ShouldFlushWithCap(int intents, int rows) =>
            workingSet
                ? wsBytes >= applyEnvelope
                : ShouldFlush(intents, rows) || intents >= maxIntentsPerCommit;





        bool syncIngest = false;

        // The RUN is the index-cycle scope. Rebuilding an index scans the
        // whole live table, so per-apply cycling costs
        // O(applies × table size); bracketing the run drops once at the
        // first qualifying apply and rebuilds exactly once, in the finally
        // below. A crash before the finally is covered by the writer's
        // index-cycle journal (recovered at the next run's begin).
        await _writer.BeginBulkRunAsync(ct);
        try
        {
            if (syncIngest)
            {
                CpuTopology.RequirePerformanceCorePin();




                var sbatch = new List<SubstrateChange>(batchSize);
                int sbatchRows = 0;
                await foreach (var intent in decomposer
                    .DecomposeAsync(ctx, options.DecomposerOptions, ct).WithCancellation(ct))
                {
                    Interlocked.Increment(ref counters._unitsProduced);
                    long units = intent.Metadata.InputUnitsConsumed;
                    if (units > 0) Interlocked.Add(ref counters._inputUnitsComposed, units);
                    options.Progress?.Report(MakeProgress(counters));
                    if (!workingSet && batchSize == 1 && commitRows == 0)
                    {
                        await ProcessOneIntentAsync(intent, decomposer, options, rng,
                                                    counters, failures, log, ct);
                        continue;
                    }
                    long sib = BytesOf(intent);
                    // Flush BEFORE adding an intent that would push the accumulated COPY
                    // bytes past the budget, so a single apply never exceeds it. Adding then
                    // checking (below) let the crossing intent land first, so one apply could
                    // reach ~2× budget and build a single-table buffer near the 2 GiB wall.
                    if (workingSet && sbatch.Count > 0
                        && wsBytes + sib > Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes)
                    {
                        await ProcessBatchAsync(sbatch, decomposer, options, rng,
                                                counters, failures, log, workingSet, ct);
                        sbatch.Clear();
                        sbatchRows = 0;
                        wsBytes = 0;
                    }
                    sbatch.Add(intent);
                    sbatchRows += RowsOf(intent);
                    wsBytes += sib;
                    if (ShouldFlushWithCap(sbatch.Count, sbatchRows)
                        || (IsPeriodBoundary(intent) && wsBytes >= boundaryCommitFloor))
                    {
                        await ProcessBatchAsync(sbatch, decomposer, options, rng,
                                                counters, failures, log, workingSet, ct);
                        sbatch.Clear();
                        sbatchRows = 0;
                        wsBytes = 0;
                    }
                }
                if (sbatch.Count > 0)
                    await ProcessBatchAsync(sbatch, decomposer, options, rng,
                                            counters, failures, log, workingSet, ct);
            }
            else
            {

                int channelCap = sizing.DecomposeChannelCapacity;
                long rowBudget = sizing.RowBudget;
                long bufferedRows = 0;
                // Compose-ahead is bounded by BYTES as well as rows: with huge
                // trajectory rows the 58-intent channel alone can hold tens of
                // GB, so the row budget never constrains anything.
                long byteBudget = Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes;
                long bufferedBytes = 0;
                var drained = new SemaphoreSlim(0, channelCap);

                var channel = Channel.CreateBounded<SubstrateChange>(
                    new BoundedChannelOptions(channelCap)
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        FullMode = BoundedChannelFullMode.Wait,
                    });

                var producer = CpuTopology.RunOnPinnedThread(async ct =>
                {
                    try
                    {
                        await foreach (var intent in decomposer
                            .DecomposeAsync(ctx, options.DecomposerOptions, ct).WithCancellation(ct))
                        {
                            Interlocked.Increment(ref counters._unitsProduced);
                            long units = intent.Metadata.InputUnitsConsumed;
                            if (units > 0) Interlocked.Add(ref counters._inputUnitsComposed, units);
                            options.Progress?.Report(MakeProgress(counters));
                            int r = RowsOf(intent);
                            long b = BytesOf(intent);
                            while ((Interlocked.Read(ref bufferedRows) + r > rowBudget
                                    || Interlocked.Read(ref bufferedBytes) + b > byteBudget)
                                   && Volatile.Read(ref bufferedRows) > 0)
                            {
                                await drained.WaitAsync(ct);
                            }
                            Interlocked.Add(ref bufferedRows, r);
                            Interlocked.Add(ref bufferedBytes, b);
                            await channel.Writer.WriteAsync(intent, ct);
                        }
                        channel.Writer.TryComplete();
                    }
                    catch (Exception ex)
                    {
                        channel.Writer.TryComplete(ex);
                    }
                }, "ingest-decompose-pcore", ct);

                var batch = new List<SubstrateChange>(batchSize);
                int batchRows = 0;
                while (await channel.Reader.WaitToReadAsync(ct))
                {
                    while (channel.Reader.TryRead(out var intent))
                    {
                        ct.ThrowIfCancellationRequested();
                        Interlocked.Add(ref bufferedRows, -RowsOf(intent));
                        Interlocked.Add(ref bufferedBytes, -BytesOf(intent));
                        try { drained.Release(); } catch (SemaphoreFullException) { }

                        if (!workingSet && batchSize == 1 && commitRows == 0)
                        {
                            await ProcessOneIntentAsync(intent, decomposer, options, rng,
                                                         counters, failures, log, ct);
                            continue;
                        }
                        long ib = BytesOf(intent);
                        // Flush BEFORE adding an intent that would push accumulated COPY bytes
                        // past the budget, so a single working-set apply never exceeds it and
                        // no single-table buffer approaches the 2 GiB int wall.
                        if (workingSet && batch.Count > 0
                            && wsBytes + ib > Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes)
                        {
                            await ProcessBatchAsync(batch, decomposer, options, rng,
                                                    counters, failures, log, workingSet, ct);
                            batch.Clear();
                            batchRows = 0;
                            wsBytes = 0;
                        }
                        batch.Add(intent);
                        batchRows += RowsOf(intent);
                        wsBytes += ib;
                        if (ShouldFlushWithCap(batch.Count, batchRows)
                            || (IsPeriodBoundary(intent) && wsBytes >= boundaryCommitFloor))
                        {
                            await ProcessBatchAsync(batch, decomposer, options, rng,
                                                    counters, failures, log, workingSet, ct);
                            batch.Clear();
                            batchRows = 0;
                            wsBytes = 0;
                        }
                    }
                }
                if (batch.Count > 0)
                    await ProcessBatchAsync(batch, decomposer, options, rng,
                                            counters, failures, log, workingSet, ct);


                await producer;
            }
        }
        finally
        {
            // Rebuild on every exit path, including failures — a fatal
            // apply error must not leave the table index-less. The one
            // exception is cancellation: the user is tearing the process
            // down, so don't block exit on a minutes-scale rebuild — the
            // journal recovers the drops at the next run's begin.
            try
            {
                await _writer.CompleteBulkRunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                log.LogWarning(
                    "bulk-run index rebuild skipped (cancelled) — journaled "
                    + "drops will be recovered at the next run's begin");
            }
        }

        unitsAttempted = counters.UnitsAttempted;
        unitsApplied = counters.UnitsApplied;
        unitsFailed = counters.UnitsFailed;
        entitiesInserted = counters.EntitiesInserted;
        physicalitiesInserted = counters.PhysicalitiesInserted;
        attestationsInserted = counters.AttestationsInserted;
        totalRoundTrips = counters.RoundTrips;

        if (!options.SkipSourceCompletion
            && counters.UnitsFailed == 0
            && failures.Count == 0
            && counters.UnitsApplied > 0
            && options.DecomposerOptions.MaxInputUnits <= 0)
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





        long declaredInput = inventory?.TotalInputUnits ?? 0;
        long declaredFiles = inventory?.FileCount ?? 0;
        bool emptySourceNoOp = result.UnitsApplied == 0 && (declaredInput > 0 || declaredFiles > 0);
        string status = result.UnitsFailed > 0 ? "failed" : (emptySourceNoOp ? "empty-noop" : "ok");
        log.LogInformation(
            "INGEST_COMPLETE source={Source} layer={Layer} input_done={InputDone} input_total={InputTotal} "
            + "files_done={FilesDone} files_total={FilesTotal} intents={Applied}/{Produced} "
            + "rows_new={Ent}e+{Phys}p+{Att}a elapsed_s={Elapsed:F1} failed={Failed} status={Status} "
            + "synset_hit_cum={SynHit} synset_miss_cum={SynMiss} lang_miss_cum={LangMiss}",
            decomposer.SourceName, decomposer.LayerOrder,
            counters.InputUnitsDone, declaredInput,
            counters.FilesDone, declaredFiles,
            result.UnitsApplied, result.UnitsAttempted,
            result.EntitiesInserted, result.PhysicalitiesInserted, result.AttestationsInserted,
            result.WallClock.TotalSeconds, result.UnitsFailed, status,
            SourceEntityIdConventions.SynsetHits, SourceEntityIdConventions.SynsetMisses,
            LanguageReference.ResolveMisses);
        _obs.OnRunFinished(decomposer.SourceName, result);
        if (emptySourceNoOp)
            throw new InvalidOperationException(
                $"{decomposer.SourceName}: source declares {declaredInput} input unit(s) / {declaredFiles} file(s) "
                + "but ingested 0 — grammar/format mismatch (silent no-op). Failing instead of reporting success. "
                + "Check the decomposer's modality/grammar matches the actual file format.");
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
        if (intent.CountsAsUnit) Interlocked.Increment(ref counters._unitsAttempted);

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

                if (intent.CountsAsUnit) Interlocked.Increment(ref counters._unitsApplied);
                Interlocked.Add(ref counters._entitiesInserted, apply.EntitiesInserted);
                Interlocked.Add(ref counters._physicalitiesInserted, apply.PhysicalitiesInserted);
                Interlocked.Add(ref counters._attestationsInserted, apply.AttestationsInserted);
                Interlocked.Add(ref counters._roundTrips, apply.RoundTrips);

                TrackIntent(counters, intent);

                _obs.OnIntentApplied(decomposer.SourceName, apply);
                options.Progress?.Report(MakeProgress(counters));
                Laplace.Engine.Core.IntentStage.ResetContentBank();
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
                    if (intent.CountsAsUnit) Interlocked.Increment(ref counters._unitsFailed);
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
        bool workingSet,
        CancellationToken ct)
    {
        if (batch.Count == 0) return;




        int unitCount = 0;
        foreach (var c in batch) if (c.CountsAsUnit) unitCount++;
        Interlocked.Add(ref counters._unitsAttempted, unitCount);

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
                var apply = workingSet
                    ? await _writer.ApplyWorkingSetAsync(batch, ct)
                    : await _writer.ApplyManyAsync(batch, ct);

                Interlocked.Add(ref counters._unitsApplied, unitCount);
                Interlocked.Add(ref counters._entitiesInserted, apply.EntitiesInserted);
                Interlocked.Add(ref counters._physicalitiesInserted, apply.PhysicalitiesInserted);
                Interlocked.Add(ref counters._attestationsInserted, apply.AttestationsInserted);
                Interlocked.Add(ref counters._roundTrips, apply.RoundTrips);

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
                Laplace.Engine.Core.IntentStage.ResetContentBank();
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
                    log.LogError(ex,
                        "Fatal ingest error in batch of {Count} intents "
                        + "(first unit {FirstUnit}, last unit {LastUnit}, "
                        + "~{Rows} staged rows, ~{Atts} managed attestations); aborting run.",
                        batch.Count,
                        batch[0].Metadata.SourceContentUnitName,
                        batch[^1].Metadata.SourceContentUnitName,
                        BatchRowEstimate(batch),
                        BatchManagedAttestations(batch));
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

    private static long BatchManagedAttestations(IReadOnlyList<SubstrateChange> batch)
    {
        long total = 0;
        for (int i = 0; i < batch.Count; i++)
            total += batch[i].Attestations.Length;
        return total;
    }

    private static long BatchRowEstimate(IReadOnlyList<SubstrateChange> batch)
    {
        long total = 0;
        for (int i = 0; i < batch.Count; i++)
        {
            var c = batch[i];
            total += c.Entities.Length + c.Physicalities.Length + c.Attestations.Length;
            if (!c.IntentStages.IsDefaultOrEmpty)
            {
                foreach (var s in c.IntentStages)
                {
                    if (s.IsInvalid) continue;
                    total += s.EntityCount + s.PhysicalityCount + s.AttestationCount;
                }
            }
        }
        return total;
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
        int failedUnits = 0;
        foreach (var c in batch) if (c.CountsAsUnit) failedUnits++;
        Interlocked.Add(ref counters._unitsFailed, failedUnits);
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
        long cap = options.DecomposerOptions.MaxInputUnits;
        if (decomposer is IIngestInventoryProvider provider)
        {
            var inv = await provider.DescribeInputAsync(ctx, options.DecomposerOptions, ct);
            if (inv is not null) return ApplyInputCap(inv, cap);
        }
        if (cap > 0)
            return IngestInventory.Single(cap, "records");
        long? est = await decomposer.EstimateUnitCountAsync(ctx, ct);
        return est is long n ? IngestInventory.Single(n) : null;
    }

    private static IngestInventory ApplyInputCap(IngestInventory inv, long cap) =>
        cap > 0 && inv.TotalInputUnits > cap
            ? inv with { TotalInputUnits = cap }
            : inv;

    private static void TrackIntent(RunCounters c, SubstrateChange intent)
    {
        string unit = intent.Metadata.SourceContentUnitName;
        const string periodBoundary = IngestBatchPipeline.PeriodBoundaryUnitPrefix;
        if (unit.StartsWith(periodBoundary, StringComparison.Ordinal))
        {
            int done = Interlocked.Increment(ref c._filesDone);
            string file = unit[periodBoundary.Length..];
            c._currentFile = file;
            // A file's boundary intent APPLYING is the moment that file is durable.
            // Emitted per file, not just counted, so the log names what landed and
            // when — "files=37/1226" tells you nothing about which file is slow.
            Console.Error.WriteLine(
                $"INGEST_FILE_COMMITTED source={c.SourceName} file={file} "
                + $"files={done}/{c.Inventory?.FileCount ?? 0} "
                + $"run_elapsed_s={c.Sw?.Elapsed.TotalSeconds ?? 0:F0}");
            return;
        }
        if (unit.StartsWith("layer-complete/", StringComparison.Ordinal)) return;

        long consumed = intent.Metadata.InputUnitsConsumed;
        if (consumed > 0 && intent.CountsAsUnit)
            Interlocked.Add(ref c._inputUnitsDone, consumed);
        else if (consumed == 0)
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
            c.UnitsProduced,
            c.InputUnitsComposed);
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
        internal long _inputUnitsComposed;
        internal int _filesDone;
        internal string? _currentFile;
        internal Stopwatch? Sw;
        internal string? SourceName;
        internal int LayerOrder;
        internal IngestInventory? Inventory;
        public long InputUnitsDone => Interlocked.Read(ref _inputUnitsDone);
        public long InputUnitsComposed => Interlocked.Read(ref _inputUnitsComposed);
        public int FilesDone => Volatile.Read(ref _filesDone);
        public string? CurrentFile => Volatile.Read(ref _currentFile);
        public long UnitsAttempted => Interlocked.Read(ref _unitsAttempted);
        public long UnitsProduced => Interlocked.Read(ref _unitsProduced);
        public long UnitsApplied => Interlocked.Read(ref _unitsApplied);
        public long UnitsFailed => Interlocked.Read(ref _unitsFailed);
        public long EntitiesInserted => Interlocked.Read(ref _entitiesInserted);
        public long PhysicalitiesInserted => Interlocked.Read(ref _physicalitiesInserted);
        public long AttestationsInserted => Interlocked.Read(ref _attestationsInserted);
        public long RoundTrips => Interlocked.Read(ref _roundTrips);
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
