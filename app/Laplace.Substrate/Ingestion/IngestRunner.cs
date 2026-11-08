using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

public sealed partial class IngestRunner
{
    private async Task<IngestRunResult> RunCoreAsync(
        IDecomposer decomposer,
        IngestRunOptions options,
        CancellationToken ct)
    {
        var log = _loggerFactory.CreateLogger($"Ingest:{decomposer.SourceName}");
        var sw = Stopwatch.StartNew();
        var foldMetrics = _writer as IConsensusFoldMetrics;
        var deferredMetrics = _writer as IDeferredApplyMetrics;
        var deferredAtStart = deferredMetrics?.DeferredRowsInserted ?? default;
        long foldObservationsAtStart = foldMetrics?.ObservationsAccumulated ?? 0;
        long foldCellsAtStart = foldMetrics?.CellsFolded ?? 0;
        var failures = new List<IngestFailure>();
        long unitsAttempted = 0, unitsApplied = 0, unitsFailed = 0;
        long entitiesInserted = 0, physicalitiesInserted = 0, attestationsInserted = 0;
        long totalRoundTrips = 0;
        if (!options.SkipSourceCompletion
            && !options.BypassSourceCompletionGuard
            && !decomposer.PerFileCompletion
            && await _reader.HasSourceCompletedAsync(decomposer.SourceId, decomposer.LayerOrder, ct))
        {
            log.LogInformation(
                "{Source}: already ingested (completion marker present and physicality contract satisfied) — "
                + "short-circuiting; a re-ingest would double-count testimony into consensus.",
                decomposer.SourceName);
            _obs.OnRunSkipped(decomposer.SourceName, decomposer.SourceId, decomposer.LayerOrder);
            sw.Stop();
            return new IngestRunResult(
                decomposer.SourceId, decomposer.SourceName,
                UnitsAttempted: 0, UnitsApplied: 0, UnitsFailed: 0,
                EntitiesInserted: 0, PhysicalitiesInserted: 0, AttestationsInserted: 0,
                TotalRoundTrips: 0, WallClock: sw.Elapsed,
                Failures: Array.Empty<IngestFailure>());
        }
        var counters = new RunCounters
        {
            Sw = sw,
            SourceName = decomposer.SourceName,
            LayerOrder = decomposer.LayerOrder,
        };
        string ecosystemPath = ResolveEcosystemPath(decomposer, options);
        IngestArtifactGraph? artifactGraph = LoadArtifactGraph(
            ecosystemPath,
            options.RequireArtifactManifest,
            allowAmbientManifest: decomposer is not IIgnoresAmbientArtifactManifest);
        if (artifactGraph is null && decomposer is IIngestArtifactGraphProvider graphProvider)
            artifactGraph = await graphProvider.DescribeArtifactsAsync(
                ecosystemPath, options.DecomposerOptions, ct);
        var ctx = new InternalContext(
            EcosystemPath: ecosystemPath,
            SelectedArtifacts: artifactGraph?.Selected ?? Array.Empty<IngestArtifact>(),
            HasArtifactGraph: artifactGraph is not null,
            Writer: new InitializationAccountingWriter(_writer, counters),
            Reader: _reader,
            Logger: _loggerFactory.CreateLogger($"Decomposer:{decomposer.SourceName}"),
            SubstrateVersion: "v1");
        await using var bulkRun = await BulkRunLease.BeginAsync(_writer, ct).ConfigureAwait(false);
        // The closure this run checks is of the entities this run writes.
        (_writer as Laplace.SubstrateCRUD.Npgsql.IPhysicalityClosure)?.TakePhysicalityClosure();
        await decomposer.InitializeAsync(ctx, ct);
        NativeRuntimeEnv.ApplyFromTopologyIfUnset();
        IngestTopology.EnsureReady();
        bool pathIsDir = Directory.Exists(ctx.EcosystemPath);
        bool pathIsFile = File.Exists(ctx.EcosystemPath);
        log.LogInformation(
            "INGEST_PATH source={Source} ecosystem_path={Path} exists={Exists} kind={Kind}",
            decomposer.SourceName, ctx.EcosystemPath, pathIsDir || pathIsFile,
            pathIsDir ? "dir" : pathIsFile ? "file" : "missing");
        var inventory = await ResolveInventoryAsync(decomposer, ctx, artifactGraph, options, ct);
        _obs.OnRunStart(decomposer.SourceName, decomposer.SourceId, decomposer.LayerOrder, inventory, artifactGraph);
        using var obsScope = IngestObservabilityScope.Begin(_obs, decomposer.SourceName);
        log.LogInformation(
            "INGEST_START source={Source} layer={Layer} unit_type={UnitType} input_units={InputUnits} files={Files}",
            decomposer.SourceName, decomposer.LayerOrder,
            inventory?.UnitType ?? "units", inventory?.TotalInputUnits ?? 0, inventory?.FileCount ?? 0);
        var rng = new Random(unchecked((int)decomposer.SourceId.Lo));
        counters.Inventory = inventory;
        int batchSize = Math.Max(1, options.BatchSize);
        int commitRows = Math.Max(0, options.CommitRows);
        var topo = IngestTopology.Current;
        var sizing = IngestSizing.Resolve(
            topo.PerformanceCoreCount,
            topo.FileWorkers,
            topo.ApplyPartitions,
            recordBatchOverride: batchSize,
            commitRowsOverride: commitRows > 0 ? commitRows : null);
        int applyRowLimit = Math.Min(
            sizing.CommitRows,
            IngestSizing.ResolveApplyTransactionRows());
        int maxIntentsPerCommit = commitRows > 0
            ? sizing.MaxIntentsPerCommit
            : batchSize;
        bool workingSet = Laplace.Decomposers.Abstractions.WorkingSetMode.Enabled;
        long wsBytes = 0;
        bool ShouldFlush(int intents, long rows) =>
            commitRows > 0
                ? (rows >= Math.Min(commitRows, applyRowLimit) || intents >= batchSize)
                : rows >= applyRowLimit || intents >= batchSize;
        long applyEnvelope = Math.Min(
            IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(),
            Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes);
        var admissionWindow = new IngestAdmissionWindow(applyEnvelope);
        bool ShouldFlushWithCap(int intents, long rows) =>
            workingSet
                ? rows >= applyRowLimit
                    || ShouldFlushWorkingSet(wsBytes, applyEnvelope)
                    || admissionWindow.ModeledSourcePayloadBytes >= applyEnvelope
                : ShouldFlush(intents, rows) || intents >= maxIntentsPerCommit;
        void LogAdmissionWindow(int count)
        {
            if (workingSet)
                log.LogInformation(
                    "INGEST_ADMISSION_WINDOW source={Source} intents={Intents} physicalities={Physicalities} "
                    + "modeled_source_payload_bytes={Modeled} grant_bytes={Grant} over_bound_singleton={OverBound}",
                    decomposer.SourceName, count, admissionWindow.Sizing.Physicalities,
                    admissionWindow.ModeledSourcePayloadBytes,
                    applyEnvelope, count == 1 && admissionWindow.ModeledSourcePayloadBytes > applyEnvelope);
        }
        bool syncIngest = false;
        var runCt = ct;
        bool extractionSucceeded = false;
        Exception? extractionFailure = null;
        try
        {
            if (syncIngest)
            {
                CpuTopology.RequirePerformanceCorePin();
                var sbatch = new List<SubstrateChange>(batchSize);
                using var sbatchOwnership = new ApplyEnvelopeBatchOwner(sbatch);
                long sbatchRows = 0;
                Hash128? sbatchSource = null;
                await foreach (var intent in decomposer
                    .DecomposeAsync(ctx, options.DecomposerOptions, runCt).WithCancellation(runCt))
                {
                    using var transfer = new ApplyEnvelopeTransfer(intent);
                    Interlocked.Increment(ref counters._unitsProduced);
                    long units = intent.Metadata.InputUnitsConsumed;
                    if (units > 0) Interlocked.Add(ref counters._inputUnitsComposed, units);
                    options.Progress?.Report(MakeProgress(counters));
                    if (intent.ApplyBarrier is { } syncBarrier)
                    {
                        try
                        {
                            if (sbatch.Count > 0)
                            {
                                LogAdmissionWindow(sbatch.Count);
                                await ProcessOwnedBatchAsync(
                                    sbatch, decomposer, options, rng,
                                    counters, failures, log, workingSet, runCt);
                                sbatch.Clear();
                                sbatchRows = 0;
                                wsBytes = 0;
                                admissionWindow.Reset();
                                sbatchSource = null;
                            }
                            syncBarrier.Complete();
                        }
                        catch (Exception ex)
                        {
                            syncBarrier.Fail(ex);
                            throw;
                        }
                        continue;
                    }
                    if (!workingSet && batchSize == 1 && commitRows == 0)
                    {
                        transfer.Complete();
                        await ProcessOwnedIntentAsync(intent, decomposer, options, rng,
                                                    counters, failures, log, runCt);
                        continue;
                    }
                    long sib = BytesOf(intent);
                    var admission = workingSet
                        ? IngestAdmissionSizing.Measure(intent, sib, runCt)
                        : default;
                    if (workingSet && ShouldFlushWorkingSetSourceBoundary(
                            sbatchSource, intent.Metadata.SourceId))
                    {
                        LogAdmissionWindow(sbatch.Count);
                        await ProcessOwnedBatchAsync(sbatch, decomposer, options, rng,
                                                counters, failures, log, workingSet, runCt);
                        sbatch.Clear();
                        sbatchRows = 0;
                        wsBytes = 0;
                        admissionWindow.Reset();
                        sbatchSource = null;
                    }
                    if (workingSet && sbatch.Count > 0
                        && (sbatchRows + RowsOf(intent) > applyRowLimit
                            || wsBytes + sib > Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes
                            || admissionWindow.ShouldFlushBefore(admission)))
                    {
                        LogAdmissionWindow(sbatch.Count);
                        await ProcessOwnedBatchAsync(sbatch, decomposer, options, rng,
                                                counters, failures, log, workingSet, runCt);
                        sbatch.Clear();
                        sbatchRows = 0;
                        wsBytes = 0;
                        admissionWindow.Reset();
                        sbatchSource = null;
                    }
                    if (workingSet) admissionWindow.Add(admission);
                    sbatch.Add(intent);
                    transfer.Complete();
                    sbatchSource ??= intent.Metadata.SourceId;
                    sbatchRows += RowsOf(intent);
                    wsBytes += sib;
                    // A file boundary is a journal marker, not a transaction boundary: it stays
                    // in the same capacity-bounded batch as its file's data and neighboring
                    // files, applies atomically with that batch, and CompleteFileAsync closes
                    // the file only after the apply.
                    if (ShouldFlushWithCap(sbatch.Count, sbatchRows))
                    {
                        LogAdmissionWindow(sbatch.Count);
                        await ProcessOwnedBatchAsync(sbatch, decomposer, options, rng,
                                                counters, failures, log, workingSet, runCt);
                        sbatch.Clear();
                        sbatchRows = 0;
                        wsBytes = 0;
                        admissionWindow.Reset();
                        sbatchSource = null;
                    }
                }
                if (sbatch.Count > 0)
                {
                    LogAdmissionWindow(sbatch.Count);
                    await ProcessOwnedBatchAsync(sbatch, decomposer, options, rng,
                                            counters, failures, log, workingSet, runCt);
                }
            }
            else
            {
                int channelCap = sizing.DecomposeChannelCapacity;
                long rowBudget = sizing.RowBudget;
                long bufferedRows = 0;
                long byteBudget = Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes;
                long bufferedBytes = 0;
                using var drained = new SemaphoreSlim(0, channelCap);
                using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(runCt);
                var channel = Channel.CreateBounded<QueuedIntent>(
                    new BoundedChannelOptions(channelCap)
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        FullMode = BoundedChannelFullMode.Wait,
                    });
                var producer = CpuTopology.RunOnPinnedThread(async producerCt =>
                {
                    try
                    {
                        await foreach (var intent in decomposer
                            .DecomposeAsync(ctx, options.DecomposerOptions, producerCt)
                            .WithCancellation(producerCt))
                        {
                            using var transfer = new ApplyEnvelopeTransfer(intent);
                            Interlocked.Increment(ref counters._unitsProduced);
                            long units = intent.Metadata.InputUnitsConsumed;
                            if (units > 0) Interlocked.Add(ref counters._inputUnitsComposed, units);
                            options.Progress?.Report(MakeProgress(counters));
                            long r = RowsOf(intent);
                            long b = BytesOf(intent);
                            var admission = workingSet
                                ? IngestAdmissionSizing.Measure(intent, b, producerCt)
                                : default;
                            while ((Interlocked.Read(ref bufferedRows) + r > rowBudget
                                    || Interlocked.Read(ref bufferedBytes) + b > byteBudget)
                                   && Volatile.Read(ref bufferedRows) > 0)
                            {
                                await drained.WaitAsync(producerCt);
                            }
                            Interlocked.Add(ref bufferedRows, r);
                            Interlocked.Add(ref bufferedBytes, b);
                            await channel.Writer.WriteAsync(new QueuedIntent(intent, r, b, admission), producerCt);
                            transfer.Complete();
                        }
                        channel.Writer.TryComplete();
                    }
                    catch (Exception ex)
                    {
                        channel.Writer.TryComplete(ex);
                        // The consumer may already be failing in apply and never
                        // await the channel fault. Its cleanup joins this task,
                        // so retain the producer's cause on that owner as well.
                        throw;
                    }
                }, "ingest-decompose-pcore", pipelineCts.Token);
                // Apply batches accumulate per source, not per file, so the source's file
                // packaging does not set the transaction cadence. File labels stay on every
                // change for resume, observability and fold attribution.
                var buckets = new Dictionary<Hash128, ApplyBatchBucket>();
                ApplyBatchBucket BucketFor(SubstrateChange intent)
                {
                    Hash128 owner = intent.Metadata.SourceId;
                    if (!buckets.TryGetValue(owner, out var bucket))
                    {
                        bucket = new ApplyBatchBucket(batchSize, applyEnvelope);
                        buckets.Add(owner, bucket);
                    }
                    return bucket;
                }
                async Task FlushBucketAsync(ApplyBatchBucket bucket)
                {
                    if (bucket.Batch.Count == 0) return;
                    if (workingSet)
                        log.LogInformation(
                            "INGEST_ADMISSION_WINDOW source={Source} file={File} intents={Intents} "
                            + "physicalities={Physicalities} "
                            + "modeled_source_payload_bytes={Modeled} grant_bytes={Grant}",
                            decomposer.SourceName,
                            bucket.Batch[0].Metadata.FileLabel ?? "<source>",
                            bucket.Batch.Count,
                            bucket.AdmissionWindow.Sizing.Physicalities,
                            bucket.AdmissionWindow.ModeledSourcePayloadBytes,
                            applyEnvelope);
                    await ProcessOwnedBatchAsync(bucket.Batch, decomposer, options, rng,
                        counters, failures, log, workingSet, runCt).ConfigureAwait(false);
                    bucket.Reset();
                }
                await using var pipelineCleanup = new ApplyPipelineCleanup(
                    pipelineCts, producer, buckets, channel.Reader);
                try
                {
                    while (await channel.Reader.WaitToReadAsync(runCt))
                    {
                        while (channel.Reader.TryRead(out var queued))
                        {
                            var intent = queued.Intent;
                            using var transfer = new ApplyEnvelopeTransfer(intent);
                            runCt.ThrowIfCancellationRequested();
                            Interlocked.Add(ref bufferedRows, -queued.Rows);
                            Interlocked.Add(ref bufferedBytes, -queued.SerializedBytes);
                            try { drained.Release(); } catch (SemaphoreFullException) { }
                            if (intent.ApplyBarrier is { } applyBarrier)
                            {
                                try
                                {
                                    foreach (ApplyBatchBucket pendingBucket in buckets.Values.ToArray())
                                        await FlushBucketAsync(pendingBucket).ConfigureAwait(false);
                                    applyBarrier.Complete();
                                }
                                catch (Exception ex)
                                {
                                    applyBarrier.Fail(ex);
                                    throw;
                                }
                                continue;
                            }
                            if (!workingSet && batchSize == 1 && commitRows == 0)
                            {
                                transfer.Complete();
                                await ProcessOwnedIntentAsync(intent, decomposer, options, rng,
                                                             counters, failures, log, runCt);
                                continue;
                            }
                            var bucket = BucketFor(intent);
                            long ib = queued.SerializedBytes;
                            var admission = queued.Admission;
                            // A terminal marker batches like any small control row: it follows its
                            // file's data in the stream and may share a transaction with other
                            // files. CompleteFileAsync runs only after the atomic apply returns,
                            // so files_done never outruns durable rows.
                            if (workingSet && ShouldFlushWorkingSetSourceBoundary(
                                    bucket.Source, intent.Metadata.SourceId))
                                await FlushBucketAsync(bucket);
                            if (workingSet && bucket.Batch.Count > 0
                                && (bucket.Rows + queued.Rows > applyRowLimit
                                    || bucket.Bytes + ib > Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes
                                    || bucket.AdmissionWindow.ShouldFlushBefore(admission)))
                                await FlushBucketAsync(bucket);
                            if (workingSet) bucket.AdmissionWindow.Add(admission);
                            bucket.Batch.Add(intent);
                            transfer.Complete();
                            bucket.Source ??= intent.Metadata.SourceId;
                            bucket.Rows += queued.Rows;
                            bucket.Bytes += ib;
                            bool capacityReached = workingSet
                                ? bucket.Rows >= applyRowLimit
                                    || ShouldFlushWorkingSet(bucket.Bytes, applyEnvelope)
                                    || bucket.AdmissionWindow.ModeledSourcePayloadBytes >= applyEnvelope
                                : ShouldFlush(bucket.Batch.Count, bucket.Rows)
                                    || bucket.Batch.Count >= maxIntentsPerCommit;
                            if (capacityReached)
                                await FlushBucketAsync(bucket);
                        }
                    }
                    foreach (var bucket in buckets.Values.ToArray())
                        await FlushBucketAsync(bucket);
                    await producer;
                }
                catch (Exception failure)
                {
                    pipelineCleanup.ApplyFailure = failure;
                    throw;
                }
            }
            extractionSucceeded = failures.Count == 0 && counters.UnitsFailed == 0
                && options.DecomposerOptions.MaxInputUnits <= 0 && !ct.IsCancellationRequested;
        }
        catch (Exception failure) { extractionFailure = failure; throw; }
        finally
        {
            List<Exception>? completionFailures = null;
            try
            {
                if (extractionSucceeded)
                    await bulkRun.CompleteAsync(
                        phase => _obs.OnCompletionPhase(decomposer.SourceName, phase))
                        .ConfigureAwait(false);
                else
                    await bulkRun.AbortAsync().ConfigureAwait(false);
                if (deferredMetrics is not null)
                {
                    var committed = deferredMetrics.DeferredRowsInserted;
                    Interlocked.Add(ref counters._entitiesInserted, committed.Entities - deferredAtStart.Entities);
                    Interlocked.Add(ref counters._physicalitiesInserted, committed.Physicalities - deferredAtStart.Physicalities);
                    Interlocked.Add(ref counters._attestationsInserted, committed.Attestations - deferredAtStart.Attestations);
                }
            }
            catch (Exception failure) { (completionFailures ??= []).Add(failure); }
            try
            {
                if (foldMetrics is not null)
                {
                    _obs.OnBulkCompletion(
                        decomposer.SourceName,
                        foldMetrics.LastFoldDrainWallClock,
                        foldMetrics.LastWriterMaintenanceWallClock,
                        foldMetrics.LastFoldSpanWallClock,
                        foldMetrics.ConsensusUpsertBackendWallClock,
                        foldMetrics.HighwayMaskBackendWallClock,
                        foldMetrics.ConsensusUpsertCalls,
                        foldMetrics.HighwayMaskCalls,
                        foldMetrics.HighwayMaskPairs);
                    log.LogInformation(
                        "LAPSIGHT_COMPLETION source={Source} fold_drain_ms={FoldMs} "
                        + "writer_maintenance_ms={MaintenanceMs} fold_span_ms={FoldSpanMs} "
                        + "consensus_backend_ms={ConsensusMs} consensus_calls={ConsensusCalls} "
                        + "mask_backend_ms={MaskMs} mask_calls={MaskCalls} mask_pairs={MaskPairs}",
                        decomposer.SourceName,
                        foldMetrics.LastFoldDrainWallClock.TotalMilliseconds,
                        foldMetrics.LastWriterMaintenanceWallClock.TotalMilliseconds,
                        foldMetrics.LastFoldSpanWallClock.TotalMilliseconds,
                        foldMetrics.ConsensusUpsertBackendWallClock.TotalMilliseconds,
                        foldMetrics.ConsensusUpsertCalls,
                        foldMetrics.HighwayMaskBackendWallClock.TotalMilliseconds,
                        foldMetrics.HighwayMaskCalls,
                        foldMetrics.HighwayMaskPairs);
                }
            }
            catch (Exception failure) { (completionFailures ??= []).Add(failure); }
            // These observers own file durability, including when an apply or
            // bulk completion failed. Join them before releasing the run.
            try { await counters.DrainPendingFileCompletionsAsync().ConfigureAwait(false); }
            catch (Exception failure) { (completionFailures ??= []).Add(failure); }
            if (completionFailures is not null)
            {
                if (extractionFailure is not null) completionFailures.Insert(0, extractionFailure);
                Exception[] distinct = completionFailures.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
                if (distinct.Length == 1)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(distinct[0]).Throw();
                throw new AggregateException("Ingest extraction/completion failed.", distinct);
            }
        }
        // Cancellation can arrive during the non-cancellable durability/cleanup
        // join. It must still prevent a successful source completion receipt.
        ct.ThrowIfCancellationRequested();
        unitsAttempted = counters.UnitsAttempted;
        unitsApplied = counters.UnitsApplied;
        unitsFailed = counters.UnitsFailed;
        entitiesInserted = counters.EntitiesInserted;
        physicalitiesInserted = counters.PhysicalitiesInserted;
        attestationsInserted = counters.AttestationsInserted;
        totalRoundTrips = counters.RoundTrips;
        ReportStagedEntityAdmission(counters, log);
        long filesTotalForMarker = inventory?.FileCount ?? 0;
        bool filesComplete = filesTotalForMarker <= 0
            || counters.FilesDone == filesTotalForMarker;
        bool fullSuccessfulExtraction = options.DecomposerOptions.MaxInputUnits <= 0
            && counters.UnitsFailed == 0
            && failures.Count == 0
            && filesComplete;
        // A resumed run does not observe the input units of files skipped by their markers,
        // so when any file was reused the declared/previous inventory total is kept rather
        // than replaced by this run's partial count.
        if (fullSuccessfulExtraction
            && inventory is not null
            && counters.InputUnitsDone > 0
            && counters.FilesSkippedComplete == 0)
            inventory.PublishExactTotal(counters.InputUnitsDone);
        if (fullSuccessfulExtraction)
        {
            var (writtenEntities, unplacedEntities) =
                _writer is Laplace.SubstrateCRUD.Npgsql.IPhysicalityClosure closure
                    ? closure.TakePhysicalityClosure() : (0L, 0L);
            if (unplacedEntities != 0)
                throw new InvalidOperationException(
                    $"{decomposer.SourceName}: physicality closure failed after ingest: "
                    + $"{unplacedEntities} of {writtenEntities} written entity identities have no "
                    + "physicality. No completion marker will be written.");
            log.LogInformation(
                "INGEST_PHYSICALITY_COVERAGE source={Source} entities={Entities} missing=0 status=ok",
                decomposer.SourceName, writtenEntities);
        }
        // Files proven complete by their own markers count toward the layer: a run
        // that reuses every committed file still establishes the source's layer.
        if (!options.SkipSourceCompletion
            && fullSuccessfulExtraction
            && (counters.UnitsApplied > 0 || counters.FilesSkippedComplete > 0))
            await _writer.ApplyAsync(LayerCompletion.Build(decomposer), ct);
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
            Failures: failures,
            FilesDone: counters.FilesDone,
            InputUnitsDone: counters.InputUnitsDone,
            InputUnitsTotal: inventory?.EffectiveTotalInputUnits ?? 0,
            BootstrapEntitiesInserted: counters.BootstrapEntitiesInserted,
            BootstrapPhysicalitiesInserted: counters.BootstrapPhysicalitiesInserted,
            BootstrapAttestationsInserted: counters.BootstrapAttestationsInserted,
            ConsensusObservations: Math.Max(
                0, (foldMetrics?.ObservationsAccumulated ?? foldObservationsAtStart)
                   - foldObservationsAtStart),
            ConsensusCellDeposits: Math.Max(
                0, (foldMetrics?.CellsFolded ?? foldCellsAtStart) - foldCellsAtStart));
        long declaredInput = inventory?.EffectiveTotalInputUnits ?? 0;
        long declaredFiles = inventory?.FileCount ?? 0;
        bool emptySourceNoOp = result.UnitsApplied == 0 && (declaredInput > 0 || declaredFiles > 0);
        (string Status, string Detail)? explained = null;
        if (emptySourceNoOp && decomposer is IIngestNoOpExplainer explainer)
        {
            explained = explainer.ExplainEmptyRun(declaredInput);
            if (explained is { } e)
            {
                emptySourceNoOp = false;
                log.LogInformation(
                    "INGEST_EMPTY_EXPECTED source={Source} status={Status} detail={Detail}",
                    decomposer.SourceName, e.Status, e.Detail);
            }
        }
        string status = DeriveRunStatus(
            result.UnitsFailed,
            emptySourceNoOp,
            capped: options.DecomposerOptions.MaxInputUnits > 0,
            filesDone: counters.FilesDone,
            filesTotal: declaredFiles);
        // An explanation may name an otherwise complete no-op; it never overrides failed
        // units, unfinished files or an explicit input cap.
        if (status == "ok" && explained is { } exp)
            status = exp.Status;
        log.LogInformation(
            "INGEST_COMPLETE source={Source} layer={Layer} input_done={InputDone} input_total={InputTotal} "
            + "files_done={FilesDone} files_total={FilesTotal} files_reused_complete={FilesReusedComplete} "
            + "intents={Applied}/{Produced} "
            + "rows_new={Ent}e+{Phys}p+{Att}a elapsed_s={Elapsed:F1} failed={Failed} status={Status} "
            + "bootstrap_rows_new={BootEnt}e+{BootPhys}p+{BootAtt}a "
            + "synset_hit_cum={SynHit} synset_miss_cum={SynMiss} lang_miss_cum={LangMiss}",
            decomposer.SourceName, decomposer.LayerOrder,
            counters.InputUnitsDone, declaredInput,
            counters.FilesDone, declaredFiles, counters.FilesSkippedComplete,
            result.UnitsApplied, result.UnitsAttempted,
            result.EntitiesInserted, result.PhysicalitiesInserted, result.AttestationsInserted,
            result.WallClock.TotalSeconds, result.UnitsFailed, status,
            result.BootstrapEntitiesInserted, result.BootstrapPhysicalitiesInserted,
            result.BootstrapAttestationsInserted,
            SourceEntityIdConventions.SynsetHits, SourceEntityIdConventions.SynsetMisses,
            LanguageReference.ResolveMisses);
        log.LogInformation(
            "LAPSIGHT_AMPLIFICATION source={Source} input={Input} "
            + "payload_rows={PayloadRows} payload_entities={PayloadEntities} "
            + "payload_physicalities={PayloadPhysicalities} payload_attestations={PayloadAttestations} "
            + "bootstrap_rows={BootstrapRows} rows_per_input={RowsPerInput:F3} "
            + "fold_observations={FoldObservations} fold_cell_deposits={FoldCells} "
            + "observations_per_cell_deposit={ObservationsPerCell:F3}",
            decomposer.SourceName, result.InputUnitsDone,
            result.PayloadRowsInserted, result.PayloadEntitiesInserted,
            result.PayloadPhysicalitiesInserted, result.PayloadAttestationsInserted,
            result.BootstrapRowsInserted, result.PayloadRowsPerInput,
            result.ConsensusObservations, result.ConsensusCellDeposits,
            result.ObservationsPerCellDeposit);
        string? failureReason = status == "failed"
            ? DescribeRunFailure(result.UnitsFailed, counters.FilesDone, declaredFiles)
            : null;
        _obs.OnRunFinished(decomposer.SourceName, result, status, failureReason);
        if (status == "failed")
            throw new InvalidOperationException(
                $"{decomposer.SourceName}: ingest run recorded status=failed — "
                + (failureReason ?? "no reason derived")
                + ". Failing the process so the exit code matches the ledger.");
        if (emptySourceNoOp)
            throw new InvalidOperationException(
                $"{decomposer.SourceName}: source declares {declaredInput} input unit(s) / {declaredFiles} file(s) "
                + "but ingested 0 — grammar/format mismatch (silent no-op). Failing instead of reporting success. "
                + "Check the decomposer's modality/grammar matches the actual file format.");
        return result;
    }
}
