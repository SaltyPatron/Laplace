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

    /// <summary>
    /// Filesystem path when the source IS a plain file, else null (zip entries,
    /// synthesized streams). Per-file resume (GH #898) needs the raw bytes to mint
    /// the file's content identity; sources without a path simply never resume.
    /// </summary>
    string? FilePath => null;
}

public interface IMultiFileRecordStream<TRecord>
{
    /// <summary>
    /// The source's files as independently-openable record sources. Enumeration is CHEAP — it
    /// yields file handles/specs and reads NOTHING; each worker opens and streams ONE source
    /// through read + parse + compose. Finalized changes merge into the driver's bounded output
    /// stream and the shared applier coalesces them into bulk database transactions. Files are
    /// order-independent (references resolve content-addressed), so there is no ordering contract.
    /// </summary>
    IAsyncEnumerable<IFileRecordSource<TRecord>> FilesAsync(CancellationToken ct = default);
}

/// <summary>A file source whose reader is a lazy factory — the common "I have a path, open it on demand" case.</summary>
public sealed class DelegateFileRecordSource<TRecord>(
    string fileLabel, Func<CancellationToken, IAsyncEnumerable<TRecord>> open,
    string? filePath = null) : IFileRecordSource<TRecord>
{
    public string FileLabel => fileLabel;
    public IAsyncEnumerable<TRecord> RecordsAsync(CancellationToken ct = default) => open(ct);
    public string? FilePath => filePath;
}

/// <summary>
/// Multi-file stream whose per-file reader IS the single-file masticator.
/// Enumeration is cheap (paths only); each worker calls
/// <paramref name="extractFile"/> once for its claimed path — same function a
/// monolith source uses for its one file.
/// </summary>
public sealed class PathListMultiFileStream<TRecord>(
    IReadOnlyList<(string Path, string Label)> files,
    Func<string, string, DecomposerOptions, CancellationToken, IAsyncEnumerable<TRecord>> extractFile,
    DecomposerOptions options) : IMultiFileRecordStream<TRecord>
{
    public async IAsyncEnumerable<IFileRecordSource<TRecord>> FilesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (path, label) in files)
        {
            ct.ThrowIfCancellationRequested();
            string p = path;
            string l = label;
            yield return new DelegateFileRecordSource<TRecord>(
                l, token => extractFile(p, l, options, token), filePath: p);
        }
        await Task.CompletedTask;
    }
}

/// <summary>Generic file-pool dispatch order. Full-corpus runs use longest-processing-
/// time first with file bytes as the format-independent cost estimate; this prevents a
/// giant file discovered late from becoming a one-worker serial tail. Capped runs retain
/// the decomposer's declared order because their exact input prefix is operator-visible.</summary>
internal static class MultiFileScheduler
{
    internal static IReadOnlyList<(string Path, string Label)> Schedule(
        IReadOnlyList<(string Path, string Label)> files, long maxTotalUnits)
    {
        if (files.Count <= 1 || maxTotalUnits > 0) return files;

        return files
            .Select(static f => (File: f, Bytes: TryLength(f.Path)))
            .OrderByDescending(static f => f.Bytes)
            .ThenBy(static f => f.File.Path, StringComparer.Ordinal)
            .ThenBy(static f => f.File.Label, StringComparer.Ordinal)
            .Select(static f => f.File)
            .ToArray();
    }

    private static long TryLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
    }
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
    /// <summary>
    /// Whether creating deferred units performs independent compose work worth spreading
    /// across the process compose budget. Direct handlers do their real work during the
    /// later serial builder drain and opt out.
    /// </summary>
    bool ParallelizeDeferredUnitCreation => true;

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

    /// <summary>
    /// Working sets that may be resident concurrently in this process. Multi-file and
    /// segmented lanes set this from their actual fan-out so each set receives a share of
    /// the one compose-memory envelope instead of every set claiming the whole envelope.
    /// </summary>
    public int ConcurrentWorkingSets { get; init; } = 1;
    internal Func<int>? ActiveWorkingSetCount { get; init; }
    internal int EffectiveConcurrentWorkingSets =>
        Math.Max(1, ActiveWorkingSetCount?.Invoke() ?? ConcurrentWorkingSets);
    public int CommitEpoch { get; init; }
    public ISubstrateReader? ContainmentReader { get; init; }
    public Action<long>? ReportUnits { get; init; }
    public long MaxInputUnits { get; init; }
    internal Func<IReadOnlyCollection<string>>? CanonicalNamesProvider { get; init; }

    /// <summary>
    /// Batch content surfaces and bulk-probe their resolved roots before materializing
    /// tier rows. This keeps compose-time content checks on the indexed reader boundary.
    /// It is effective only when a containment reader is present.
    /// </summary>
    public bool EnableDeferredContentOnBuilder { get; init; } = true;

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
        // Presence oracle unconditionally when a reader exists — independent of deferred content,
        // which is a separate opt-in. A composer that can ask "already deposited?" can skip
        // STAGING a subtree instead of building it and having apply dedup it away.
        b.SetPresenceOracle(ContainmentReader);
        return b;
    }

    internal ISubstrateReader? EffectiveReader => ContainmentReader;

    private IngestBatchConfig Copy(
        long? maxInputUnits = null,
        int? concurrentWorkingSets = null,
        Func<int>? activeWorkingSetCount = null,
        Func<IReadOnlyCollection<string>>? canonicalNamesProvider = null) =>
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
            MaxInputUnits = maxInputUnits ?? MaxInputUnits,
            EnableDeferredContentOnBuilder = EnableDeferredContentOnBuilder,
            EntityCapacity = EntityCapacity,
            PhysicalityCapacity = PhysicalityCapacity,
            AttestationCapacity = AttestationCapacity,
            WorkingSet = WorkingSet,
            WorkingSetProbeInterval = concurrentWorkingSets is { } concurrency
                ? Math.Min(
                    WorkingSetProbeInterval ?? int.MaxValue,
                    IngestSizing.ResolveWorkingSetProbeInterval(
                        BatchSize,
                        WorkingSetProfile ?? IngestSourceProfile.Default,
                        IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(concurrency)))
                : WorkingSetProbeInterval,
            WorkingSetRecordCap = concurrentWorkingSets is { } concurrencyCap
                ? Math.Min(
                    WorkingSetRecordCap ?? int.MaxValue,
                    IngestSizing.ResolveFlushEnvelopeRecordCap(
                        WorkingSetProfile ?? IngestSourceProfile.Default,
                        IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(concurrencyCap)))
                : WorkingSetRecordCap,
            WorkingSetProfile = WorkingSetProfile,
            ConcurrentWorkingSets = concurrentWorkingSets ?? ConcurrentWorkingSets,
            ActiveWorkingSetCount = activeWorkingSetCount ?? ActiveWorkingSetCount,
            CanonicalNamesProvider = canonicalNamesProvider ?? CanonicalNamesProvider,
        };

    public IngestBatchConfig WithMaxInputUnits(long max) => Copy(maxInputUnits: max);

    public IngestBatchConfig WithWorkingSetConcurrency(int concurrentWorkingSets)
    {
        int concurrency = Math.Max(ConcurrentWorkingSets, Math.Max(1, concurrentWorkingSets));
        return concurrency == ConcurrentWorkingSets
            ? this
            : Copy(concurrentWorkingSets: concurrency);
    }

    internal IngestBatchConfig WithActiveWorkingSetConcurrency(
        int maximumConcurrentWorkingSets,
        Func<int> activeWorkingSetCount) =>
        Copy(
            concurrentWorkingSets: Math.Max(ConcurrentWorkingSets, maximumConcurrentWorkingSets),
            activeWorkingSetCount: activeWorkingSetCount);

    internal IngestBatchConfig WithCanonicalNamesProvider(
        Func<IReadOnlyCollection<string>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Copy(canonicalNamesProvider: provider);
    }
}

public static class IngestBatchPipeline
{
    public const string PeriodBoundaryUnitPrefix = "period-boundary/";
    public const string SkippedBoundaryUnitPrefix = PeriodBoundaryUnitPrefix + "skipped-complete/";
    public const string CancelledBoundaryUnitPrefix = PeriodBoundaryUnitPrefix + "cancelled/";

    /// <summary>Per-file failure marker (see IngestRunner.TrackIntent): emitted INSTEAD of the
    /// file's period boundary when file-failure isolation is on and that file's read/parse/
    /// compose threw. Zero rows, CountsAsUnit=false — it exists purely so the runner counts
    /// the failure with its reason and the file is neither counted done nor marked complete.</summary>
    public const string FileFailedUnitPrefix = "file-failed/";

    /// <summary>Ingest file-progress marker (see IngestRunner.TrackIntent). The fold is inline
    /// per batch (ConsensusAccumulatingWriter → consensus_upsert) — this marker carries no fold
    /// semantics; the writer skips it as an empty change.</summary>
    public static SubstrateChange BuildPeriodBoundary(Hash128 sourceId, string fileLabel) =>
        new SubstrateChangeBuilder(
            sourceId, $"{PeriodBoundaryUnitPrefix}{fileLabel}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0).Build();

    public static SubstrateChange BuildSkippedBoundary(Hash128 sourceId, string fileLabel) =>
        new SubstrateChangeBuilder(
            sourceId, $"{SkippedBoundaryUnitPrefix}{fileLabel}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0).Build();

    public static SubstrateChange BuildCancelledBoundary(Hash128 sourceId, string fileLabel) =>
        new SubstrateChangeBuilder(
            sourceId, $"{CancelledBoundaryUnitPrefix}{fileLabel}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0).Build();

    /// <summary>
    /// Per-file resume for multi-file sources (GH #898). A source-level completion
    /// marker writes only at run end, so a killed multi-hour seed used to restart
    /// from record zero and RE-FOLD everything already applied — testimony is not
    /// idempotent, so the restart inflated witness counts on the whole applied
    /// prefix. With this enabled, each finished file's boundary carries a
    /// HasLayerCompleted marker on the FILE's content identity, and a restart
    /// true-skips marker-complete files before opening them. Blast radius of a kill
    /// shrinks from the whole corpus to the one file that was mid-apply.
    /// </summary>
    public readonly record struct PerFileResumePlan(
        ISubstrateReader Reader,
        int LayerOrder,
        bool IgnoreCompletedFiles,
        Hash128 DecomposerSourceId = default)
    {
        /// <summary>
        /// Dispatcher-resolved (identity, already-complete) per file path, filled a chunk
        /// at a time so the marker probe costs one round trip per CHUNK instead of one per
        /// FILE. Absent entry = not yet resolved, and the worker falls back to the scalar
        /// path — never to "skip", so a miss can only cost time, never silently drop a file.
        /// </summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, (Hash128? Root, bool Skip)>? Resolved { get; init; }
    }

    /// <summary>
    /// Ceiling on how many files the dispatcher resolves per bulk marker probe.
    ///
    /// The chunk RAMPS from 1 to this value rather than starting here. A fixed chunk
    /// makes the first file wait for the whole chunk to be read and hashed, and
    /// TryResolveFileIdentity reads every byte -- MEASURED 2026-08-10: on 209 x 1 MB
    /// files (the document corpus shape, 204 files / 208 MB with a 27.6 MB Webster in
    /// it) time-to-first-file went 1.9 ms -> 506.7 ms, a 260x regression, because the
    /// entire corpus is under a 512 chunk and so gets read before any row is written.
    /// On 2000 x 64 KB it was 0.2 ms -> 104 ms, 504x.
    ///
    /// Ramping keeps both properties: the first file releases after ONE hash, and a
    /// 14,900-file corpus still reaches full batching after ~10 doublings (~30 probes
    /// total instead of 14,900).
    /// </summary>
    private const int ResumeProbeChunkMax = 512;

    private const int ResumeHashChunkBytes = 4 << 20;
    private static readonly Hash128 ResumeHashDomain =
        SubstrateCanonicalIds.OfVersioned("file-resume-fingerprint");

    /// <summary>Input size for the ledger. Best-effort: a stream-backed source has no path,
    /// and a missing file is the enumerator's problem, not the journal's.</summary>
    internal static long TryFileBytes(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return 0;
        try { return new FileInfo(filePath).Length; }
        catch { return 0; }
    }

    internal static Hash128? TryResolveFileIdentity(string? filePath)
    {
        if (filePath is null) return null;
        byte[]? buffer = null;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return null;

            long dataChunks = info.Length / ResumeHashChunkBytes
                + (info.Length % ResumeHashChunkBytes == 0 ? 0 : 1);
            var chunks = new List<Hash128>((int)Math.Min(1024, 2 + dataChunks))
            {
                ResumeHashDomain,
            };
            Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, info.Length);
            chunks.Add(Hash128.Blake3(lengthBytes));

            buffer = ArrayPool<byte>.Shared.Rent(ResumeHashChunkBytes);
            using var fs = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, FileOptions.SequentialScan);
            while (true)
            {
                int read = 0;
                while (read < ResumeHashChunkBytes)
                {
                    int n = fs.Read(buffer, read, ResumeHashChunkBytes - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read == 0) break;
                chunks.Add(Hash128.Blake3(buffer.AsSpan(0, read)));
                if (read < ResumeHashChunkBytes) break;
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
            return null; // unreadable/degenerate path: no resume identity, never a crash
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Boundary that ALSO deposits the file's completion marker (resume-enabled lanes).</summary>
    public static SubstrateChange BuildFileCompletion(
        Hash128 sourceId, string fileLabel, Hash128 fileRoot, int layerOrder,
        IReadOnlyCollection<string>? canonicalNames = null)
    {
        var builder = new SubstrateChangeBuilder(
            sourceId, $"{PeriodBoundaryUnitPrefix}{fileLabel}", null,
            entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: 1);
        Laplace.Ingestion.LayerCompletion.EmitFileMarker(
            builder, fileRoot, sourceId, layerOrder);
        var change = builder.Build();
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

    public static SubstrateChange BuildFileFailure(Hash128 sourceId, string fileLabel, Exception ex) =>
        new SubstrateChangeBuilder(
            sourceId, $"{FileFailedUnitPrefix}{fileLabel}: [{ex.GetType().Name}] {ex.Message}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0)
        .Build() with
        { CountsAsUnit = false };

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

        var state = new BatchState(config.NewBuilder(0));
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

            // pending.Count must count toward the envelope: InBatch only advances inside
            // FlushPending. A probeInterval larger than recordCap (Wiktionary units=64 →
            // probe 32768 vs cap ~516) otherwise held the whole stream until EOF.
            if (config.WorkingSet && pending.Count > 0
                && ShouldCloseWorkingSet(state, config, pending.Count))
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
                    Laplace.Ingestion.IngestObservabilityScope.SourceName, label, fileRoot);
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
                    var rowCounts = CountRows(change);
                    fileEntities += rowCounts.Entities;
                    filePhysicalities += rowCounts.Physicalities;
                    fileAttestations += rowCounts.Attestations;
                    yield return change;
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
                Laplace.Ingestion.IngestObservabilityScope.SourceName, label, fileRoot,
                records: unitsConsumed - unitsAtFileStart,
                entities: fileEntities,
                physicalities: filePhysicalities,
                attestations: fileAttestations);

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

    private static async IAsyncEnumerable<SubstrateChange> RunMultiFileParallelAsync<TRecord>(
        IMultiFileRecordStream<TRecord> stream,
        Func<string, IIngestRecordHandler<TRecord>> handlerFactory,
        Func<string, IngestBatchConfig> configFactory,
        int workers,
        bool isolateFileFailures,
        PerFileResumePlan? resume,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // The dispatcher enumerates FILE SOURCES — cheap handles, reading NOTHING — into a bounded
        // channel; N workers each claim one source and run OPEN + read + parse + compose +
        // working-set finalization. The expensive parse is therefore
        // parallel across files, and no file is ever slammed into a list — records stream straight
        // from the file reader into the working-set spine. Finalized changes cross outCh into the
        // driver's one coalescing apply lane; database apply itself fans out by table partitions.
        // Files are order-independent (references resolve content-addressed), so there is no
        // source-level phase/barrier — just the pool.
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
        int concurrentWorkingSets = exhaustedInPeek
            ? Math.Max(1, peeked.Count * segmentsPerFile)
            : Math.Max(1, workers);

        // Resolve the resume question a CHUNK at a time on the way into the channel: file
        // identities in parallel (pure CPU: read + BLAKE3), then ONE marker probe for the
        // whole chunk. The scalar path this replaces cost one round trip per file, which
        // on FrameNet was 14,900 of them and ~100% of the run's wall clock. Chunked rather
        // than whole-corpus so the stream stays lazy — nothing materializes 14,900 sources.
        var chunk = new List<IFileRecordSource<TRecord>>(ResumeProbeChunkMax);
        int chunkTarget = 1;   // ramps 1,2,4,...,ResumeProbeChunkMax
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
                using (var gate = new SemaphoreSlim(Math.Max(1, Math.Min(4, paths.Length))))
                {
                    var hashing = new Task[paths.Length];
                    for (int i = 0; i < paths.Length; i++)
                    {
                        int idx = i;
                        hashing[i] = Task.Run(async () =>
                        {
                            await gate.WaitAsync(ct).ConfigureAwait(false);
                            try { roots[idx] = TryResolveFileIdentity(paths[idx]); }
                            finally { gate.Release(); }
                        }, ct);
                    }
                    await Task.WhenAll(hashing).ConfigureAwait(false);
                }

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
            if (chunkTarget < ResumeProbeChunkMax) chunkTarget = Math.Min(ResumeProbeChunkMax, chunkTarget * 2);
        }

        var dispatcher = Task.Run(async () =>
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
                await fileEnumerator.DisposeAsync();
            }
        }, ct);

        // Approximate total from the peek when the stream fit in it; else 0 (sample-only).
        int knownFileTotal = exhaustedInPeek ? peeked.Count : 0;
        int fileOrdinal = 0;
        int activeFiles = 0;

        var workerTasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            // Captured OUTSIDE Task.Run. `for` does not give per-iteration capture the way
            // `foreach` does, so a `int workerId = w;` inside the lambda reads the one shared
            // loop variable at RUN time — racing the loop thread and usually yielding the exit
            // value for every worker. That silently made per-worker ingest telemetry useless.
            int workerId = w;
            workerTasks[w] = Task.Run(async () =>
            {
                await foreach (var source in sources.Reader.ReadAllAsync(ct))
                {
                    Interlocked.Increment(ref activeFiles);
                    try
                    {
                    var config = configFactory(source.FileLabel)
                        .WithActiveWorkingSetConcurrency(
                            concurrentWorkingSets,
                            () => Math.Max(1, Volatile.Read(ref activeFiles) * segmentsPerFile));

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
                            source.FileLabel, fileRoot);
                        // Counts, for the reason spelled out on the sequential path's skip:
                        // FilesTotal counts enumerated files, _filesDone counts boundaries, so
                        // dropping the boundary makes an already-complete file read as
                        // unfinished and fails the run. Boundary, not FileCompletion -- the
                        // marker this file already carries is what got us here; re-depositing
                        // it would be a write on a lane whose whole contract is zero rows.
                        await outCh.Writer.WriteAsync(
                            BuildSkippedBoundary(config.SourceId, source.FileLabel), ct);
                        continue;
                    }

                    var records = new AsyncEnumerableRecordStream<TRecord>(source.RecordsAsync(ct));
                    int segments = segmentsPerFile > 1
                        ? MonolithSegmenter.ResolveSegments(config, segmentsPerFile)
                        : 1;

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
                            MonolithSegmenter.ResolveChunkRecords(config),
                            source.FileLabel,
                            ct)
                        : RunAsync(records, handlerFactory(source.FileLabel), config, ct);

                    long fileUnits = 0;
                    long fileEntities = 0, filePhysicalities = 0, fileAttestations = 0;
                    SubstrateChange? fileFailure = null;
                    try
                    {
                        await foreach (var change in changes)
                        {
                            fileUnits += change.Metadata.InputUnitsConsumed;
                            var rowCounts = CountRows(change);
                            fileEntities += rowCounts.Entities;
                            filePhysicalities += rowCounts.Physicalities;
                            fileAttestations += rowCounts.Attestations;
                            await outCh.Writer.WriteAsync(change, ct);
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
                        fileRoot,
                        records: fileUnits,
                        entities: fileEntities,
                        physicalities: filePhysicalities,
                        attestations: fileAttestations);
                    // Publish compose accounting before the boundary becomes visible to the
                    // apply consumer. The runner terminalizes the file when that boundary is
                    // applied; reversing these two operations lets a fast consumer enqueue
                    // Finished before Composed and exposes a terminal zero-count row in the UI.
                    await outCh.Writer.WriteAsync(
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
                        Interlocked.Decrement(ref activeFiles);
                    }
                }
            }, ct);
        }

        _ = Task.Run(async () =>
        {
            try { await dispatcher; await Task.WhenAll(workerTasks); outCh.Writer.Complete(); }
            catch (Exception ex) { outCh.Writer.Complete(ex); }
        }, ct);

        await foreach (var change in outCh.Reader.ReadAllAsync(ct))
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
            config.ConcurrentWorkingSets);

        int recordCap = Math.Min(
            config.WorkingSetRecordCap ?? int.MaxValue,
            IngestSizing.ResolveFlushEnvelopeRecordCap(profile, envelope));
        // InBatch + still-buffered pending: pending is not in InBatch until FlushPending.
        int inFlight = state.InBatch + Math.Max(0, pendingCount);
        if (recordCap > 0 && inFlight >= recordCap)
            return true;

        long staged = state.Builder.StagedBytesEstimate;
        if (staged >= envelope)
            return true;

        if (inFlight > 0)
        {
            long est = IngestSizing.EstimateWorkingSetBytes(inFlight, staged, profile);
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
                batch, handler, reader, state.Builder, config, probedAbsent, ct).ConfigureAwait(false);
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

    private sealed class BatchState(SubstrateChangeBuilder builder)
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

        public void AddUnits(long units)
        {
            InBatch++;
            _rowsInBatch += units;
        }

        public void ResetBuilder(SubstrateChangeBuilder next) => Builder = next;

        public async Task<SubstrateChange> YieldBatchAsync(CancellationToken ct)
        {
            var change = await Builder.SetInputUnitsConsumed(_rowsInBatch).BuildAsync(ct);
            InBatch = 0;
            _rowsInBatch = 0;
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
