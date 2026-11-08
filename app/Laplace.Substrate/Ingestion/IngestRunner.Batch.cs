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

        using var linked = new IngestCancellationScope(ct);

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


    private static long RowsOf(SubstrateChange c)
        {
            long rows = (long)c.Entities.Length + c.Physicalities.Length + c.Attestations.Length;
            if (!c.IntentStages.IsDefaultOrEmpty)
                foreach (var s in c.IntentStages)
                    rows += s.EntityCount + s.PhysicalityCount + s.AttestationCount;
            return rows;
        }

    private static long BytesOf(SubstrateChange c)
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
                // Commit grain covers the native allocations actually held. The serialized
                // E/P/A metric is unchanged, and a stage referenced more than once is not
                // counted twice.
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

        // CompleteFileAsync observes durability: under pipelined bulk ingest it waits for the
        // dispatched fold/mask continuation carrying this file's completion marker. It is not
        // awaited here, so the next working set's apply does not wait behind the fold.
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
        Exception? applyFailure = null;
        try
        {
            await ProcessOneIntentAsync(
                intent, decomposer, options, rng, counters, failures, log, ct);
        }
        catch (Exception failure) { applyFailure = failure; throw; }
        finally
        {
            try { intent.ApplyEnvelope?.Dispose(); }
            catch (Exception releaseFailure) when (applyFailure is not null)
            {
                throw new AggregateException("Ingest apply/source release failed.", applyFailure, releaseFailure);
            }
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

        // Native stages are disposed once the writer accepts them, so entity/physicality
        // closure is checked here while the staged rows exist. The tracker is idempotent
        // across a retry.
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
        Exception? applyFailure = null;
        try
        {
            await ProcessBatchAsync(
                batch, decomposer, options, rng, counters, failures, log, workingSet, ct);
        }
        catch (Exception failure) { applyFailure = failure; throw; }
        finally
        {
            SubstrateApplyEnvelope.Release(batch, applyFailure);
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

        // As in the single-intent path: check closure before the writer disposes the stages.
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

}
