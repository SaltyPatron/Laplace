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
    private static void CaptureSemanticFileId(
        ref Hash128? current, Hash128? candidate, string fileLabel)
    {
        if (candidate is not { } fileId) return;
        if (current is { } existing && existing != fileId)
            throw new InvalidOperationException(
                $"file '{fileLabel}' emitted conflicting semantic identities {existing} and {fileId}");
        current = fileId;
    }

    /// <summary>How often a still-composing file republishes its counters: one coalesced
    /// UPDATE per file per interval, never per change.</summary>
    private const long FileProgressIntervalMs = 2_000;

    public const string PeriodBoundaryUnitPrefix = "period-boundary/";
    public const string SkippedBoundaryUnitPrefix = PeriodBoundaryUnitPrefix + "skipped-complete/";
    public const string CancelledBoundaryUnitPrefix = PeriodBoundaryUnitPrefix + "cancelled/";

    /// <summary>Per-file failure receipt (see IngestRunner.TrackIntent), emitted instead of the
    /// file's period boundary when file-failure isolation is on and that file's read, parse or
    /// compose threw. Zero rows, CountsAsUnit=false: the runner records the failure and its
    /// reason, and the file is neither counted done nor marked complete.</summary>
    public const string FileFailedUnitPrefix = "file-failed/";
    public const string ApplyBarrierUnitPrefix = "apply-barrier/";

    /// <summary>Attach the physical-file execution owner without changing content identity.</summary>
    public static SubstrateChange BindFileLabel(SubstrateChange change, string fileLabel)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileLabel);
        if (change.Metadata.FileLabel is { } existing
            && !string.Equals(existing, fileLabel, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"ingest change already belongs to file '{existing}', cannot rebind to '{fileLabel}'");
        return change with { Metadata = change.Metadata with { FileLabel = fileLabel } };
    }

    public static SubstrateChange BuildPeriodBoundary(Hash128 sourceId, string fileLabel) =>
        BindFileLabel(new SubstrateChangeBuilder(
            sourceId, $"{PeriodBoundaryUnitPrefix}{fileLabel}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0).Build(), fileLabel);

    public static SubstrateChange BuildSkippedBoundary(Hash128 sourceId, string fileLabel) =>
        BindFileLabel(new SubstrateChangeBuilder(
            sourceId, $"{SkippedBoundaryUnitPrefix}{fileLabel}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0).Build(), fileLabel);

    public static SubstrateChange BuildCancelledBoundary(Hash128 sourceId, string fileLabel) =>
        BindFileLabel(new SubstrateChangeBuilder(
            sourceId, $"{CancelledBoundaryUnitPrefix}{fileLabel}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0).Build(), fileLabel);

    public static SubstrateChange BuildApplyBarrier(
        Hash128 sourceId, string label, IngestApplyBarrier barrier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(barrier);
        return new SubstrateChangeBuilder(
                sourceId, $"{ApplyBarrierUnitPrefix}{label}", null,
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0)
            .Build() with
            {
                CountsAsUnit = false,
                ApplyBarrier = barrier,
            };
    }

    /// <summary>
    /// Per-file resume for multi-file sources. Testimony is not idempotent, so a restarted
    /// run must not re-fold what was applied. Each finished file's boundary records a unit
    /// completion keyed by the file's byte fingerprint, and a later run skips completed
    /// files before opening them; an interrupted run redoes only the file in flight.
    /// </summary>
    public readonly record struct PerFileResumePlan(
        ISubstrateReader Reader,
        int LayerOrder,
        bool IgnoreCompletedFiles,
        Hash128 DecomposerSourceId = default)
    {
        /// <summary>
        /// Dispatcher-resolved (fingerprint, already-complete) per file path, filled a chunk
        /// at a time so the marker probe costs one round trip per chunk. An absent entry
        /// sends the worker to the scalar probe, never to a skip.
        /// </summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, (Hash128? Root, bool Skip)>? Resolved { get; init; }
    }

    // The resume fingerprint is Merkle(domain, BLAKE3(length), BLAKE3 of each block of this
    // size). The block size is part of that preimage, so changing it invalidates existing
    // completion markers; it is not an execution batch knob.
    private const int ResumeFingerprintBlockBytes = 4 << 20;
    private static readonly Hash128 ResumeHashDomain =
        SubstrateCanonicalIds.OfVersioned("file-resume-fingerprint");

    /// <summary>Input size for the ledger; 0 when the source has no path or the file is
    /// unreadable.</summary>
    public static long TryFileBytes(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return 0;
        try { return new FileInfo(filePath).Length; }
        catch { return 0; }
    }

    public static Hash128? TryResolveFileIdentity(string? filePath)
    {
        if (filePath is null) return null;
        byte[]? buffer = null;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return null;

            var chunks = new List<Hash128>()
            {
                ResumeHashDomain,
            };
            Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, info.Length);
            chunks.Add(Hash128.Blake3(lengthBytes));

            buffer = ArrayPool<byte>.Shared.Rent(ResumeFingerprintBlockBytes);
            using var fs = IngestIo.OpenSequentialRead(filePath);
            while (true)
            {
                int read = 0;
                while (read < ResumeFingerprintBlockBytes)
                {
                    int n = fs.Read(buffer, read, ResumeFingerprintBlockBytes - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read == 0) break;
                chunks.Add(Hash128.Blake3(buffer.AsSpan(0, read)));
                if (read < ResumeFingerprintBlockBytes) break;
            }
            return Hash128.Merkle(0, CollectionsMarshal.AsSpan(chunks));
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return null; // unreadable path: no resume fingerprint
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Period boundary that also records the file's unit-completion receipt for resume.</summary>
    public static SubstrateChange BuildFileCompletion(
        Hash128 sourceId, string fileLabel, Hash128 fileRoot, int layerOrder,
        IReadOnlyCollection<string>? canonicalNames = null)
    {
        var builder = new SubstrateChangeBuilder(
            sourceId, $"{PeriodBoundaryUnitPrefix}{fileLabel}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0);
        Laplace.Ingestion.LayerCompletion.RecordFile(
            builder, fileRoot, sourceId, layerOrder);
        var change = BindFileLabel(builder.Build(), fileLabel);
        return canonicalNames is { Count: > 0 }
            ? change with
            {
                CanonicalNames = canonicalNames
                    .Where(static n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.Ordinal)
                    .ToImmutableArray(),
            }
            : change;
    }

    public static SubstrateChange BuildFileFailure(Hash128 sourceId, string fileLabel, Exception ex)
    {
        // The receipt carries a concise display message. Keep the full exception
        // chain and stack in the host's persistent Trace bridge before isolation.
        System.Diagnostics.Trace.TraceError("INGEST_FILE_FAILED source={0} file={1}: {2}", sourceId, fileLabel, ex);
        return BindFileLabel(
            new SubstrateChangeBuilder(
                sourceId, $"{FileFailedUnitPrefix}{fileLabel}: [{ex.GetType().Name}] {ex.Message}", null,
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0)
            .Build() with { CountsAsUnit = false },
            fileLabel);
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
            ? config.EffectiveWorkingSetProbeInterval
            : config.ProbeChunkSize;
        var pending = new List<TRecord>(probeInterval);
        long pendingResidentBytes = 0;
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

        using var state = new BatchState(config.NewBuilder(0));
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
            pendingResidentBytes = checked(pendingResidentBytes
                + IngestRecordMemory.Measure(record, config.WorkingSetProfile));

            if (pending.Count >= probeInterval
                || pendingResidentBytes >= IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(
                    config.EffectiveConcurrentWorkingSets))
            {
                await foreach (var change in FlushPending(pending, handler, reader, state, config, probedAbsent, ct))
                {
                    yield return change;
                    ResetBatchScope(handler);
                }
                pendingResidentBytes = 0;
            }

            // Pending records count through the source model before compose. Composed
            // deferred units count through their actual retained tree capacity. Check both
            // after every probe flush so an unexpectedly broad tree closes immediately
            // instead of waiting for another arbitrary record-count threshold.
            if (config.WorkingSet
                && ShouldCloseWorkingSet(state, config, pending.Count))
            {
                if (pending.Count > 0)
                {
                    await foreach (var change in FlushPending(
                                       pending, handler, reader, state, config, probedAbsent, ct))
                    {
                        yield return change;
                        ResetBatchScope(handler);
                    }
                    pendingResidentBytes = 0;
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
    /// ingested concurrently across a bounded pool — each file has its own handler/config/working
    /// set; native compose is lock-free and each builder owns its intent stage. Finalized changes
    /// merge into one bounded stream; the caller coalesces fragments across files and applies those
    /// batches through the writer's internally parallel bulk path. No phase/ordering concept:
    /// references resolve content-
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
        bool isolateFileFailures = false,
        PerFileResumePlan? resume = null,
        CancellationToken ct = default)
    {
        int workers = maxTotalUnits > 0 ? 1 : Math.Max(1, fileWorkers);
        return workers <= 1
            ? RunMultiFileSequentialAsync(
                stream, handlerFactory, configFactory, maxTotalUnits, isolateFileFailures, resume, ct)
            : RunMultiFileParallelAsync(
                stream, handlerFactory, configFactory, workers, isolateFileFailures, resume, ct);
    }

    /// <summary>
    /// Resume decision for one file: resolve its content identity and consult the
    /// marker. Returns the identity (null = lane opted out / no path / oversized)
    /// and whether the file is already marker-complete.
    /// </summary>
    private static async Task<(Hash128? FileRoot, bool Skip)> ResolveResumeAsync(
        PerFileResumePlan? resume, string? filePath, CancellationToken ct)
    {
        if (resume is not { } plan) return (null, false);

        // Dispatcher already resolved this file as part of a bulk probe: no round trip.
        // A MISS falls through to the scalar path below rather than assuming anything —
        // the fallback can only cost latency, never skip an un-ingested file.
        if (filePath is not null && plan.Resolved is { } cache
            && cache.TryGetValue(filePath, out var hit))
            return plan.IgnoreCompletedFiles ? (hit.Root, false) : hit;

        Hash128? root = TryResolveFileIdentity(filePath);
        if (root is not { } r) return (null, false);
        if (plan.IgnoreCompletedFiles) return (r, false); // --force: re-observe, still re-mark
        bool done = plan.DecomposerSourceId == default
            ? await plan.Reader.HasSourceCompletedAsync(r, plan.LayerOrder, ct).ConfigureAwait(false)
            : await plan.Reader.HasFileCompletedAsync(
                r, plan.DecomposerSourceId, plan.LayerOrder, ct).ConfigureAwait(false);
        return (r, done);
    }

    private static async IAsyncEnumerable<SubstrateChange> RunMultiFileSequentialAsync<TRecord>(
        IMultiFileRecordStream<TRecord> stream,
        Func<string, IIngestRecordHandler<TRecord>> handlerFactory,
        Func<string, IngestBatchConfig> configFactory,
        long maxTotalUnits,
        bool isolateFileFailures,
        PerFileResumePlan? resume,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long unitsConsumed = 0;
        await foreach (var source in stream.FilesAsync(ct))
        {
            string label = source.FileLabel;
            var handler = handlerFactory(label);
            var config = configFactory(label);

            var (fileRoot, skipComplete) = await ResolveResumeAsync(resume, source.FilePath, ct)
                .ConfigureAwait(false);
            if (skipComplete)
            {
                Console.Error.WriteLine($"INGEST_FILE_SKIPPED file={label} reason=marker-complete");
                Laplace.Ingestion.IngestObservabilityScope.Current.OnFileComposed(
                    Laplace.Ingestion.IngestObservabilityScope.SourceName, label,
                    resumeFingerprint: fileRoot);
                // Still a true skip -- zero rows, zero testimony, no re-fold -- but it must
                // COUNT. FilesTotal comes from enumeration and includes skipped files, while
                // _filesDone only advances when a period boundary reaches TrackIntent. A bare
                // `continue` therefore made every already-complete file look unfinished:
                // MEASURED 2026-08-10 on the document lane, 209 enumerated, 8 marker-complete,
                // files_done 201, and the run failed claiming "8 file(s) did not reach
                // completion; their content is absent from the substrate" -- the exact
                // opposite of the truth, since the content being PRESENT is what caused the
                // skip. BuildPeriodBoundary carries no fold semantics and the writer drops it
                // as an empty change, so counting costs nothing the skip was protecting.
                yield return BuildSkippedBoundary(config.SourceId, label);
                continue;
            }

            Laplace.Ingestion.IngestObservabilityScope.Current.OnFileStarted(
                Laplace.Ingestion.IngestObservabilityScope.SourceName, label, TryFileBytes(source.FilePath));

            // unitsConsumed is the RUN accumulator (it also enforces maxTotalUnits below), so
            // the per-file ledger has to difference it. Writing it straight through recorded a
            // cumulative total in every file row, which makes per-file reporting and resume
            // diagnostics read as monotonically growing regardless of file size. The parallel
            // path already differences correctly via fileUnits.
            long unitsAtFileStart = unitsConsumed;
            long fileCap = maxTotalUnits > 0 ? maxTotalUnits - unitsConsumed : 0;
            if (maxTotalUnits > 0 && fileCap <= 0)
                yield break;
            var runConfig = fileCap > 0 ? config.WithMaxInputUnits(fileCap) : config;
            bool hitCap = false;

            var changes = RunAsync(
                new AsyncEnumerableRecordStream<TRecord>(source.RecordsAsync(ct)), handler, runConfig, ct);
            var enumerator = changes.GetAsyncEnumerator(ct);
            SubstrateChange? fileFailure = null;
            long fileEntities = 0, filePhysicalities = 0, fileAttestations = 0;
            Hash128? semanticFileId = null;
            // Wall-clock throttle, not a record count: files differ by five orders of
            // magnitude in size (UD: 4,577 B to 360,217,466 B), so any per-N-records
            // trigger is either silent on the big ones or a flood on the small ones.
            long nextProgressAt = Environment.TickCount64 + FileProgressIntervalMs;
            try
            {
                while (true)
                {
                    SubstrateChange change;
                    try
                    {
                        if (!await enumerator.MoveNextAsync()) break;
                        change = enumerator.Current;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (isolateFileFailures)
                    {
                        Console.Error.WriteLine(
                            $"INGEST_FILE_FAILED file={label} error=[{ex.GetType().Name}] {ex.Message}");
                        fileFailure = BuildFileFailure(config.SourceId, label, ex);
                        break;
                    }
                    unitsConsumed += change.Metadata.InputUnitsConsumed;
                    CaptureSemanticFileId(ref semanticFileId, change.Metadata.FileId, label);
                    var rowCounts = CountRows(change);
                    fileEntities += rowCounts.Entities;
                    filePhysicalities += rowCounts.Physicalities;
                    fileAttestations += rowCounts.Attestations;
                    if (Environment.TickCount64 >= nextProgressAt)
                    {
                        nextProgressAt = Environment.TickCount64 + FileProgressIntervalMs;
                        Laplace.Ingestion.IngestObservabilityScope.Current.OnFileProgress(
                            Laplace.Ingestion.IngestObservabilityScope.SourceName, label,
                            unitsConsumed - unitsAtFileStart,
                            fileEntities, filePhysicalities, fileAttestations);
                    }
                    yield return BindFileLabel(change, label);
                    if (maxTotalUnits > 0 && unitsConsumed >= maxTotalUnits)
                    {
                        hitCap = true;
                        break;
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            // A failed file emits its failure marker INSTEAD of a boundary: it is
            // neither counted done nor marked complete, so a re-run retries exactly it.
            // A capped file stopped mid-stream, which is the SAME resume situation as
            // a kill — it must not be marked complete either.
            // Ledger before the boundary: a capped file stopped mid-stream and is NOT
            // complete, which is the same resume situation as a kill — recording it 'ok'
            // would claim a completeness the marker itself refuses to assert.
            Laplace.Ingestion.IngestObservabilityScope.Current.OnFileComposed(
                Laplace.Ingestion.IngestObservabilityScope.SourceName, label, semanticFileId,
                records: unitsConsumed - unitsAtFileStart,
                entities: fileEntities,
                physicalities: filePhysicalities,
                attestations: fileAttestations,
                resumeFingerprint: fileRoot);

            yield return fileFailure
                ?? (fileRoot is { } fr && !hitCap && resume is { } rp
                    ? BuildFileCompletion(config.SourceId, label, fr, rp.LayerOrder,
                        config.CanonicalNamesProvider?.Invoke())
                    : hitCap
                        ? BuildCancelledBoundary(config.SourceId, label)
                        : BuildPeriodBoundary(config.SourceId, label));

            if (hitCap)
                yield break;
        }
    }

}
