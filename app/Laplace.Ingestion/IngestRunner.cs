using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// The shared orchestration loop per ADR 0052 — composes
/// <see cref="IDecomposer"/> + <see cref="ISubstrateWriter"/> into the
/// canonical per-source ingest recipe. Every per-source decomposer
/// (Unicode, ISO, WordNet, ...) ingests through this same RunAsync.
///
/// <para>
/// Cross-cutting concerns owned here: layer-ordering enforcement (ADR
/// 0037), checkpoint/resume, transient retry, parallel-worker variant,
/// progress reporting, structured logging, observability emission.
/// </para>
/// </summary>
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

    /// <summary>Run a decomposer end-to-end per ADR 0052.</summary>
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
        long unitsAttempted = 0, unitsApplied = 0, unitsSkipped = 0, unitsFailed = 0;
        long entitiesInserted = 0, physicalitiesInserted = 0, attestationsInserted = 0;
        long totalRoundTrips = 0;

        // 1. Layer-ordering prerequisite check (per ADR 0037).
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

        // 2. Build context + bootstrap.
        var ctx = new InternalContext(
            EcosystemPath: ResolveEcosystemPath(decomposer, options),
            Writer: _writer,
            Reader: _reader,
            Logger: _loggerFactory.CreateLogger($"Decomposer:{decomposer.SourceName}"),
            SubstrateVersion: "v1");

        await decomposer.InitializeAsync(ctx, ct);

        // 3. Estimate total units (best-effort).
        long? estimatedTotal = await decomposer.EstimateUnitCountAsync(ctx, ct);
        _obs.OnRunStart(decomposer.SourceName, decomposer.LayerOrder, estimatedTotal);

        // 4. Open / resume checkpoint journal.
        var checkpointPath = options.DecomposerOptions.CheckpointPath
                          ?? options.CheckpointPathOverride
                          ?? Path.Combine(ctx.EcosystemPath, "checkpoint.bin");
        await using var checkpoint = await CheckpointJournal.OpenOrCreateAsync(checkpointPath, ct);

        // 5. Iterate the decomposer's stream. Serial or parallel.
        var rng = new Random(unchecked((int)decomposer.SourceId.Lo));
        var counters = new RunCounters();

        if (options.ParallelWorkers <= 1)
        {
            await foreach (var intent in decomposer.DecomposeAsync(ctx, options.DecomposerOptions, ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();
                await ProcessOneIntentAsync(intent, decomposer, options, checkpoint, rng,
                                             counters, failures, log, ct);
            }
        }
        else
        {
            var channel = Channel.CreateBounded<SubstrateChange>(
                new BoundedChannelOptions(options.ParallelWorkers * 4)
                {
                    SingleWriter = true,
                    SingleReader = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });

            // Producer: read from decomposer, push into channel
            var producer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var intent in decomposer.DecomposeAsync(ctx, options.DecomposerOptions, ct)
                                                            .WithCancellation(ct))
                    {
                        await channel.Writer.WriteAsync(intent, ct);
                    }
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, ct);

            // Consumers: dequeue + apply concurrently
            var consumers = new Task[options.ParallelWorkers];
            for (int w = 0; w < options.ParallelWorkers; w++)
            {
                consumers[w] = Task.Run(async () =>
                {
                    var localRng = new Random(unchecked((int)decomposer.SourceId.Lo) ^ Environment.CurrentManagedThreadId);
                    while (await channel.Reader.WaitToReadAsync(ct))
                    {
                        while (channel.Reader.TryRead(out var intent))
                        {
                            await ProcessOneIntentAsync(intent, decomposer, options, checkpoint,
                                                        localRng, counters, failures, log, ct);
                        }
                    }
                }, ct);
            }
            await Task.WhenAll(producer);
            await Task.WhenAll(consumers);
        }

        unitsAttempted        = counters.UnitsAttempted;
        unitsApplied          = counters.UnitsApplied;
        unitsSkipped          = counters.UnitsSkipped;
        unitsFailed           = counters.UnitsFailed;
        entitiesInserted      = counters.EntitiesInserted;
        physicalitiesInserted = counters.PhysicalitiesInserted;
        attestationsInserted  = counters.AttestationsInserted;
        totalRoundTrips       = counters.RoundTrips;

        // 6. Final flush + summary.
        await checkpoint.FlushAsync(CancellationToken.None);
        sw.Stop();

        var result = new IngestRunResult(
            SourceId: decomposer.SourceId,
            SourceName: decomposer.SourceName,
            UnitsAttempted: unitsAttempted,
            UnitsApplied: unitsApplied,
            UnitsSkippedFromCheckpoint: unitsSkipped,
            UnitsFailed: unitsFailed,
            EntitiesInserted: entitiesInserted,
            PhysicalitiesInserted: physicalitiesInserted,
            AttestationsInserted: attestationsInserted,
            TotalRoundTrips: totalRoundTrips,
            WallClock: sw.Elapsed,
            Failures: failures);
        _obs.OnRunFinished(decomposer.SourceName, result);
        return result;
    }

    private async Task ProcessOneIntentAsync(
        SubstrateChange intent,
        IDecomposer decomposer,
        IngestRunOptions options,
        CheckpointJournal checkpoint,
        Random rng,
        RunCounters counters,
        List<IngestFailure> failures,
        ILogger log,
        CancellationToken ct)
    {
        Interlocked.Increment(ref counters._unitsAttempted);

        if (checkpoint.WasApplied(intent.Metadata.IntentId))
        {
            Interlocked.Increment(ref counters._unitsSkipped);
            _obs.OnIntentSkipped(decomposer.SourceName);
            options.Progress?.Report(MakeProgress(counters));
            return;
        }

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

                long appliedUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
                await checkpoint.AppendAsync(intent.Metadata.IntentId, appliedUs, CancellationToken.None);

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
                    // Fatal — record failure, surface, abort run.
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
                    await checkpoint.FlushAsync(CancellationToken.None);
                    log.LogError(ex, "Fatal ingest error on intent {IntentId} (unit {Unit}); aborting run.",
                        intent.Metadata.IntentId, intent.Metadata.SourceContentUnitName);
                    throw;
                }
                // Transient — loop will retry
                log.LogWarning(ex, "Transient ingest error on intent {IntentId} (attempt {Attempt}); will retry.",
                    intent.Metadata.IntentId, attempt + 1);
            }
        }

        // All retries exhausted
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

    private static string ResolveEcosystemPath(IDecomposer decomposer, IngestRunOptions options)
        => options.CheckpointPathOverride is not null
           ? Path.GetDirectoryName(options.CheckpointPathOverride) ?? "."
           : Path.Combine(Path.GetTempPath(), "laplace-ingest", decomposer.SourceName);

    private static IngestProgress MakeProgress(RunCounters c) =>
        new(c.UnitsAttempted, c.UnitsApplied, c.UnitsSkipped, c.UnitsFailed,
            null, TimeSpan.Zero);

    private sealed class RunCounters
    {
        internal long _unitsAttempted;
        internal long _unitsApplied;
        internal long _unitsSkipped;
        internal long _unitsFailed;
        internal long _entitiesInserted;
        internal long _physicalitiesInserted;
        internal long _attestationsInserted;
        internal long _roundTrips;
        public long UnitsAttempted        => Interlocked.Read(ref _unitsAttempted);
        public long UnitsApplied          => Interlocked.Read(ref _unitsApplied);
        public long UnitsSkipped          => Interlocked.Read(ref _unitsSkipped);
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

/// <summary>Thrown when <see cref="IngestRunner.RunAsync"/> is invoked
/// for a decomposer whose Layer N requires Layer 0..N-1 completion, but
/// some prerequisite layer hasn't completed.</summary>
public sealed class LayerOrderingViolationException : Exception
{
    public int DecomposerLayer { get; }
    public int MissingLayer { get; }
    public LayerOrderingViolationException(int decomposerLayer, int missingLayer)
        : base($"Layer {decomposerLayer} decomposer requires Layer {missingLayer} "
             + "to have completed at least once per ADR 0037.")
    {
        DecomposerLayer = decomposerLayer;
        MissingLayer = missingLayer;
    }
}
