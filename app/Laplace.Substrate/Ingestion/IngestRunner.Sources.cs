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

    private static void ReportStagedEntityAdmission(
        RunCounters counters,
        ILogger log)
    {
        var pending = counters.EntityAdmission.SnapshotPendingContent();
        if (pending.Length == 0)
        {
            log.LogInformation(
                "INGEST_IDENTITY_ADMISSION source={Source} staged_unpaired=0 status=ok",
                counters.SourceName);
            return;
        }

        // Diagnostic only: an entity may already be placed by an earlier source or run. The
        // durable source-closure check below decides, and blocks completion when an entity
        // is truly unplaced.
        string examples = string.Join(", ", pending.Take(8).Select(static item =>
            $"{item.Id}:{item.TypeId}@{item.UnitName}"));
        log.LogWarning(
            "INGEST_IDENTITY_ADMISSION source={Source} staged_unpaired={Unplaced} "
            + "status=diagnostic detail={Examples}",
            counters.SourceName, pending.Length, examples);
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
        internal Exception? ApplyFailure { get; set; }

        public async ValueTask DisposeAsync()
        {
            List<Exception>? failures = null;
            // Cancellation callbacks, the producer and each source lease are
            // independent owners. A fault in one must not abandon the rest.
            try { cancellation.Cancel(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            foreach (var bucket in buckets.Values)
            {
                try { SubstrateApplyEnvelope.Release(bucket.Batch); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            while (reader.TryRead(out QueuedIntent abandoned))
            {
                try { abandoned.Intent.ApplyEnvelope?.Dispose(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            if (failures is null) return;
            if (ApplyFailure is not null) failures.Insert(0, ApplyFailure);
            Exception[] distinct = failures.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
            if (distinct.Length == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(distinct[0]).Throw();
            throw new AggregateException("Ingest apply/producer cleanup failed.", distinct);
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
            try
            {
                await _writer.CompleteBulkRunAsync(onPhase, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception completionFailure)
            {
                try { await _writer.AbortBulkRunAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception abortFailure)
                {
                    throw new AggregateException("Ingest bulk completion/abort failed.", completionFailure, abortFailure);
                }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await AbortAsync().ConfigureAwait(false);
        }

        public async Task AbortAsync()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            await _writer.AbortBulkRunAsync(CancellationToken.None)
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
