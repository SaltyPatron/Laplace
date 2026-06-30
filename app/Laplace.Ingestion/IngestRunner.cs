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
        _writer        = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader        = reader ?? throw new ArgumentNullException(nameof(reader));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _obs           = observability ?? NoOpObservability.Instance;
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

        // No ingest-order dependency. Identity is content-addressed, so a reference to
        // not-yet-ingested content is a forward reference that resolves when that content lands
        // (or is already present) — John 3:16 converges whether it arrives before or after the
        // whole Bible. T0 codepoints come from the perfcache FILE (client-side), not from
        // "ingesting unicode first". Sources may ingest in any order and concurrently with each
        // other; the old layer-ordering check was procedural thinking that contradicts the DAG.

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

        IngestTopology.EnsureReady();
        Laplace.Engine.Core.IntentStage.ResetContentBank();
        Laplace.Engine.Core.IntentStage.SetBulkFreshBypass(options.BulkFresh);

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

        int batchSize  = Math.Max(1, options.BatchSize);
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
        bool ShouldFlush(int intents, int rows) =>
            commitRows > 0
                ? (rows >= commitRows || intents >= batchSize)
                : intents >= batchSize;

        bool ShouldFlushWithCap(int intents, int rows) =>
            ShouldFlush(intents, rows) || intents >= maxIntentsPerCommit;

        // Referential integrity is no longer pre-checked, so every SubstrateChange is a
        // self-contained, independently-consistent batch and commit order across batches is
        // irrelevant (the consensus fold is commutative). Multi-worker runs therefore all use the
        // one bounded N-consumer lane.
        bool syncIngest = Environment.GetEnvironmentVariable("LAPLACE_INGEST_SYNC") == "1";
        if (syncIngest)
        {
            CpuTopology.RequirePerformanceCorePin();
            // Fully synchronous: iterate the decomposer INLINE and apply each batch on the SAME thread —
            // NO producer Task, so compose and apply never overlap. Diagnostic/mitigation for the native
            // heap-corruption race (producer composing into laplace_core while a consumer applies into it
            // concurrently). Opt-in via LAPLACE_INGEST_SYNC=1; the default channel path is unchanged.
            var sbatch = new List<SubstrateChange>(batchSize);
            int sbatchRows = 0;
            await foreach (var intent in decomposer
                .DecomposeAsync(ctx, options.DecomposerOptions, ct).WithCancellation(ct))
            {
                Interlocked.Increment(ref counters._unitsProduced);
                long units = intent.Metadata.InputUnitsConsumed;
                if (units > 0) Interlocked.Add(ref counters._inputUnitsComposed, units);
                options.Progress?.Report(MakeProgress(counters));
                if (batchSize == 1 && commitRows == 0)
                {
                    await ProcessOneIntentAsync(intent, decomposer, options, rng,
                                                counters, failures, log, ct);
                    continue;
                }
                sbatch.Add(intent);
                sbatchRows += RowsOf(intent);
                if (ShouldFlushWithCap(sbatch.Count, sbatchRows))
                {
                    await ProcessBatchAsync(sbatch, decomposer, options, rng,
                                            counters, failures, log, ct);
                    sbatch.Clear();
                    sbatchRows = 0;
                }
            }
            if (sbatch.Count > 0)
                await ProcessBatchAsync(sbatch, decomposer, options, rng,
                                        counters, failures, log, ct);
        }
        else
        {
            // Single commit consumer; parallelism inside NpgsqlSubstrateWriter.ApplyManyAsync.
            int channelCap = sizing.DecomposeChannelCapacity;
            long rowBudget = sizing.RowBudget;
            long bufferedRows = 0;
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
                        while (Interlocked.Read(ref bufferedRows) + r > rowBudget
                               && Volatile.Read(ref bufferedRows) > 0)
                        {
                            await drained.WaitAsync(ct);
                        }
                        Interlocked.Add(ref bufferedRows, r);
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
                    try { drained.Release(); } catch (SemaphoreFullException) { }

                    if (batchSize == 1 && commitRows == 0)
                    {
                        await ProcessOneIntentAsync(intent, decomposer, options, rng,
                                                     counters, failures, log, ct);
                        continue;
                    }
                    batch.Add(intent);
                    batchRows += RowsOf(intent);
                    if (ShouldFlushWithCap(batch.Count, batchRows))
                    {
                        await ProcessBatchAsync(batch, decomposer, options, rng,
                                                counters, failures, log, ct);
                        batch.Clear();
                        batchRows = 0;
                    }
                }
            }
            if (batch.Count > 0)
                await ProcessBatchAsync(batch, decomposer, options, rng,
                                        counters, failures, log, ct);

            
            await producer;
        }

        unitsAttempted        = counters.UnitsAttempted;
        unitsApplied          = counters.UnitsApplied;
        unitsFailed           = counters.UnitsFailed;
        entitiesInserted      = counters.EntitiesInserted;
        physicalitiesInserted = counters.PhysicalitiesInserted;
        attestationsInserted  = counters.AttestationsInserted;
        totalRoundTrips       = counters.RoundTrips;

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
        // Anti-false-green: a source that reports input (units or files) but applied ZERO units did not
        // ingest — it's a grammar/format mismatch the pipeline silently swallowed (the ConceptNet /
        // Atomic2020 no-op: 0 rows parsed, status reported ok). Re-runs of an already-seeded source still
        // APPLY their units (dedup happens at the DB via ON CONFLICT), so UnitsApplied stays > 0 — this
        // only fires on a genuine "consumed nothing from a non-empty source".
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
        CancellationToken ct)
    {
        if (batch.Count == 0) return;

        // Count whole units, not partitioned sub-changes: the parallel path splits one unit's rows
        // across lanes, and only its representative sub-change has CountsAsUnit=true (whole units
        // elsewhere default to true, so unitCount == batch.Count on the non-parallel path).
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
                var apply = await _writer.ApplyManyAsync(batch, ct);

                Interlocked.Add(ref counters._unitsApplied,           unitCount);
                Interlocked.Add(ref counters._entitiesInserted,       apply.EntitiesInserted);
                Interlocked.Add(ref counters._physicalitiesInserted,  apply.PhysicalitiesInserted);
                Interlocked.Add(ref counters._attestationsInserted,   apply.AttestationsInserted);
                Interlocked.Add(ref counters._roundTrips,             apply.RoundTrips);

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
                    log.LogError(ex, "Fatal ingest error in batch of {Count} intents "
                        + "(first unit {Unit}); aborting run.",
                        batch.Count, batch[0].Metadata.SourceContentUnitName);
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
        const string periodBoundary = "period-boundary/";
        if (unit.StartsWith(periodBoundary, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref c._filesDone);
            c._currentFile = unit[periodBoundary.Length..];
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
        public long UnitsAttempted        => Interlocked.Read(ref _unitsAttempted);
        public long UnitsProduced         => Interlocked.Read(ref _unitsProduced);
        public long UnitsApplied          => Interlocked.Read(ref _unitsApplied);
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
