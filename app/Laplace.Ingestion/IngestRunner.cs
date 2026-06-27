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

        
        
        
        
        
        
        
        // Referential integrity is no longer pre-checked, so every SubstrateChange is a
        // self-contained, independently-consistent batch and commit order across batches is
        // irrelevant (the consensus fold is commutative). Multi-worker runs therefore all use the
        // one bounded N-consumer lane.
        bool syncIngest = Environment.GetEnvironmentVariable("LAPLACE_INGEST_SYNC") == "1";
        if (syncIngest)
        {
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
                if (batchSize == 1 && commitRows == 0)
                {
                    await ProcessOneIntentAsync(intent, decomposer, options, rng,
                                                counters, failures, log, ct);
                    continue;
                }
                sbatch.Add(intent);
                sbatchRows += RowsOf(intent);
                if (ShouldFlush(sbatch.Count, sbatchRows))
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
        else if (options.ParallelWorkers <= 1)
        {
            
            
            
            
            
            
            
            
            
            
            
            
            
            long rowBudget = Math.Max((long)commitRows, batchSize) * 3L;
            long bufferedRows = 0;
            var drained = new SemaphoreSlim(0, 1);

            var channel = Channel.CreateUnbounded<SubstrateChange>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

            var producer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var intent in decomposer
                        .DecomposeAsync(ctx, options.DecomposerOptions, ct).WithCancellation(ct))
                    {
                        Interlocked.Increment(ref counters._unitsProduced);
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
            }, ct);

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
                    if (ShouldFlush(batch.Count, batchRows))
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
        else
        {
            await RunUnorderedParallelAsync(
                decomposer, ctx, options, batchSize, commitRows, ShouldFlush, RowsOf,
                counters, failures, log, rng, ct);
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
            + "rows_new={Ent}e+{Phys}p+{Att}a elapsed_s={Elapsed:F1} failed={Failed} status={Status}",
            decomposer.SourceName, decomposer.LayerOrder,
            counters.InputUnitsDone, declaredInput,
            counters.FilesDone, declaredFiles,
            result.UnitsApplied, result.UnitsAttempted,
            result.EntitiesInserted, result.PhysicalitiesInserted, result.AttestationsInserted,
            result.WallClock.TotalSeconds, result.UnitsFailed, status);
        _obs.OnRunFinished(decomposer.SourceName, result);
        if (emptySourceNoOp)
            throw new InvalidOperationException(
                $"{decomposer.SourceName}: source declares {declaredInput} input unit(s) / {declaredFiles} file(s) "
                + "but ingested 0 — grammar/format mismatch (silent no-op). Failing instead of reporting success. "
                + "Check the decomposer's modality/grammar matches the actual file format.");
        return result;
    }

    private async Task RunUnorderedParallelAsync(
        IDecomposer decomposer,
        InternalContext ctx,
        IngestRunOptions options,
        int batchSize,
        int commitRows,
        Func<int, int, bool> shouldFlush,
        Func<SubstrateChange, int> rowsOf,
        RunCounters counters,
        List<IngestFailure> failures,
        ILogger log,
        Random rng,
        CancellationToken ct)
    {
        // HEAVY COMPUTE IS PARALLEL; THE LIGHT MERGE IS PER-CONTENT-ID PARALLEL. The decomposer's
        // DecomposeAsync runs parse / compose / BLAKE3 / geometry / tier build in parallel and
        // yields already-computed SubstrateChanges. The commit is no longer a serial lane: the
        // writer (NpgsqlSubstrateWriter) splits each batch's staged rows NATIVELY by id.lo % N
        // (intent_stage_partition) into N disjoint partitions and COPYs+applies them on N
        // connections IN PARALLEL. Because a given content id lands in exactly one partition, the
        // set-based anti-join cannot collide cross-partition — no 23505, no ON CONFLICT, no retry.
        //
        // The id-partition lives in laplace_core precisely because a native IntentStage carries
        // many ids that managed code cannot split out of the opaque tuple blob; the old approach of
        // routing a whole intent to a worker by ONE representative id left the intent's OTHER novel
        // ids shared across two workers (the 23505 bug). Splitting PER ROW by each row's own id
        // fixes that by construction.
        //
        // The consumer here stays SINGLE so that two apply calls never run concurrently over an
        // overlapping id-space (a cross-batch collision the in-call partition cannot prevent): the
        // partition's parallelism is INSIDE one apply call, ordered by this one consumer. The heavy
        // commit work therefore fans out across N connections without a serial commit floor, while
        // the single consumer keeps the merge collision-proof. A fatal cancels the shared token so
        // the producer stops instead of deadlocking on a full channel.
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runCt = stopCts.Token;

        // Channel capacity: bound to a few commit batches so the parallel compose overlaps the
        // serial apply without unbounded native-IntentStage accumulation (the old 32K-deep channel
        // exhausted the native heap during stalls → -3 from laplace_grammar_compose). Multiplier is
        // commitRows/batchSize (SubstrateChange items per commit), not batchSize (rows per item).
        int intentsPerCommit = commitRows > 0
            ? commitRows / Math.Max(1, batchSize) + 1
            : batchSize;
        int channelCap = intentsPerCommit * 4 + 4;
        var channel = Channel.CreateBounded<SubstrateChange>(
            new BoundedChannelOptions(channelCap)
            {
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var intent in decomposer.DecomposeAsync(ctx, options.DecomposerOptions, runCt)
                                                        .WithCancellation(runCt))
                {
                    Interlocked.Increment(ref counters._unitsProduced);
                    await channel.Writer.WriteAsync(intent, runCt);
                }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, runCt);

        // PARALLEL COMMIT LANES — this is what kills the single consumer that ran the commit on one
        // thread (the 7.5%-CPU floor). The router splits every change's rows by id.lo % N (native
        // stages via IntentStage.Partition; managed rows + walks by id.lo % N) so a given content id is
        // only ever committed by ONE lane. Lanes run concurrently and their id-spaces are disjoint, so
        // the set-based apply can never collide cross-lane (no 23505) while the per-attestation
        // consensus accumulate AND the COPY/apply now run on N cores instead of one. Within a lane it
        // stays serial, so the same id is never applied twice at once. (Pair with LAPLACE_APPLY_PARTITIONS=1
        // so the writer does not re-partition an already-partitioned lane sub-change.)
        int lanes = Math.Max(1,
            int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_COMMIT_LANES"), out var cl) && cl > 0
                ? cl : CpuTopology.ResolveCpuBoundWorkers(headroom: 1, maxCap: 12));

        if (lanes == 1)
        {
            await DrainLaneAsync(channel.Reader, decomposer, options, counters, failures, log,
                                 batchSize, commitRows, shouldFlush, rowsOf, stopCts, runCt);
            await producer;
            return;
        }

        var laneChans = new Channel<SubstrateChange>[lanes];
        for (int k = 0; k < lanes; k++)
            laneChans[k] = Channel.CreateBounded<SubstrateChange>(
                new BoundedChannelOptions(channelCap)
                { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        var laneTasks = new Task[lanes];
        for (int k = 0; k < lanes; k++)
        {
            int lane = k;
            laneTasks[lane] = Task.Run(() => DrainLaneAsync(
                laneChans[lane].Reader, decomposer, options, counters, failures, log,
                batchSize, commitRows, shouldFlush, rowsOf, stopCts, runCt), runCt);
        }

        var router = Task.Run(async () =>
        {
            try
            {
                while (await channel.Reader.WaitToReadAsync(runCt))
                    while (channel.Reader.TryRead(out var c))
                    {
                        var subs = PartitionChange(c, lanes);
                        for (int k = 0; k < lanes; k++)
                            if (subs[k] is { } s)
                                await laneChans[k].Writer.WriteAsync(s, runCt);
                    }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopCts.Cancel();
                throw;
            }
            finally
            {
                for (int k = 0; k < lanes; k++) laneChans[k].Writer.TryComplete();
            }
        }, runCt);

        await Task.WhenAll(producer, router);
        await Task.WhenAll(laneTasks);
    }

    // One commit lane: drains its id-partitioned sub-changes, batches, and applies them serially on its
    // own connection path. N of these run concurrently over disjoint id-spaces.
    private async Task DrainLaneAsync(
        ChannelReader<SubstrateChange> reader,
        IDecomposer decomposer, IngestRunOptions options, RunCounters counters,
        List<IngestFailure> failures, ILogger log, int batchSize, int commitRows,
        Func<int, int, bool> shouldFlush, Func<SubstrateChange, int> rowsOf,
        CancellationTokenSource stopCts, CancellationToken runCt)
    {
        var localRng = new Random(unchecked((int)decomposer.SourceId.Lo));
        var batch = new List<SubstrateChange>(batchSize);
        int batchRows = 0;
        try
        {
            while (await reader.WaitToReadAsync(runCt))
                while (reader.TryRead(out var intent))
                {
                    if (batchSize == 1 && commitRows == 0)
                    {
                        await ProcessOneIntentAsync(intent, decomposer, options,
                                                    localRng, counters, failures, log, runCt);
                        continue;
                    }
                    batch.Add(intent);
                    batchRows += rowsOf(intent);
                    if (shouldFlush(batch.Count, batchRows))
                    {
                        await ProcessBatchAsync(batch, decomposer, options,
                                                localRng, counters, failures, log, runCt);
                        batch.Clear();
                        batchRows = 0;
                    }
                }
            if (batch.Count > 0)
                await ProcessBatchAsync(batch, decomposer, options,
                                        localRng, counters, failures, log, runCt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopCts.Cancel();
            throw;
        }
    }

    // Split one change into N id-disjoint sub-changes (lane k = rows with id.lo % N == k). Native
    // IntentStages are split by IntentStage.Partition (the same id.lo % N the apply uses); managed rows
    // and walks are bucketed by their own id. The source's native stages are consumed (disposed) here;
    // each lane's partition handles are disposed by the writer when applied. Lanes with no rows are null.
    private static SubstrateChange[] PartitionChange(SubstrateChange c, int n)
    {
        var result = new SubstrateChange[n];
        var ents = new List<EntityRow>[n];
        var phys = new List<PhysicalityRow>[n];
        var atts = new List<AttestationRow>[n];
        var stageParts = new List<IntentStage>[n];
        List<TestimonyWalkRow>[]? walks = c.TestimonyWalks.IsDefaultOrEmpty ? null : new List<TestimonyWalkRow>[n];
        for (int k = 0; k < n; k++)
        {
            ents[k] = new List<EntityRow>();
            phys[k] = new List<PhysicalityRow>();
            atts[k] = new List<AttestationRow>();
            stageParts[k] = new List<IntentStage>();
            if (walks != null) walks[k] = new List<TestimonyWalkRow>();
        }

        foreach (var e in c.Entities) ents[(int)(e.Id.Lo % (ulong)n)].Add(e);
        foreach (var p in c.Physicalities) phys[(int)(p.Id.Lo % (ulong)n)].Add(p);
        foreach (var a in c.Attestations) atts[(int)(a.Id.Lo % (ulong)n)].Add(a);
        if (walks != null)
            foreach (var w in c.TestimonyWalks) walks[(int)(w.Subject.Lo % (ulong)n)].Add(w);

        if (!c.IntentStages.IsDefaultOrEmpty)
            foreach (var st in c.IntentStages)
            {
                if (st.IsInvalid) continue;
                var split = st.Partition(n);
                for (int k = 0; k < n; k++) stageParts[k].Add(split[k]);
                st.Dispose();
            }

        // Exactly one non-empty partition carries the unit's identity for run-metric accounting, so a
        // unit is counted once (not once per lane its rows scatter to). The rest are CountsAsUnit=false.
        bool unitCounted = false;
        for (int k = 0; k < n; k++)
        {
            bool any = ents[k].Count > 0 || phys[k].Count > 0 || atts[k].Count > 0
                       || stageParts[k].Count > 0 || (walks != null && walks[k].Count > 0);
            if (!any)
            {
                foreach (var s in stageParts[k]) s.Dispose();
                result[k] = null!;
                continue;
            }
            bool isRepresentative = !unitCounted;
            unitCounted = true;
            result[k] = c with
            {
                Entities = ents[k].ToImmutableArray(),
                Physicalities = phys[k].ToImmutableArray(),
                Attestations = atts[k].ToImmutableArray(),
                IntentStages = stageParts[k].ToImmutableArray(),
                TestimonyWalks = walks != null ? walks[k].ToImmutableArray() : c.TestimonyWalks,
                CountsAsUnit = isRepresentative,
            };
        }
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
        if (decomposer is IIngestInventoryProvider provider)
        {
            var inv = await provider.DescribeInputAsync(ctx, options.DecomposerOptions, ct);
            if (inv is not null) return inv;
        }
        long? est = await decomposer.EstimateUnitCountAsync(ctx, ct);
        return est is long n ? IngestInventory.Single(n) : null;
    }

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
        if (consumed > 0)
            Interlocked.Add(ref c._inputUnitsDone, consumed);
        else
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
            c.UnitsProduced);
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
        internal int _filesDone;
        internal string? _currentFile;
        internal Stopwatch? Sw;
        internal string? SourceName;
        internal int LayerOrder;
        internal IngestInventory? Inventory;
        public long InputUnitsDone => Interlocked.Read(ref _inputUnitsDone);
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
