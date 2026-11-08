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
/// One enumerated artifact of a multi-file source, opened lazily: <see cref="RecordsAsync"/>
/// reads and parses inside the worker that claims it, never in the dispatcher, so parsing
/// runs in parallel across files and no file is materialized into a list.
/// </summary>
public interface IFileRecordSource<TRecord>
{
    string FileLabel { get; }
    IAsyncEnumerable<TRecord> RecordsAsync(CancellationToken ct = default);

    /// <summary>
    /// Filesystem path when the source is a plain file, else null (zip entries,
    /// synthesized streams). Per-file resume fingerprints the raw bytes, so a source
    /// without a path never resumes.
    /// </summary>
    string? FilePath => null;
}

public interface IMultiFileRecordStream<TRecord>
{
    /// <summary>
    /// Enumerates the source's artifacts as independently openable record sources, reading
    /// nothing; each worker opens and streams one through read, parse and compose. Finalized
    /// changes merge into one bounded stream that the shared applier persists in bulk. Files
    /// are order-independent because references resolve by content address.
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
/// Multi-file stream whose per-file reader is the single-file extractor: enumeration
/// yields paths only, and each worker calls <paramref name="extractFile"/> once for
/// its claimed path, the same function a one-file source uses.
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

/// <summary>File-pool dispatch order. Uncapped runs use longest-processing-time first, with
/// file bytes as the format-independent cost, so a large file never becomes a one-worker
/// tail. Capped runs keep the declared order so the admitted prefix is predictable.</summary>
internal static class MultiFileScheduler
{
    internal static IReadOnlyList<(string Path, string Label)> Schedule(
        IReadOnlyList<(string Path, string Label)> files, long maxTotalUnits)
    {
        if (files.Count <= 1 || maxTotalUnits > 0) return files;

        return files
            .Select(static f => (File: f, Bytes: EstimateBytes(f.Path)))
            .OrderByDescending(static f => f.Bytes)
            .ThenBy(static f => f.File.Path, StringComparer.Ordinal)
            .ThenBy(static f => f.File.Label, StringComparer.Ordinal)
            .Select(static f => f.File)
            .ToArray();
    }

    internal static long EstimateBytes(string? path)
    {
        if (path is null) return 1;
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 1; }
        catch (UnauthorizedAccessException) { return 1; }
    }

    /// <summary>
    /// Divides the compose pool across the first LPT wave: each claimed file gets one
    /// segment, and each spare compose worker goes to the file with the largest current
    /// cost per segment, so one large file among many still composes in parallel.
    /// </summary>
    internal static int[] PlanInitialSegments(
        IReadOnlyList<long> costs,
        int fileWorkers,
        int composeWorkers)
    {
        int active = Math.Min(costs.Count, Math.Max(1, fileWorkers));
        if (active == 0) return [];

        var widths = Enumerable.Repeat(1, active).ToArray();
        int spare = Math.Max(0, composeWorkers - active);
        while (spare-- > 0)
        {
            int best = 0;
            double bestRemaining = Math.Max(1L, costs[0]) / (double)widths[0];
            for (int i = 1; i < active; i++)
            {
                double remaining = Math.Max(1L, costs[i]) / (double)widths[i];
                if (remaining > bestRemaining)
                {
                    best = i;
                    bestRemaining = remaining;
                }
            }
            widths[best]++;
        }
        return widths;
    }
}

/// <summary>
/// Shared bounded fan-out for ingest work whose one input may yield many output records.
/// A caller supplies the work items and an async producer; worker concurrency, output
/// backpressure, cancellation and producer-failure propagation happen here.
/// </summary>
public static class ParallelIngestWork
{
    public static IAsyncEnumerable<TResult> RunAsync<TWork, TResult>(
        IReadOnlyList<TWork> work,
        int maxConcurrency,
        Func<TWork, CancellationToken, IAsyncEnumerable<TResult>> execute,
        CancellationToken ct = default)
        => RunAsync(work, maxConcurrency, execute, ct, releaseAbandoned: null);

    public static IAsyncEnumerable<TResult> RunAsync<TWork, TResult>(
        IReadOnlyList<TWork> work,
        int maxConcurrency,
        Func<TWork, CancellationToken, IAsyncEnumerable<TResult>> execute,
        CancellationToken ct,
        Action<TResult>? releaseAbandoned)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(Enumerate(work), maxConcurrency, execute, ct, releaseAbandoned);
    }

    /// <summary>
    /// Streaming-work overload for input generated lazily or too large to materialize, with
    /// the same concurrency, backpressure, abandonment cancellation and failure propagation.
    /// When supplied, releaseAbandoned owns results rejected by the channel or left queued
    /// after consumption stops. Results already yielded belong to the consumer.
    /// </summary>
    public static IAsyncEnumerable<TResult> RunAsync<TWork, TResult>(
        IAsyncEnumerable<TWork> work,
        int maxConcurrency,
        Func<TWork, CancellationToken, IAsyncEnumerable<TResult>> execute,
        CancellationToken ct = default)
        => RunAsync(work, maxConcurrency, execute, ct, releaseAbandoned: null);

    public static async IAsyncEnumerable<TResult> RunAsync<TWork, TResult>(
        IAsyncEnumerable<TWork> work,
        int maxConcurrency,
        Func<TWork, CancellationToken, IAsyncEnumerable<TResult>> execute,
        [EnumeratorCancellation] CancellationToken ct,
        Action<TResult>? releaseAbandoned)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(execute);
        if (maxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // One result slot per active producer overlaps production and consumption.
        var output = Channel.CreateBounded<TResult>(new BoundedChannelOptions(maxConcurrency)
        {
            SingleReader = true,
            SingleWriter = maxConcurrency == 1,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var workerFailures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        void RetainFailure(Exception failure)
        {
            if (!workerFailures.Any(known => ReferenceEquals(known, failure)
                    || known is AggregateException aggregate
                    && aggregate.Flatten().InnerExceptions.Any(inner => ReferenceEquals(inner, failure))))
                workerFailures.Enqueue(failure);
        }

        async Task<(Exception? Failure, bool ExpectedCancellation)> ProduceAsync()
        {
            Exception? failure = null;
            bool expectedCancellation = false;
            Task? workers = null;
            try
            {
                workers = Parallel.ForEachAsync(
                    work,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxConcurrency,
                        CancellationToken = stop.Token,
                    },
                    async (item, token) =>
                    {
                        try
                        {
                            await foreach (var result in execute(item, token)
                                               .WithCancellation(token).ConfigureAwait(false))
                            {
                                try { await output.Writer.WriteAsync(result, token).ConfigureAwait(false); }
                                catch
                                {
                                    // Until the channel accepts it, the worker
                                    // still owns this result.
                                    try { releaseAbandoned?.Invoke(result); }
                                    catch (Exception cause) { RetainFailure(cause); }
                                    throw;
                                }
                            }
                        }
                        catch (Exception cause)
                        {
                            // Parallel.ForEachAsync can return cancellation when
                            // a worker failed during consumer abandonment. Keep
                            // the actual worker cause before that conversion.
                            if (cause is not OperationCanceledException || !token.IsCancellationRequested)
                                RetainFailure(cause);
                            throw;
                        }
                    });
                await workers.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (workers?.Exception is { } allFailures)
                {
                    // Await exposes one exception; the task retains every
                    // concurrent worker and source-enumerator failure.
                    foreach (Exception cause in allFailures.Flatten().InnerExceptions)
                        if (cause is not OperationCanceledException cancelled
                            || !cancelled.CancellationToken.IsCancellationRequested)
                            RetainFailure(cause);
                }
                else if (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                    RetainFailure(ex);
                else
                {
                    failure = ex;
                    expectedCancellation = true;
                }
            }
            finally
            {
                Exception[] causes = workerFailures.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
                if (causes.Length > 0)
                {
                    failure = causes.Length == 1 ? causes[0]
                        : new AggregateException("Parallel ingest workers failed.", causes);
                    expectedCancellation = false;
                }
                output.Writer.TryComplete(failure);
            }
            return (failure, expectedCancellation);
        }

        var producer = ProduceAsync();
        Exception? readFailure = null;
        try
        {
            await using var results = output.Reader.ReadAllAsync(ct).GetAsyncEnumerator(ct);
            while (true)
            {
                bool next;
                try { next = await results.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception cause) { readFailure = cause; throw; }
                if (!next) break;
                yield return results.Current;
            }
        }
        finally
        {
            List<Exception>? failures = null;
            try { stop.Cancel(); }
            catch (Exception cause) { (failures ??= []).Add(cause); }
            try
            {
                var terminal = await producer.ConfigureAwait(false);
                if (terminal.Failure is not null && !terminal.ExpectedCancellation)
                    (failures ??= []).Add(terminal.Failure);
            }
            catch (Exception cause) { (failures ??= []).Add(cause); }
            // All workers have joined; no new result can race this drain.
            // Already-yielded results belong to the consumer. Pending results
            // use only the explicit ownership policy supplied by the caller.
            while (output.Reader.TryRead(out TResult? abandoned))
            {
                try { releaseAbandoned?.Invoke(abandoned); }
                catch (Exception cause) { (failures ??= []).Add(cause); }
            }
            if (failures is not null)
            {
                if (readFailure is not null) failures.Insert(0, readFailure);
                Exception[] causes = failures.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
                if (causes.Length == 1)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(causes[0]).Throw();
                throw new AggregateException("Parallel ingest teardown failed.", causes);
            }
        }
    }

    private static async IAsyncEnumerable<T> Enumerate<T>(
        IReadOnlyList<T> work,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < work.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return work[i];
        }
        await Task.CompletedTask;
    }
}

public interface IIngestDeferredUnit : IDisposable
{
    TierTree? TreeForBatchProbe { get; }

    /// <summary>
    /// Native/managed bytes retained by this unit but not visible in the change builder.
    /// Zero lets the pipeline measure exposed tier-tree capacity or use the source profile;
    /// a unit that hides its probe trees reports their allocation here.
    /// </summary>
    long ResidentBytes => 0;

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
/// Record whose content-addressed trunk root is known before compose. Entity presence
/// proves only that the identity exists, not that this record's physicalities,
/// occurrences and testimony were admitted; only <see cref="IIngestCompletionRecord"/>
/// can skip a record.
/// </summary>
public interface ITrunkRootRecord
{
    Hash128 TrunkRootId { get; }
}

/// <summary>
/// A record with an exact durable unit-completion receipt, written only after the whole
/// source unit is staged, in the same transaction as its testimony and consensus fold.
/// Without a receipt the record composes even when its trunk is present. Null: no
/// completion key; the record always composes.
/// </summary>
public interface IIngestCompletionRecord
{
    IngestUnitCompletionKey? Completion { get; }
}

public interface IIngestRecordHandler<TRecord>
{
    /// <summary>
    /// Whether creating deferred units does independent compose work worth spreading across
    /// the compose pool. Handlers whose work happens in the serial builder drain opt out.
    /// </summary>
    bool ParallelizeDeferredUnitCreation => true;

    IIngestDeferredUnit CreateDeferredUnit(TRecord record);

    void WalkWitness(TRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit);

    long UnitsPerRecord(TRecord record) => 1;

    /// <summary>Upper bound on durable rows emitted by this source record.</summary>
    long EstimatedOutputRows(TRecord record) => 1;
}

/// <summary>Handler with per-batch dedup state that must reset after each yielded change.</summary>
public interface IIngestBatchScopedHandler
{
    void ResetBatchState();
}

public sealed class IngestBatchConfig
{
    private static readonly Lazy<IngestSizing.SourcePlan> LazyDefaultSizing = new(
        () => IngestSizing.ResolveForSource(IngestSourceProfile.Default),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public required Hash128 SourceId { get; init; }
    public required string BatchLabelPrefix { get; init; }
    public int BatchSize { get; init; } = LazyDefaultSizing.Value.RecordBatchSize;
    public int ProbeChunkSize { get; init; } = LazyDefaultSizing.Value.ProbeChunkSize;
    public double WitnessWeight { get; init; } = 1.0;

    /// <summary>
    /// Working-set mode (06_Engineering_Ruleset.txt rule 8): one builder spans the record
    /// stream, the O(tiers) existence descent runs once per working set, and one
    /// SubstrateChange is yielded per working set unless the memory budget closes it
    /// early. BatchSize is ignored in this mode.
    /// </summary>
    public bool WorkingSet { get; init; }

    /// <summary>Records per O(tiers) existence interval in working-set mode.</summary>
    public int? WorkingSetProbeInterval { get; init; }

    /// <summary>
    /// Max records in one working set before descent, apply and yield. Sized by
    /// <see cref="IngestSizing.ResolveWorkingSetRecordCap"/> for the source profile, so the
    /// set closes before deferred tier trees exceed the memory budget even when
    /// <see cref="SubstrateChangeBuilder.StagedBytesEstimate"/> is still low.
    /// </summary>
    public int? WorkingSetRecordCap { get; init; }

    /// <summary>Per-source byte model for the working-set memory estimate valve.</summary>
    public IngestSourceProfile? WorkingSetProfile { get; init; }

    /// <summary>
    /// Working sets that may be resident concurrently in this process. Multi-file and
    /// segmented ingest set it from their fan-out, so each set gets a share of the one
    /// compose-memory envelope.
    /// </summary>
    public int ConcurrentWorkingSets { get; init; } = 1;
    internal Func<int>? ActiveWorkingSetCount { get; init; }
    internal int EffectiveConcurrentWorkingSets =>
        Math.Max(1, ActiveWorkingSetCount?.Invoke() ?? ConcurrentWorkingSets);

    internal int EffectiveWorkingSetRecordCap
    {
        get
        {
            int configured = WorkingSetRecordCap ?? int.MaxValue;
            if (!WorkingSet) return configured;
            long envelope = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(
                EffectiveConcurrentWorkingSets);
            return Math.Min(configured, IngestSizing.ResolveFlushEnvelopeRecordCap(
                WorkingSetProfile ?? IngestSourceProfile.Default, envelope));
        }
    }

    internal int EffectiveWorkingSetProbeInterval
    {
        get
        {
            int configured = WorkingSetProbeInterval ?? int.MaxValue;
            if (!WorkingSet) return configured;
            long envelope = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(
                EffectiveConcurrentWorkingSets);
            return Math.Min(configured, IngestSizing.ResolveWorkingSetProbeInterval(
                BatchSize, WorkingSetProfile ?? IngestSourceProfile.Default, envelope));
        }
    }
    public int CommitEpoch { get; init; }
    public ISubstrateReader? ContainmentReader { get; init; }
    public Action<long>? ReportUnits { get; init; }
    public long MaxInputUnits { get; init; }
    internal Func<IReadOnlyCollection<string>>? CanonicalNamesProvider { get; init; }

    /// <summary>
    /// Batch content surfaces and bulk-probe their resolved roots before materializing tier
    /// rows. Effective only when a containment reader is present.
    /// </summary>
    public bool EnableDeferredContentOnBuilder { get; init; } = true;

    public int? EntityCapacity { get; init; }
    public int? PhysicalityCapacity { get; init; }
    public int? AttestationCapacity { get; init; }
    public int MaxOutputRows { get; init; } = IngestSizing.ResolveApplyTransactionRows();

    public SubstrateChangeBuilder NewBuilder(int batchNumber)
    {
        var capacities = ResolveBuilderCapacities();
        var b = new SubstrateChangeBuilder(SourceId, $"{BatchLabelPrefix}/{batchNumber}", null,
            entityCapacity: capacities.Entities,
            physicalityCapacity: capacities.Physicalities,
            attestationCapacity: capacities.Attestations)
            .SetCommitEpoch(CommitEpoch);
        if (EnableDeferredContentOnBuilder && ContainmentReader is not null)
            b.EnableDeferredContent(ContainmentReader);
        // The presence oracle is set whenever a reader exists, independent of deferred
        // content: a composer that can ask "already present?" skips staging that subtree.
        b.SetPresenceOracle(ContainmentReader);
        return b;
    }

    internal (int Entities, int Physicalities, int Attestations) ResolveBuilderCapacities()
    {
        int batchRecords = Math.Max(1, BatchSize);
        // Capacity is an initial allocation, not the working-set limit: a working-set builder
        // starts at one record's modeled row width and grows as rows arrive, so many open
        // small files do not each reserve a full batch. The close limits remain
        // EffectiveWorkingSetRecordCap and the byte envelope.
        int residentRecords = WorkingSet ? 1 : batchRecords;

        return (
            ScaleInitialCapacity(EntityCapacity ?? batchRecords, batchRecords, residentRecords),
            ScaleInitialCapacity(PhysicalityCapacity ?? batchRecords, batchRecords, residentRecords),
            ScaleInitialCapacity(AttestationCapacity ?? SaturatingMultiply(batchRecords, 4), batchRecords, residentRecords));
    }

    private static int ScaleInitialCapacity(int capacity, int batchRecords, int residentRecords)
    {
        if (capacity <= 0)
            return 0;
        if (residentRecords >= batchRecords)
            return capacity;

        long scaled = ((long)capacity * residentRecords + batchRecords - 1L) / batchRecords;
        return (int)Math.Clamp(scaled, 1L, int.MaxValue);
    }

    private static int SaturatingMultiply(int value, int multiplier) =>
        (int)Math.Min((long)value * multiplier, int.MaxValue);

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
            MaxOutputRows = MaxOutputRows,
            WorkingSet = WorkingSet,
            WorkingSetProbeInterval = WorkingSetProbeInterval,
            WorkingSetRecordCap = WorkingSetRecordCap,
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
