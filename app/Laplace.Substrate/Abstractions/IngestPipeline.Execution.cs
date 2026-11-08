using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static partial class IngestBatchPipeline
{
    private static async IAsyncEnumerable<SubstrateChange> RunMultiFileParallelAsync<TRecord>(
        IMultiFileRecordStream<TRecord> stream,
        Func<string, IIngestRecordHandler<TRecord>> handlerFactory,
        Func<string, IngestBatchConfig> configFactory,
        int workers,
        bool isolateFileFailures,
        PerFileResumePlan? resume,
        [EnumeratorCancellation] CancellationToken ct)
    {
        CancellationToken consumerToken = ct;
        await using var producers = new IngestProducerGroup(ct);
        ct = producers.Token;
        // The dispatcher enumerates FILE SOURCES — cheap handles, reading NOTHING — into a bounded
        // channel; N workers each claim one source and run OPEN + read + parse + compose +
        // working-set finalization. The expensive parse is therefore
        // parallel across files, and no file is ever slammed into a list — records stream straight
        // from the file reader into the working-set spine. Finalized changes cross outCh into the
        // driver's one coalescing apply lane; database apply itself fans out by table partitions.
        // Files are order-independent (references resolve content-addressed), so there is no
        // source-level phase/barrier — just the pool.
        var sources = Channel.CreateBounded<IFileRecordSource<TRecord>>(
            new BoundedChannelOptions(workers)
            { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false });
        var outCh = Channel.CreateBounded<SubstrateChange>(
            new BoundedChannelOptions(workers)
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
        await using var fileEnumerator = stream.FilesAsync(ct).GetAsyncEnumerator(ct);
        while (peeked.Count <= workers)
        {
            if (!await fileEnumerator.MoveNextAsync())
                break;
            peeked.Add(fileEnumerator.Current);
            if (peeked.Count > workers) exhaustedInPeek = false;
        }

        int composeWorkers = Math.Max(1, IngestTopology.Current.ComposeWorkers);
        int initialFileCount = exhaustedInPeek ? peeked.Count : Math.Min(workers, peeked.Count);
        var initialSources = peeked.Take(initialFileCount).ToArray();
        int[] initialWidths = MultiFileScheduler.PlanInitialSegments(
            initialSources.Select(static source =>
                    MultiFileScheduler.EstimateBytes(source.FilePath))
                .ToArray(),
            workers,
            composeWorkers);
        var plannedSegments = new Dictionary<IFileRecordSource<TRecord>, int>(
            ReferenceEqualityComparer.Instance);
        for (int i = 0; i < initialSources.Length; i++)
            plannedSegments[initialSources[i]] = initialWidths[i];
        int concurrentWorkingSets = Math.Max(1, initialWidths.Sum());

        // Resolve the resume question a CHUNK at a time on the way into the channel: file
        // identities in parallel (pure CPU: read + BLAKE3), then ONE marker probe for the
        // whole chunk. The scalar path this replaces cost one round trip per file, which
        // on FrameNet was 14,900 of them and ~100% of the run's wall clock. Chunked rather
        // than whole-corpus so the stream stays lazy — nothing materializes 14,900 sources.
        int resumeProbeChunkMax = IngestSizing.ResolveApplyIo(
            IngestTopology.Current.ApplyPartitions).ProbeChunkIds;
        var chunk = new List<IFileRecordSource<TRecord>>();
        int chunkTarget = 1;   // ramps to the byte-derived database probe width
        async Task FlushChunkAsync()
        {
            if (chunk.Count == 0) return;
            if (resume is { } plan && plan.Resolved is { } cache && !plan.IgnoreCompletedFiles)
            {
                var paths = chunk.Where(s => s.FilePath is not null).Select(s => s.FilePath!).ToArray();
                var roots = new Hash128?[paths.Length];

                // Identity reads are bounded independently from the file workers. Each
                // task uses one pooled 4 MiB buffer; no whole-file arrays or nested CPU
                // fan-out sit outside the ingest memory envelope.
                int hashWorkers = Math.Max(1, Math.Min(
                    IngestTopology.Current.IoWorkersAvailable,
                    (int)Math.Max(1, IngestSizing.ResolveWorkingSetFlushEnvelopeBytes()
                        / ResumeFingerprintBlockBytes)));
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, paths.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = hashWorkers, CancellationToken = ct },
                    (idx, _) =>
                    {
                        roots[idx] = TryResolveFileIdentity(paths[idx]);
                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);

                var probe = new List<Hash128>(paths.Length);
                foreach (var r in roots) if (r is { } v) probe.Add(v);
                var done = probe.Count > 0
                    ? plan.DecomposerSourceId == default
                        ? await plan.Reader.HasSourcesCompletedAsync(probe, plan.LayerOrder, ct)
                            .ConfigureAwait(false)
                        : await plan.Reader.HasFilesCompletedAsync(
                            probe, plan.DecomposerSourceId, plan.LayerOrder, ct)
                            .ConfigureAwait(false)
                    : (IReadOnlySet<Hash128>)new HashSet<Hash128>();

                for (int i = 0; i < paths.Length; i++)
                    cache[paths[i]] = (roots[i], roots[i] is { } v && done.Contains(v));
            }
            foreach (var s in chunk) await sources.Writer.WriteAsync(s, ct);
            chunk.Clear();
            if (chunkTarget < resumeProbeChunkMax)
                chunkTarget = (int)Math.Min(resumeProbeChunkMax, (long)chunkTarget * 2);
        }

        producers.Run(async () =>
        {
            try
            {
                foreach (var source in peeked)
                {
                    chunk.Add(source);
                    if (chunk.Count >= chunkTarget) await FlushChunkAsync();
                }
                if (!exhaustedInPeek)
                    while (await fileEnumerator.MoveNextAsync())
                    {
                        chunk.Add(fileEnumerator.Current);
                        if (chunk.Count >= chunkTarget) await FlushChunkAsync();
                    }
                await FlushChunkAsync();
                sources.Writer.Complete();
            }
            catch (Exception ex)
            {
                // Complete(ex) propagates the failure to ReadAllAsync instead of leaving
                // consumers parked. Without it a throw anywhere above skips Complete()
                // entirely and every worker awaits a channel that never closes -- a hang
                // with no error. That window widened when identity resolution and a
                // marker round trip moved into this try: the DB call is exactly what
                // fails when the substrate is dropped.
                sources.Writer.TryComplete(ex);
                throw;
            }
            finally
            {
                // Belt and braces: TryComplete is idempotent, so a path that somehow
                // reaches here un-completed still releases the consumers.
                sources.Writer.TryComplete();
            }
        });

        // Approximate total from the peek when the stream fit in it; else 0 (sample-only).
        int knownFileTotal = exhaustedInPeek ? peeked.Count : 0;
        int fileOrdinal = 0;
        int activeWorkingSets = 0;

        for (int w = 0; w < workers; w++)
        {
            // Captured OUTSIDE Task.Run. `for` does not give per-iteration capture the way
            // `foreach` does, so a `int workerId = w;` inside the lambda reads the one shared
            // loop variable at RUN time — racing the loop thread and usually yielding the exit
            // value for every worker. That silently made per-worker ingest telemetry useless.
            int workerId = w;
            producers.Run(async () =>
            {
                await foreach (var source in sources.Reader.ReadAllAsync(ct))
                {
                    var baseConfig = configFactory(source.FileLabel);
                    int desiredSegments = plannedSegments.TryGetValue(source, out int planned)
                        ? planned
                        : 1;
                    int segments = MonolithSegmenter.ResolveSegments(baseConfig, desiredSegments);
                    Interlocked.Add(ref activeWorkingSets, segments);
                    try
                    {
                    var config = baseConfig
                        .WithActiveWorkingSetConcurrency(
                            concurrentWorkingSets,
                            () => Math.Max(1, Volatile.Read(ref activeWorkingSets)));

                    // Per-file resume (GH #898): identity + marker check happen in the
                    // WORKER, so skips parallelize with the ingest they replace.
                    var (fileRoot, skipComplete) =
                        await ResolveResumeAsync(resume, source.FilePath, ct).ConfigureAwait(false);
                    if (skipComplete)
                    {
                        Console.Error.WriteLine(
                            $"INGEST_FILE_SKIPPED file={source.FileLabel} worker={workerId} reason=marker-complete");
                        // Ledger: a true-skipped file never opens, so it has no 'running' row.
                        // Recording it is how a resumed run accounts for the prefix it did not redo.
                        Laplace.Ingestion.IngestObservabilityScope.Current.OnFileComposed(
                            Laplace.Ingestion.IngestObservabilityScope.SourceName,
                            source.FileLabel, resumeFingerprint: fileRoot);
                        // Counts, for the reason spelled out on the sequential path's skip:
                        // FilesTotal counts enumerated files, _filesDone counts boundaries, so
                        // dropping the boundary makes an already-complete file read as
                        // unfinished and fails the run. Boundary, not FileCompletion -- the
                        // marker this file already carries is what got us here; re-depositing
                        // it would be a write on a lane whose whole contract is zero rows.
                        await IngestProducerGroup.WriteAsync(outCh.Writer,
                            BuildSkippedBoundary(config.SourceId, source.FileLabel), ct);
                        continue;
                    }

                    var records = new AsyncEnumerableRecordStream<TRecord>(source.RecordsAsync(ct));
                    int ordinal = Interlocked.Increment(ref fileOrdinal);
                    var fileSw = System.Diagnostics.Stopwatch.StartNew();
                    Laplace.Ingestion.IngestObservabilityScope.Current.OnFileStarted(
                        Laplace.Ingestion.IngestObservabilityScope.SourceName, source.FileLabel, TryFileBytes(source.FilePath));
                    bool logFile = MultiFileTelemetry.ShouldLogFileLine(ordinal, knownFileTotal);
                    if (logFile)
                        Console.Error.WriteLine(
                            $"INGEST_FILE_START file={source.FileLabel} worker={workerId}"
                            + (segments > 1 ? $" segments={segments}" : ""));

                    var changes = segments > 1
                        ? MonolithSegmenter.RunSegmentedAsync(
                            records,
                            _ => handlerFactory(source.FileLabel),
                            _ => config,
                            segments,
                            source.FileLabel,
                            ct)
                        : RunAsync(records, handlerFactory(source.FileLabel), config, ct);

                    long fileUnits = 0;
                    long fileEntities = 0, filePhysicalities = 0, fileAttestations = 0;
                    Hash128? semanticFileId = null;
                    SubstrateChange? fileFailure = null;
                    try
                    {
                        await foreach (var change in changes)
                        {
                            fileUnits += change.Metadata.InputUnitsConsumed;
                            CaptureSemanticFileId(
                                ref semanticFileId, change.Metadata.FileId, source.FileLabel);
                            var rowCounts = CountRows(change);
                            fileEntities += rowCounts.Entities;
                            filePhysicalities += rowCounts.Physicalities;
                            fileAttestations += rowCounts.Attestations;
                            await IngestProducerGroup.WriteAsync(
                                outCh.Writer, BindFileLabel(change, source.FileLabel), ct);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (isolateFileFailures)
                    {
                        // Failures always log — sampling must not hide a broken file.
                        Console.Error.WriteLine(
                            $"INGEST_FILE_FAILED file={source.FileLabel} worker={workerId} "
                            + $"error=[{ex.GetType().Name}] {ex.Message}");
                        fileFailure = BuildFileFailure(config.SourceId, source.FileLabel, ex);
                    }
                    Laplace.Ingestion.IngestObservabilityScope.Current.OnFileComposed(
                        Laplace.Ingestion.IngestObservabilityScope.SourceName, source.FileLabel,
                        semanticFileId,
                        records: fileUnits,
                        entities: fileEntities,
                        physicalities: filePhysicalities,
                        attestations: fileAttestations,
                        resumeFingerprint: fileRoot);
                    // Publish compose accounting before the boundary becomes visible to the
                    // apply consumer. The runner terminalizes the file when that boundary is
                    // applied; reversing these two operations lets a fast consumer enqueue
                    // Finished before Composed and exposes a terminal zero-count row in the UI.
                    await IngestProducerGroup.WriteAsync(outCh.Writer,
                        fileFailure
                            ?? (fileRoot is { } fr && resume is { } rp
                                ? BuildFileCompletion(config.SourceId, source.FileLabel, fr, rp.LayerOrder,
                                    config.CanonicalNamesProvider?.Invoke())
                                : BuildPeriodBoundary(config.SourceId, source.FileLabel)), ct);
                    if (fileFailure is not null) continue;

                    if (logFile)
                    {
                        double secs = Math.Max(1e-3, fileSw.Elapsed.TotalSeconds);
                        Console.Error.WriteLine(
                            $"INGEST_FILE_COMPOSED file={source.FileLabel} worker={workerId} "
                            + $"records={fileUnits:N0} elapsed_s={secs:F1} rate_rec_s={fileUnits / secs:N0}");
                    }
                    }
                    finally
                    {
                        Interlocked.Add(ref activeWorkingSets, -segments);
                    }
                }
            });
        }

        await foreach (var change in producers.ConsumeAsync(outCh, consumerToken))
            yield return change;
    }

    private static (long Entities, long Physicalities, long Attestations) CountRows(SubstrateChange change)
    {
        long entities = change.Entities.Length;
        long physicalities = change.Physicalities.Length;
        long attestations = change.Attestations.Length;
        if (!change.IntentStages.IsDefaultOrEmpty)
        {
            foreach (var stage in change.IntentStages)
            {
                if (stage.IsInvalid) continue;
                entities += stage.EntityCount;
                physicalities += stage.PhysicalityCount;
                attestations += stage.AttestationCount;
            }
        }
        return (entities, physicalities, attestations);
    }

    private static void ResetBatchScope<TRecord>(IIngestRecordHandler<TRecord> handler)
    {
        if (handler is IIngestBatchScopedHandler scoped)
            scoped.ResetBatchState();
    }

    private static bool ShouldCloseWorkingSet(
        BatchState state, IngestBatchConfig config, int pendingCount = 0)
    {
        var profile = config.WorkingSetProfile ?? IngestSourceProfile.Default;

        // Close the compose set at the small COMPOSE FLUSH ENVELOPE, not the large apply
        // COPY budget (WorkingSetMode.BudgetBytes ~ RAM/16, up to 4 GiB). Holding a set open
        // until the apply budget fills accumulates millions of deferred tier trees plus the
        // live content bank and collapses compose throughput (MEASURED 30k -> 1.8k rec/s as
        // a ~4 GiB set filled with ~3M records before flushing). The envelope (RAM/64,
        // <= 512 MiB) closes the set continuously in small memory-bounded batches so resident
        // memory stays flat and compose stays fast.
        long envelope = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(
            config.EffectiveConcurrentWorkingSets);

        int recordCap = config.EffectiveWorkingSetRecordCap;
        // InBatch + still-buffered pending: pending is not in InBatch until FlushPending.
        int inFlight = state.InBatch + Math.Max(0, pendingCount);
        if (recordCap > 0 && inFlight >= recordCap)
            return true;
        if (state.EstimatedOutputRows >= config.MaxOutputRows)
            return true;

        long staged = state.Builder.StagedBytesEstimate;
        long deferred = state.WorkingSetDeferred is IWorkingSetDeferredBatch resident
            ? resident.ResidentBytes
            : 0;
        long uncomposed = checked(
            (long)Math.Max(0, pendingCount) * profile.UncomposedResidentBytesPerRecord);
        if (staged >= envelope
            || deferred >= envelope - staged
            || uncomposed >= envelope - staged - deferred)
            return true;

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
            bool rootsAlreadyProbed = false;
            while (batch.Count > 0)
            {
                var deferred = (WorkingSetDeferredBatch<TRecord>)(state.WorkingSetDeferred
                    ??= new WorkingSetDeferredBatch<TRecord>());
                long envelope = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(
                    config.EffectiveConcurrentWorkingSets);
                long available = Math.Max(1,
                    envelope - state.Builder.StagedBytesEstimate - deferred.ResidentBytes);
                long outputRowsAvailable = Math.Max(
                    1L, (long)config.MaxOutputRows - state.EstimatedOutputRows);
                var composed = await IngestDescentFlush.ComposeBatchAsync(
                    batch, handler, reader, state.Builder, config, probedAbsent, ct,
                    available, rootsAlreadyProbed, outputRowsAvailable).ConfigureAwait(false);
                rootsAlreadyProbed = true;
                deferred.Shortcircuited.AddRange(composed.Shortcircuited);
                deferred.Pending.AddRange(composed.Pending);
                deferred.AddResidentBytes(composed.ResidentBytes);
                foreach (var (_, units) in composed.Shortcircuited)
                    state.AddUnits(units, 0);
                foreach (var (record, _) in composed.Pending)
                    state.AddUnits(
                        handler.UnitsPerRecord(record),
                        handler.EstimatedOutputRows(record));

                if (batch.Count > 0 || ShouldCloseWorkingSet(state, config))
                {
                    await state.FinalizeWorkingSetAsync(handler, reader, config, probedAbsent, ct);
                    yield return await state.YieldBatchAsync(ct);
                    state.ResetBuilder(config.NewBuilder(state.BatchNumber));
                }
            }
        }
        else
        {
            var drained = await IngestDescentFlush.ProbeAndDrainAsync(
                batch, handler, reader, state.Builder, config, probedAbsent, ct).ConfigureAwait(false);

            foreach (var (record, units) in drained)
            {
                state.AddUnits(units, handler.EstimatedOutputRows(record));
                if (state.InBatch >= config.BatchSize
                    || state.EstimatedOutputRows >= config.MaxOutputRows)
                {
                    yield return await state.YieldBatchAsync(ct);
                    state.ResetBuilder(config.NewBuilder(state.BatchNumber));
                }
            }
        }
    }

    private sealed class BatchState(SubstrateChangeBuilder builder) : IDisposable
    {
        public SubstrateChangeBuilder Builder { get; private set; } = builder;
        public int InBatch { get; private set; }
        public long EstimatedOutputRows { get; private set; }
        public int BatchNumber { get; private set; }
        internal object? WorkingSetDeferred { get; set; }
        private long _rowsInBatch;

        public void Dispose()
        {
            try
            {
                (WorkingSetDeferred as IWorkingSetDeferredBatch)?.Dispose();
            }
            finally
            {
                WorkingSetDeferred = null;
                Builder.Dispose();
            }
        }

        public async Task FinalizeWorkingSetAsync<TRecord>(
            IIngestRecordHandler<TRecord> handler,
            ISubstrateReader reader,
            IngestBatchConfig config,
            ISet<Hash128>? probedAbsent,
            CancellationToken ct)
        {
            if (WorkingSetDeferred is not WorkingSetDeferredBatch<TRecord> deferred || !deferred.HasWork)
            {
                probedAbsent?.Clear();
                return;
            }
            await IngestDescentFlush.FinalizeWorkingSetAsync(
                deferred, handler, reader, Builder, config, probedAbsent, ct).ConfigureAwait(false);
            WorkingSetDeferred = null;
            // Working-set-lifetime by contract (TierTreeDescent: another writer
            // may commit any of these ids once this working set's rows are out).
            probedAbsent?.Clear();
        }

        public void AddUnits(long units, long estimatedOutputRows)
        {
            InBatch++;
            _rowsInBatch += units;
            EstimatedOutputRows = checked(
                EstimatedOutputRows + Math.Max(0, estimatedOutputRows));
        }

        public void ResetBuilder(SubstrateChangeBuilder next)
        {
            ArgumentNullException.ThrowIfNull(next);
            var previous = Builder;
            Builder = next;
            previous.Dispose();
        }

        public async Task<SubstrateChange> YieldBatchAsync(CancellationToken ct)
        {
            var change = await Builder.SetInputUnitsConsumed(_rowsInBatch).BuildAsync(ct);
            InBatch = 0;
            _rowsInBatch = 0;
            EstimatedOutputRows = 0;
            BatchNumber++;
            return change;
        }

        public async Task<SubstrateChange> BuildRemainingAsync(CancellationToken ct)
        {
            return await Builder.SetInputUnitsConsumed(_rowsInBatch).BuildAsync(ct);
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
