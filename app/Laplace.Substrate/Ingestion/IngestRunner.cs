using System.Collections.Immutable;
using System.Runtime.InteropServices;
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
    private Hash128 _ladderSource;

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

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            linked.Cancel();
        });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            linked.Cancel();
        });

        try
        {
            return await RunCoreAsync(decomposer, options, linked.Token);
        }
        catch (OperationCanceledException)
        {
            _obs.OnRunFailed(decomposer.SourceName, "cancelled", "run cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _obs.OnRunFailed(decomposer.SourceName, "failed", $"[{ex.GetType().Name}] {ex.Message}");
            throw;
        }
    }

    private static bool IsPeriodBoundaryIntent(SubstrateChange change) =>
        change.Metadata.SourceContentUnitName.StartsWith(
            IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal);

    private static bool IsFileTerminalIntent(SubstrateChange change) =>
        IsPeriodBoundaryIntent(change)
        || change.Metadata.SourceContentUnitName.StartsWith(
            IngestBatchPipeline.FileFailedUnitPrefix, StringComparison.Ordinal);

    private async Task<IngestRunResult> RunCoreAsync(
        IDecomposer decomposer,
        IngestRunOptions options,
        CancellationToken ct)
    {
        var log = _loggerFactory.CreateLogger($"Ingest:{decomposer.SourceName}");
        var sw = Stopwatch.StartNew();
        var foldMetrics = _writer as IConsensusFoldMetrics;
        long foldObservationsAtStart = foldMetrics?.ObservationsAccumulated ?? 0;
        long foldCellsAtStart = foldMetrics?.CellsFolded ?? 0;
        var failures = new List<IngestFailure>();
        long unitsAttempted = 0, unitsApplied = 0, unitsFailed = 0;
        long entitiesInserted = 0, physicalitiesInserted = 0, attestationsInserted = 0;
        long totalRoundTrips = 0;

        IReadOnlyList<Hash128> requiredPhysicalityTypes = decomposer.RequiredPhysicalityTypeIds;
        if (requiredPhysicalityTypes.Count > 0)
        {
            PhysicalityCoverage existingCoverage = await _reader.PhysicalityCoverageAsync(
                decomposer.SourceId, requiredPhysicalityTypes, ct).ConfigureAwait(false);
            if (!existingCoverage.Complete)
            {
                log.LogWarning(
                    "INGEST_STALE_PHYSICALITY_CONTRACT source={Source} governed={Governed} placed={Placed} "
                    + "missing={Missing} action=evict-and-rederive",
                    decomposer.SourceName,
                    existingCoverage.GovernedEntities,
                    existingCoverage.PlacedEntities,
                    existingCoverage.MissingEntities);

                await _reader.EvictSourceAsync(
                    decomposer.SourceId,
                    relationIds: null,
                    markerTypeIds: null,
                    ct).ConfigureAwait(false);

                PhysicalityCoverage afterEviction = await _reader.PhysicalityCoverageAsync(
                    decomposer.SourceId, requiredPhysicalityTypes, ct).ConfigureAwait(false);
                if (!afterEviction.Complete)
                    throw new InvalidOperationException(
                        $"{decomposer.SourceName}: stale physicality repair left "
                        + $"{afterEviction.MissingEntities} governed unplaced identities; "
                        + "refusing to reuse completion markers or add testimony on top of them");
            }
        }

        if (!options.SkipSourceCompletion
            && !options.BypassSourceCompletionGuard
            && !decomposer.PerFileCompletion
            && await _reader.HasSourceCompletedAsync(decomposer.SourceId, decomposer.LayerOrder, ct))
        {
            log.LogInformation(
                "{Source}: already ingested (completion marker present and physicality contract satisfied) — "
                + "short-circuiting; a re-ingest would double-count testimony into consensus.",
                decomposer.SourceName);
            _obs.OnRunSkipped(decomposer.SourceName, decomposer.LayerOrder);
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
        _obs.OnRunStart(decomposer.SourceName, decomposer.LayerOrder, inventory, artifactGraph);
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

        static long RowsOf(SubstrateChange c)
        {
            long rows = (long)c.Entities.Length + c.Physicalities.Length + c.Attestations.Length;
            if (!c.IntentStages.IsDefaultOrEmpty)
                foreach (var s in c.IntentStages)
                    rows += s.EntityCount + s.PhysicalityCount + s.AttestationCount;
            return rows;
        }

        static long BytesOf(SubstrateChange c)
        {
            long traj = 0;
            foreach (var p in c.Physicalities)
                if (p.TrajectoryXyzm is { Length: > 0 } t) traj += (long)t.Length * 8;
            long stageBytes = 0;
            int stageAtt = 0;
            if (!c.IntentStages.IsDefaultOrEmpty)
            {
                foreach (var s in c.IntentStages)
                {
                    if (s.IsInvalid) continue;
                    stageBytes += s.TotalTupleBytes;
                    stageAtt += s.AttestationCount;
                }
                // Commit grain must cover actual held native allocations,
                // including the auxiliary facet stream. The serialized E/P/A
                // metric remains unchanged and repeated references retain their
                // existing transport estimate without double-counting ownership.
                stageBytes = Math.Max(stageBytes,
                    IngestAdmissionSizing.HeldNativeStageBytes(c.IntentStages));
            }
            return IngestSizing.EstimateApplyGateBytes(
                c.Entities.Length,
                c.Physicalities.Length,
                c.Attestations.Length,
                traj,
                stageBytes,
                stageAtt);
        }

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
                    "INGEST_ADMISSION_WINDOW source={Source} intents={Intents} source_forms_upper={Forms} "
                    + "source_vertices_upper={Vertices} modeled_source_payload_bytes={Modeled} grant_bytes={Grant} "
                    + "over_bound_singleton={OverBound} scope=source-local-provider-expansion-unmodeled",
                    decomposer.SourceName, count, admissionWindow.Sizing.Source.Forms,
                    admissionWindow.Sizing.Source.StoredVertices, admissionWindow.ModeledSourcePayloadBytes,
                    applyEnvelope, count == 1 && admissionWindow.ModeledSourcePayloadBytes > applyEnvelope);
        }

        bool syncIngest = false;

        if (_ladderSource != decomposer.SourceId)
        {
            ContentLadderLedger.Reset();
            _ladderSource = decomposer.SourceId;
        }
        var runCt = ct;

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
                    // File boundaries are semantic/journal markers, not transaction-size
                    // boundaries. Tiny-file estates such as PropBank can contain thousands
                    // of one-record XML files; flushing on every boundary turns the source
                    // layout into thousands of database transactions. Keep the boundary in
                    // the same capacity-governed batch as its file data (and neighboring
                    // files). The marker is applied atomically with the batch, and
                    // CompleteFileAsync below still closes each file only after that apply.
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
                    }
                }, "ingest-decompose-pcore", pipelineCts.Token);

                // Apply batching is source-owned, not file-owned. The previous file-label
                // bucket made the physical source packaging dictate the database cadence:
                // a corpus with 7,567 one-record XML files could never coalesce those rows
                // into a normal working-set transaction. File labels remain on every change
                // for resume, observability, and fold ownership; only the apply accumulator
                // is shared across files of the same source.
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
                            + "source_forms_upper={Forms} source_vertices_upper={Vertices} "
                            + "modeled_source_payload_bytes={Modeled} grant_bytes={Grant}",
                            decomposer.SourceName,
                            bucket.Batch[0].Metadata.FileLabel ?? "<source>",
                            bucket.Batch.Count,
                            bucket.AdmissionWindow.Sizing.Source.Forms,
                            bucket.AdmissionWindow.Sizing.Source.StoredVertices,
                            bucket.AdmissionWindow.ModeledSourcePayloadBytes,
                            applyEnvelope);
                    await ProcessOwnedBatchAsync(bucket.Batch, decomposer, options, rng,
                        counters, failures, log, workingSet, runCt).ConfigureAwait(false);
                    bucket.Reset();
                }

                await using var pipelineCleanup = new ApplyPipelineCleanup(
                    pipelineCts, producer, buckets, channel.Reader);

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

                        // A terminal marker is ordinary zero/small-row control state for
                        // batching purposes. It stays behind that file's already-produced
                        // data in the stream and can share a transaction with other files.
                        // ProcessBatchAsync calls CompleteFileAsync only after the atomic
                        // apply returns, so files_done cannot outrun their durable rows.

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
        }
        finally
        {
            try
            {
                try
                {
                    await bulkRun.CompleteAsync(
                        phase => _obs.OnCompletionPhase(decomposer.SourceName, phase))
                        .ConfigureAwait(false);
                }
                finally
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
            }
            catch (OperationCanceledException)
            {
                log.LogWarning("bulk-run completion cancelled while releasing run state");
            }
        }

        // File progress is a durability statement, not a compose statement.
        // Pipelined consensus may finish after its evidence apply returned; every
        // terminal observer above was registered without blocking the apply lane.
        // By run completion the writer has drained those continuations, so join the
        // counter/observability tasks before deciding filesComplete/source completion.
        await counters.DrainPendingFileCompletionsAsync().ConfigureAwait(false);

        unitsAttempted = counters.UnitsAttempted;
        unitsApplied = counters.UnitsApplied;
        unitsFailed = counters.UnitsFailed;
        entitiesInserted = counters.EntitiesInserted;
        physicalitiesInserted = counters.PhysicalitiesInserted;
        attestationsInserted = counters.AttestationsInserted;
        totalRoundTrips = counters.RoundTrips;

        bool enforceEntityAdmission = options.DecomposerOptions.MaxInputUnits <= 0
            && counters.UnitsFailed == 0
            && failures.Count == 0;
        int governedWithoutPhysicality = ValidateEntityAdmission(counters, log, enforceEntityAdmission);

        long filesTotalForMarker = inventory?.FileCount ?? 0;
        bool filesComplete = filesTotalForMarker <= 0
            || counters.FilesDone == filesTotalForMarker;
        bool fullSuccessfulExtraction = options.DecomposerOptions.MaxInputUnits <= 0
            && counters.UnitsFailed == 0
            && failures.Count == 0
            && filesComplete;

        // A resumed multi-file run does not observe the input units inside marker-skipped
        // files. Publishing this run's observed suffix as the source's exact total shrinks the
        // inventory (VerbNet: 329 files became input_total=142 after 187 files were reused).
        // Preserve the declared/previous inventory whenever any file was reused complete.
        if (fullSuccessfulExtraction
            && inventory is not null
            && counters.InputUnitsDone > 0
            && counters.FilesSkippedComplete == 0)
            inventory.PublishExactTotal(counters.InputUnitsDone);

        if (fullSuccessfulExtraction && requiredPhysicalityTypes.Count > 0)
        {
            PhysicalityCoverage committedCoverage = await _reader.PhysicalityCoverageAsync(
                decomposer.SourceId, requiredPhysicalityTypes, ct).ConfigureAwait(false);
            if (!committedCoverage.Complete)
                throw new InvalidOperationException(
                    $"{decomposer.SourceName}: physicality coverage failed after ingest: "
                    + $"{committedCoverage.PlacedEntities}/{committedCoverage.GovernedEntities} "
                    + "governed semantic identities are physically realized; "
                    + $"{committedCoverage.MissingEntities} remain unplaced. "
                    + "No completion marker will be written.");
            log.LogInformation(
                "INGEST_PHYSICALITY_COVERAGE source={Source} governed={Governed} placed={Placed} missing=0 status=ok",
                decomposer.SourceName,
                committedCoverage.GovernedEntities,
                committedCoverage.PlacedEntities);
        }

        if (!options.SkipSourceCompletion
            && fullSuccessfulExtraction
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
            Failures: failures,
            FilesDone: counters.FilesDone,
            InputUnitsDone: counters.InputUnitsDone,
            InputUnitsTotal: inventory?.EffectiveTotalInputUnits ?? 0,
            GovernedIdentitiesWithoutPhysicality: governedWithoutPhysicality,
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
        // An explanation may name an otherwise complete no-op. It cannot
        // override failed units, unfinished files, or an explicit input cap.
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
        if (result.EntitiesInserted + result.PhysicalitiesInserted + result.AttestationsInserted > 0)
            await ReportPartitionPressureAsync(log, ct);
        if (emptySourceNoOp)
            throw new InvalidOperationException(
                $"{decomposer.SourceName}: source declares {declaredInput} input unit(s) / {declaredFiles} file(s) "
                + "but ingested 0 — grammar/format mismatch (silent no-op). Failing instead of reporting success. "
                + "Check the decomposer's modality/grammar matches the actual file format.");
        return result;
    }

    internal static string DeriveRunStatus(
        long unitsFailed,
        bool emptySourceNoOp,
        bool capped,
        int filesDone,
        long filesTotal)
    {
        if (unitsFailed > 0) return "failed";
        if (emptySourceNoOp) return "empty-noop";
        if (capped) return "capped";
        if (filesTotal > 0 && filesDone != filesTotal) return "failed";
        return "ok";
    }

    internal static bool ShouldFlushWorkingSet(long bufferedBytes, long byteCap) =>
        bufferedBytes >= byteCap;

    internal static bool ShouldFlushWorkingSetSourceBoundary(
        Hash128? bufferedSource, Hash128 nextSource) =>
        bufferedSource is { } source && source != nextSource;

    internal static string? DescribeRunFailure(long unitsFailed, int filesDone, long filesTotal)
    {
        if (unitsFailed > 0)
            return $"{unitsFailed} unit(s) failed to apply";
        if (filesTotal > 0 && filesDone < filesTotal)
            return $"files_done {filesDone} of {filesTotal} — {filesTotal - filesDone} file(s) "
                 + "did not reach completion; their content is absent from the substrate";
        if (filesTotal > 0 && filesDone > filesTotal)
            return $"files_done {filesDone} exceeds files_total {filesTotal} — "
                 + "the lane counted more completions than it declared inputs";
        return null;
    }

    private async Task ReportPartitionPressureAsync(ILogger log, CancellationToken ct)
    {
        IReadOnlyList<PartitionPressure> pressure;
        try
        {
            pressure = await _reader.PartitionPressureAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        foreach (var p in pressure)
            log.LogWarning(
                "INGEST_PARTITION_PRESSURE relation={Relation} rows={Rows} pct_of_default={Pct:F1} "
                + "action=promote-to-hot detail=\"{Relation} rides consensus_rdefault, a single "
                + "shared heap+btree. Add `hot = true` to its [[relation]] block in "
                + "engine/manifest/relation_types.toml and run scripts/codegen-attestation-law.py; "
                + "the partition seed adopts the existing rows in place.\"",
                p.Relation, p.Rows, p.PctOfDefault, p.Relation);
    }

    private void TrackTerminalWhenDurable(
        RunCounters counters,
        SubstrateChange intent,
        List<IngestFailure> failures)
    {
        if (intent.Metadata.FileLabel is not { Length: > 0 } fileLabel)
        {
            TrackIntent(counters, intent, failures);
            return;
        }

        // CompleteFileAsync is a durability observer. For ordinary pipelined bulk
        // ingest it waits for the already-dispatched consensus/mask continuation
        // that owns this file's completion marker. Do not await it on the apply
        // lane: doing so would serialize the next working set behind every refold.
        Task durable = _writer.CompleteFileAsync(fileLabel, CancellationToken.None);
        if (durable.IsCompletedSuccessfully)
        {
            TrackIntent(counters, intent, failures);
            return;
        }

        async Task ObserveAsync()
        {
            await durable.ConfigureAwait(false);
            TrackIntent(counters, intent, failures);
        }

        counters.TrackPendingFileCompletion(ObserveAsync());
    }

    private async Task ProcessOwnedIntentAsync(
        SubstrateChange intent,
        IDecomposer decomposer,
        IngestRunOptions options,
        Random rng,
        RunCounters counters,
        List<IngestFailure> failures,
        ILogger log,
        CancellationToken ct)
    {
        try
        {
            await ProcessOneIntentAsync(
                intent, decomposer, options, rng, counters, failures, log, ct);
        }
        finally
        {
            intent.ApplyEnvelope?.Dispose();
        }
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

        // Native stages are caller-owned only until the writer accepts them. Successful
        // apply retires those stages, so inspect E/P closure here while the exact source
        // tuples are still available. The tracker is idempotent across a retry.
        counters.EntityAdmission.Observe(intent);

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
                Func<CancellationToken, ValueTask>? verifier =
                    SubstrateApplyEnvelope.ComposeVerifier([intent]);
                var apply = verifier is null
                    ? await _writer.ApplyAsync(intent, ct)
                    : await _writer.ApplyWorkingSetAsync([intent], verifier, ct);

                if (intent.CountsAsUnit) Interlocked.Increment(ref counters._unitsApplied);
                Interlocked.Add(ref counters._entitiesInserted, apply.EntitiesInserted);
                Interlocked.Add(ref counters._physicalitiesInserted, apply.PhysicalitiesInserted);
                Interlocked.Add(ref counters._attestationsInserted, apply.AttestationsInserted);
                Interlocked.Add(ref counters._roundTrips, apply.RoundTrips);

                if (IsFileTerminalIntent(intent)
                    && intent.Metadata.FileLabel is { Length: > 0 })
                    TrackTerminalWhenDurable(counters, intent, failures);
                else
                    TrackIntent(counters, intent, failures);

                _obs.OnIntentApplied(decomposer.SourceName, apply);
                var progress = MakeProgress(counters);
                _obs.OnProgress(progress);
                options.Progress?.Report(progress);
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

    private async Task ProcessOwnedBatchAsync(
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
        try
        {
            await ProcessBatchAsync(
                batch, decomposer, options, rng, counters, failures, log, workingSet, ct);
        }
        finally
        {
            SubstrateApplyEnvelope.Release(batch);
        }
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

        // Same ownership boundary as the scalar path: validate the complete staged
        // source stream before a successful writer call disposes transferred stages.
        foreach (var intent in batch)
            counters.EntityAdmission.Observe(intent);

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
                Func<CancellationToken, ValueTask>? verifier =
                    SubstrateApplyEnvelope.ComposeVerifier(batch);
                var apply = verifier is not null
                    ? await _writer.ApplyWorkingSetAsync(batch, verifier, ct)
                    : workingSet
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
                {
                    if (IsFileTerminalIntent(intent)
                        && intent.Metadata.FileLabel is { Length: > 0 })
                        TrackTerminalWhenDurable(counters, intent, failures);
                    else
                        TrackIntent(counters, intent, failures);
                }

                log.LogInformation(
                    "INGEST_BATCH source={Source} intents={Intents} rows={Rows} "
                    + "rows_new={Ent}e+{Phys}p+{Att}a elapsed_ms={Ms:N0} rate_rows_s={Rps:N0} round_trips={RT}",
                    decomposer.SourceName, batch.Count, batchRows, apply.EntitiesInserted,
                    apply.PhysicalitiesInserted, apply.AttestationsInserted,
                    apply.WallClock.TotalMilliseconds, batchRows / secs, apply.RoundTrips);

                _obs.OnIntentApplied(decomposer.SourceName, apply);
                var progress = MakeProgress(counters);
                _obs.OnProgress(progress);
                options.Progress?.Report(progress);
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
        IngestArtifactGraph? artifactGraph,
        IngestRunOptions options,
        CancellationToken ct)
    {
        long cap = options.DecomposerOptions.MaxInputUnits;
        if (decomposer is IIngestInventoryProvider provider)
        {
            var inv = await provider.DescribeInputAsync(ctx, options.DecomposerOptions, ct);
            if (inv is not null)
            {
                if (artifactGraph is not null)
                    artifactGraph.ValidateInventory(inv);
                return ApplyInputCap(inv, cap);
            }
        }
        if (artifactGraph is not null)
            return artifactGraph.ToFileInventory("records");
        if (cap > 0)
            return IngestInventory.Single(cap, "records");
        long? est = await decomposer.EstimateUnitCountAsync(ctx, ct);
        return est is long n ? IngestInventory.Single(n) : null;
    }

    private static IngestArtifactGraph? LoadArtifactGraph(
        string ecosystemPath,
        bool required,
        bool allowAmbientManifest)
    {
        if (!Directory.Exists(ecosystemPath))
        {
            if (required)
                throw new DirectoryNotFoundException(
                    $"Required source estate directory does not exist: '{ecosystemPath}'.");
            return null;
        }
        string manifestPath = Path.Combine(ecosystemPath, "MANIFEST.tsv");
        if (File.Exists(manifestPath) && (required || allowAmbientManifest))
            return IngestArtifactGraph.Load(ecosystemPath);
        if (required)
            throw new FileNotFoundException(
                "Production source ingest requires a complete MANIFEST.tsv artifact graph.",
                manifestPath);
        return null;
    }

    private static int ValidateEntityAdmission(
        RunCounters counters,
        ILogger log,
        bool enforce)
    {
        var pending = counters.EntityAdmission.SnapshotPendingContent();
        if (pending.Length == 0)
        {
            int governed = counters.EntityAdmission.GovernedWithoutPhysicalityCount;
            log.LogInformation(
                "INGEST_IDENTITY_ADMISSION source={Source} composed_unplaced=0 governed_nonphysical={Governed} status=ok",
                counters.SourceName, governed);
            return governed;
        }

        int governedPending = counters.EntityAdmission.GovernedWithoutPhysicalityCount;
        string examples = string.Join(", ", pending.Take(8).Select(static e =>
            $"{e.Id}:{e.TypeId}@{e.UnitName}"));
        if (!enforce)
        {
            log.LogWarning(
                "INGEST_IDENTITY_ADMISSION source={Source} composed_unplaced={Unplaced} "
                + "governed_nonphysical={Governed} status=partial detail={Examples}",
                counters.SourceName, pending.Length, governedPending, examples);
            return governedPending;
        }
        throw new InvalidOperationException(
            $"entity admission failed for {counters.SourceName}: {pending.Length} content/composition "
            + "entity id(s) were emitted without physicality in the complete source stream; "
            + "existing database state cannot make an incomplete decomposer output lawful. Governed structural "
            + $"identities are exempt by type. First: {examples}");
    }

    private static IngestInventory ApplyInputCap(IngestInventory inv, long cap) =>
        cap > 0 && inv.TotalInputUnits > cap
            ? inv with { TotalInputUnits = cap }
            : inv;

    private void TrackIntent(RunCounters c, SubstrateChange intent, List<IngestFailure> failures)
    {
        string unit = intent.Metadata.SourceContentUnitName;

        const string fileFailed = IngestBatchPipeline.FileFailedUnitPrefix;
        if (unit.StartsWith(fileFailed, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref c._unitsFailed);
            var failure = new IngestFailure(
                intent.Metadata.IntentId,
                unit,
                "FileIngestFailure",
                unit[fileFailed.Length..],
                WasTransient: false,
                RetryAttempts: 0,
                OccurredAt: DateTimeOffset.UtcNow);
            lock (failures) failures.Add(failure);
            _obs.OnIntentFailed(c.SourceName ?? "", failure);
            string fileAndError = unit[fileFailed.Length..];
            int errorAt = fileAndError.IndexOf(": [", StringComparison.Ordinal);
            string file = errorAt >= 0 ? fileAndError[..errorAt] : fileAndError;
            string error = errorAt >= 0 ? fileAndError[(errorAt + 2)..] : fileAndError;
            _obs.OnFileFinished(c.SourceName ?? "", file, "failed", error: error);
            return;
        }

        const string periodBoundary = IngestBatchPipeline.PeriodBoundaryUnitPrefix;
        if (unit.StartsWith(periodBoundary, StringComparison.Ordinal))
        {
            int done = Interlocked.Increment(ref c._filesDone);
            string file = unit[periodBoundary.Length..];
            string fileStatus = "ok";
            if (unit.StartsWith(IngestBatchPipeline.SkippedBoundaryUnitPrefix, StringComparison.Ordinal))
            {
                file = unit[IngestBatchPipeline.SkippedBoundaryUnitPrefix.Length..];
                fileStatus = "skipped-complete";
                Interlocked.Increment(ref c._filesSkippedComplete);
            }
            else if (unit.StartsWith(IngestBatchPipeline.CancelledBoundaryUnitPrefix, StringComparison.Ordinal))
            {
                file = unit[IngestBatchPipeline.CancelledBoundaryUnitPrefix.Length..];
                fileStatus = "cancelled";
            }
            _obs.OnFileFinished(c.SourceName ?? "", file, fileStatus);
            c._currentFile = file;
            int total = c.Inventory?.FileCount ?? 0;
            if (MultiFileTelemetry.ShouldLogFileLine(done, total))
            {
                Console.Error.WriteLine(
                    $"INGEST_FILE_COMMITTED source={c.SourceName} file={file} "
                    + $"files={done}/{total} file_status={fileStatus} "
                    + $"run_elapsed_s={c.Sw?.Elapsed.TotalSeconds ?? 0:F0}");
            }
            return;
        }
        if (unit.StartsWith("layer-complete/", StringComparison.Ordinal)) return;

        long consumed = intent.Metadata.InputUnitsConsumed;
        if (consumed > 0 && intent.CountsAsUnit)
            Interlocked.Add(ref c._inputUnitsDone, consumed);
        else if (consumed == 0)
            c._currentFile = unit;
    }

    private IngestProgress MakeProgress(RunCounters c)
    {
        var inv = c.Inventory;
        var fold = _writer as IConsensusFoldMetrics;
        inv?.PublishObservedFloor(Math.Max(c.InputUnitsDone, c.InputUnitsComposed));
        return new(
            c.SourceName ?? "",
            c.LayerOrder,
            c.UnitsAttempted,
            c.UnitsApplied,
            c.UnitsFailed,
            inv?.EffectiveTotalInputUnits ?? 0,
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
            c.InputUnitsComposed,
            fold?.ConsensusUpsertBackendWallClock ?? TimeSpan.Zero,
            fold?.HighwayMaskBackendWallClock ?? TimeSpan.Zero,
            fold?.ConsensusUpsertCalls ?? 0,
            fold?.HighwayMaskCalls ?? 0,
            fold?.HighwayMaskPairs ?? 0);
    }

    private readonly record struct QueuedIntent(
        SubstrateChange Intent, long Rows, long SerializedBytes, IngestAdmissionSizing Admission);

    private sealed class ApplyBatchBucket(int capacity, long envelope)
    {
        internal List<SubstrateChange> Batch { get; } = new(capacity);
        internal IngestAdmissionWindow AdmissionWindow { get; } = new(envelope);
        internal long Rows;
        internal long Bytes;
        internal Hash128? Source;

        internal void Reset()
        {
            Batch.Clear();
            AdmissionWindow.Reset();
            Rows = 0;
            Bytes = 0;
            Source = null;
        }
    }

    private sealed class ApplyEnvelopeTransfer(SubstrateChange change) : IDisposable
    {
        private bool _completed;

        internal void Complete() => _completed = true;

        public void Dispose()
        {
            if (!_completed) change.ApplyEnvelope?.Dispose();
        }
    }

    private sealed class ApplyEnvelopeBatchOwner(IReadOnlyList<SubstrateChange> changes) : IDisposable
    {
        public void Dispose() => SubstrateApplyEnvelope.Release(changes);
    }

    private sealed class ApplyPipelineCleanup(
        CancellationTokenSource cancellation,
        Task producer,
        IReadOnlyDictionary<Hash128, ApplyBatchBucket> buckets,
        ChannelReader<QueuedIntent> reader) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            foreach (var bucket in buckets.Values)
                SubstrateApplyEnvelope.Release(bucket.Batch);
            while (reader.TryRead(out QueuedIntent abandoned))
                abandoned.Intent.ApplyEnvelope?.Dispose();
        }
    }

    private sealed class BulkRunLease : IAsyncDisposable
    {
        private readonly ISubstrateWriter _writer;
        private int _completed;

        private BulkRunLease(ISubstrateWriter writer) => _writer = writer;

        public static async Task<BulkRunLease> BeginAsync(
            ISubstrateWriter writer, CancellationToken ct)
        {
            await writer.BeginBulkRunAsync(ct).ConfigureAwait(false);
            return new BulkRunLease(writer);
        }

        public async Task CompleteAsync(Action<BulkRunCompletionPhase>? onPhase)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            await _writer.CompleteBulkRunAsync(onPhase, CancellationToken.None)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            await _writer.CompleteBulkRunAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
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
        internal long _bootstrapEntitiesInserted;
        internal long _bootstrapPhysicalitiesInserted;
        internal long _bootstrapAttestationsInserted;
        internal long _unitsProduced;
        internal long _inputUnitsDone;
        internal long _inputUnitsComposed;
        internal int _filesDone;
        internal int _filesSkippedComplete;
        internal string? _currentFile;
        internal EntityAdmissionTracker EntityAdmission { get; } = new();
        private readonly object _fileCompletionGate = new();
        private readonly List<Task> _pendingFileCompletions = [];

        internal void TrackPendingFileCompletion(Task task)
        {
            ArgumentNullException.ThrowIfNull(task);
            lock (_fileCompletionGate)
                _pendingFileCompletions.Add(task);
        }

        internal async Task DrainPendingFileCompletionsAsync()
        {
            Task[] pending;
            lock (_fileCompletionGate)
                pending = _pendingFileCompletions.ToArray();
            if (pending.Length > 0)
                await Task.WhenAll(pending).ConfigureAwait(false);
        }

        internal Stopwatch? Sw;
        internal string? SourceName;
        internal int LayerOrder;
        internal IngestInventory? Inventory;
        public long InputUnitsDone => Interlocked.Read(ref _inputUnitsDone);
        public long InputUnitsComposed => Interlocked.Read(ref _inputUnitsComposed);
        public int FilesDone => Volatile.Read(ref _filesDone);
        public int FilesSkippedComplete => Volatile.Read(ref _filesSkippedComplete);
        public string? CurrentFile => Volatile.Read(ref _currentFile);
        public long UnitsAttempted => Interlocked.Read(ref _unitsAttempted);
        public long UnitsProduced => Interlocked.Read(ref _unitsProduced);
        public long UnitsApplied => Interlocked.Read(ref _unitsApplied);
        public long UnitsFailed => Interlocked.Read(ref _unitsFailed);
        public long EntitiesInserted => Interlocked.Read(ref _entitiesInserted);
        public long PhysicalitiesInserted => Interlocked.Read(ref _physicalitiesInserted);
        public long AttestationsInserted => Interlocked.Read(ref _attestationsInserted);
        public long RoundTrips => Interlocked.Read(ref _roundTrips);
        public long BootstrapEntitiesInserted => Interlocked.Read(ref _bootstrapEntitiesInserted);
        public long BootstrapPhysicalitiesInserted => Interlocked.Read(ref _bootstrapPhysicalitiesInserted);
        public long BootstrapAttestationsInserted => Interlocked.Read(ref _bootstrapAttestationsInserted);
    }

    private sealed class InitializationAccountingWriter(
        ISubstrateWriter inner,
        RunCounters counters) : ISubstrateWriter
    {
        private void Observe(IReadOnlyList<SubstrateChange> changes)
        {
            for (int i = 0; i < changes.Count; i++)
                counters.EntityAdmission.Observe(changes[i]);
        }

        public async Task<ApplyResult> ApplyAsync(
            SubstrateChange change, CancellationToken ct = default)
        {
            Observe([change]);
            var result = await inner.ApplyAsync(change, ct).ConfigureAwait(false);
            Account(result, [change]);
            return result;
        }

        public async Task<ApplyResult> ApplyManyAsync(
            IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        {
            Observe(changes);
            var result = await inner.ApplyManyAsync(changes, ct).ConfigureAwait(false);
            Account(result, changes);
            return result;
        }

        public Task<ApplyResult> ApplyWorkingSetAsync(
            SubstrateChange change, CancellationToken ct = default)
            => ApplyWorkingSetAsync([change], ct);

        public async Task<ApplyResult> ApplyWorkingSetAsync(
            IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        {
            Observe(changes);
            var result = await inner.ApplyWorkingSetAsync(changes, ct).ConfigureAwait(false);
            Account(result, changes);
            return result;
        }

        public async Task<ApplyResult> ApplyWorkingSetAsync(
            IReadOnlyList<SubstrateChange> changes,
            Func<CancellationToken, ValueTask> verifier, CancellationToken ct = default)
        {
            Observe(changes);
            var result = await inner.ApplyWorkingSetAsync(changes, verifier, ct).ConfigureAwait(false);
            Account(result, changes);
            return result;
        }

        public Task CompleteFileAsync(string fileLabel, CancellationToken ct = default)
            => inner.CompleteFileAsync(fileLabel, ct);

        private void Account(ApplyResult result, IReadOnlyList<SubstrateChange> changes)
        {
            Interlocked.Add(ref counters._entitiesInserted, result.EntitiesInserted);
            Interlocked.Add(ref counters._physicalitiesInserted, result.PhysicalitiesInserted);
            Interlocked.Add(ref counters._attestationsInserted, result.AttestationsInserted);
            Interlocked.Add(ref counters._bootstrapEntitiesInserted, result.EntitiesInserted);
            Interlocked.Add(ref counters._bootstrapPhysicalitiesInserted, result.PhysicalitiesInserted);
            Interlocked.Add(ref counters._bootstrapAttestationsInserted, result.AttestationsInserted);
            Interlocked.Add(ref counters._roundTrips, result.RoundTrips);
        }
    }

    private sealed record InternalContext(
        string EcosystemPath,
        IReadOnlyList<IngestArtifact> SelectedArtifacts,
        bool HasArtifactGraph,
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
