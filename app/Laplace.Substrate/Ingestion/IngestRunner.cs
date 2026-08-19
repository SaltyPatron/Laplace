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
    /// <summary>
    /// ContentLadderLedger is process-static; wipe it when the source changes so one
    /// source's deposited roots cannot suppress another source's first witness.
    /// Same-source warm re-ingest keeps membership across End/Begin.
    /// </summary>
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

        // SIGTERM MUST REACH THE CATCH BELOW. The cancellation arm already journals a
        // terminal row -- but only if the token is cancelled. A raw SIGTERM terminates
        // the process outright, so RunCoreAsync never unwinds, OnRunFinished never runs,
        // and the row sits at 'running' with no process behind it.
        //
        // That is not hypothetical and it is not rare: GitHub Actions cancels a job by
        // SIGTERMing the job's processes ("Terminate orphan process: pid (N) (dotnet)"),
        // and laplace.yml states that rebuilds preempt seeds BY DESIGN on the strength of
        // "a preempted seed loses nothing and re-runs cleanly". It does not. MEASURED
        // 2026-08-10: five PR merges inside 25 minutes preempted a ChessPgn seed at
        // 19:02; 6,649,061 entities and 17,337,962 attestations went in with no terminal
        // record, the row stranded at 'running', and wait-for-quiet-substrate.sh then
        // blocked the very deploy that had caused the preemption -- for its full budget,
        // waiting on an ingest that was already dead.
        //
        // Console.CancelKeyPress does NOT cover this. It surfaces SIGINT only; SIGTERM
        // never reaches it, which is exactly why the chess lane's handler did not help.
        //
        // ctx.Cancel = true suppresses the default terminate so the token cancellation
        // can unwind normally. Actions escalates to SIGKILL after its grace period, so
        // the terminal write must be cheap -- it is one UPDATE on one row by primary key.
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

        // Abnormal exits still journal a terminal status: a run cut off by cancellation or
        // a fatal error must be distinguishable from one that never ran (the run-journal
        // row would otherwise sit at 'running' forever with no explanation attached).
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

    private async Task<IngestRunResult> RunCoreAsync(
        IDecomposer decomposer,
        IngestRunOptions options,
        CancellationToken ct)
    {

        var log = _loggerFactory.CreateLogger($"Ingest:{decomposer.SourceName}");
        var sw = Stopwatch.StartNew();
        var failures = new List<IngestFailure>();
        long unitsAttempted = 0, unitsApplied = 0, unitsFailed = 0;
        long entitiesInserted = 0, physicalitiesInserted = 0, attestationsInserted = 0;
        long totalRoundTrips = 0;








        // Per-file-completion sources skip the SOURCE-level guard: their idempotency is
        // per-file (marker-complete files true-skip in the existence gate before compose),
        // so a re-run is cheap AND new files in a completed directory still ingest.
        if (!options.SkipSourceCompletion
            && !decomposer.PerFileCompletion
            && await _reader.HasSourceCompletedAsync(decomposer.SourceId, decomposer.LayerOrder, ct))
        {
            log.LogInformation(
                "{Source}: already ingested (completion marker present) — short-circuiting; "
                + "a re-ingest would double-count testimony into consensus. "
                + "To re-run: per-source eviction first.",
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

        var ctx = new InternalContext(
            EcosystemPath: ResolveEcosystemPath(decomposer, options),
            Writer: _writer,
            Reader: _reader,
            Logger: _loggerFactory.CreateLogger($"Decomposer:{decomposer.SourceName}"),
            SubstrateVersion: "v1");

        await decomposer.InitializeAsync(ctx, ct);

        NativeRuntimeEnv.ApplyFromTopologyIfUnset();
        IngestTopology.EnsureReady();

        // Directory.Exists ALONE was reported as "exists", so every single-file ingest logged
        // exists=False on a file that was then read successfully — the line immediately after
        // it announced input_units for that same path. A run that opens with a false negative
        // about its own input teaches the reader to ignore the log.
        bool pathIsDir = Directory.Exists(ctx.EcosystemPath);
        bool pathIsFile = File.Exists(ctx.EcosystemPath);
        log.LogInformation(
            "INGEST_PATH source={Source} ecosystem_path={Path} exists={Exists} kind={Kind}",
            decomposer.SourceName, ctx.EcosystemPath, pathIsDir || pathIsFile,
            pathIsDir ? "dir" : pathIsFile ? "file" : "missing");

        var inventory = await ResolveInventoryAsync(decomposer, ctx, options, ct);
        _obs.OnRunStart(decomposer.SourceName, decomposer.LayerOrder, inventory);
        // The file boundary is inside the static IngestBatchPipeline, which is called
        // directly by every decomposer and is handed no observability. The run brackets
        // the ambient so those two sites can write per-file ledger rows without changing
        // 33 call sites. Disposed with the run below.
        using var obsScope = IngestObservabilityScope.Begin(_obs, decomposer.SourceName);
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

        int batchSize = Math.Max(1, options.BatchSize);
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
        static long BytesOf(SubstrateChange c)
        {
            // Trajectory payloads dwarf the fixed tuple estimate (a factor
            // deposit is tens-to-hundreds of MB in one row); count them or the
            // byte gates never fire and the working set buffers the whole run.
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
            }
            // Attestation merge surcharge lives in IngestSizing so the
            // MemoryTopology flush envelope closes on apply work (MEASURED
            // ChessPgn: 2.43M present merges / ~103s under a 152 B/att bill).
            return IngestSizing.EstimateApplyGateBytes(
                c.Entities.Length,
                c.Physicalities.Length,
                c.Attestations.Length,
                traj,
                stageBytes,
                stageAtt);
        }

        // Rule #8: the working set is the unit of write. Yielded changes
        // accumulate until the memory budget closes the set with ONE
        // journaled apply; batch/commit-row caps only govern the retired
        // per-batch lane (LAPLACE_WORKING_SET=0).
        bool workingSet = Laplace.Decomposers.Abstractions.WorkingSetMode.Enabled;
        long wsBytes = 0;
        bool ShouldFlush(int intents, int rows) =>
            commitRows > 0
                ? (rows >= commitRows || intents >= batchSize)
                : intents >= batchSize;

        // COMMIT GRANULARITY (2026-07-21). The apply gate used to be the 4 GiB
        // COPY-buffer CEILING alone, so a source whose whole output is smaller
        // than that composed the ENTIRE run into RAM and wrote once, at the end:
        // OMW showed composed=1.6M / committed=0 / files=0/1226 / round_trips=0
        // for its whole run, then one terminal COPY with compose stalled behind
        // it. The ceiling is a memory SAFETY bound, not a batching policy —
        // using it as the batch size is what globbed every source.
        //
        // Commit at the same granularity compose already closes at (the flush
        // envelope, RAM/64 <= 512 MiB), and at every file boundary. Same total
        // COPY volume, same O(partitions) round-trips per apply, ~8x more
        // applies of 1/8 the size: the loader stays busy, files=n/N advances
        // live, and a cancelled run keeps every committed file instead of
        // losing the whole source.
        long applyEnvelope = Math.Min(
            IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(),
            Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes);

        // Compose closes are memory-safety fragments, not database transaction units.
        // Multi-file sources divide the compose envelope across the active file pool;
        // a high-fan source can therefore yield very small changes (UD: about eight
        // sentences per worker close). Flushing after three such changes turned the
        // generic ten-file pool into 526 indexed applies for 13,672 sentences: 26.5
        // sentences/apply, with the database NVMe saturated by random presence/fold IO.
        //
        // Coalesce finalized fragments here. Their deferred trees have already been
        // drained and disposed, so the compose-memory reason for closing them no longer
        // applies. The byte and row caps remain the apply safety boundary; the file-
        // boundary floor below remains the durability/UI-progress boundary.
        const int workingSetApplyRowCap = 400_000;

        // A file boundary is a commit OPPORTUNITY, not a commit requirement
        // (2026-07-21). Flushing on EVERY boundary shreds a many-small-files
        // source: OMW's 1226 files each yielded one working-set change plus one
        // boundary, so every apply was "intents=2 rows=~1,200" paying 10-12 round
        // trips and running at 1.5-9k rows/s, against 23,498 rows/s for the one
        // apply in that run that actually reached the envelope
        // (intents=3 rows=90,426, 29 round trips). It also produced
        // "intents=1 rows=0" applies — a full apply cycle for a lone boundary
        // carrying nothing.
        //
        // So a boundary commits only once the batch is worth a COPY. Below the
        // floor it rides along and commits with the next group, which still
        // advances files=n/N live (in steps of several files) and still bounds
        // what a cancelled run loses. Per-FILE visibility does not depend on
        // this: INGEST_FILE_COMPOSED/COMMITTED name every file individually.
        long boundaryCommitFloor = applyEnvelope / 8;

        static bool IsPeriodBoundary(SubstrateChange c) =>
            c.Metadata.SourceContentUnitName.StartsWith(
                IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal);

        bool ShouldFlushWithCap(int intents, int rows) =>
            workingSet
                ? ShouldFlushWorkingSet(
                    wsBytes, rows, applyEnvelope, workingSetApplyRowCap)
                : ShouldFlush(intents, rows) || intents >= maxIntentsPerCommit;





        bool syncIngest = false;

        // The RUN is the index-cycle scope. Rebuilding an index scans the
        // whole live table, so per-apply cycling costs
        // O(applies × table size); bracketing the run drops once at the
        // first qualifying apply and rebuilds exactly once, in the finally
        // below. A crash before the finally is covered by the writer's
        // index-cycle journal (recovered at the next run's begin).
        if (_ladderSource != decomposer.SourceId)
        {
            ContentLadderLedger.Reset();
            _ladderSource = decomposer.SourceId;
        }
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runCt = runCts.Token;
        Exception? cappedFail = null;
        Task? cappedWatchdog = null;
        if (options.DecomposerOptions.MaxInputUnits > 0)
        {
            // Poll even when DecomposeAsync has not yielded — the 20k Wiktionary smoke
            // sat minutes at composed=0 with no ThrowIfCappedFailFast call sites hit.
            cappedWatchdog = Task.Run(async () =>
            {
                try
                {
                    while (!runCt.IsCancellationRequested)
                    {
                        await Task.Delay(250, runCt).ConfigureAwait(false);
                        ThrowIfCappedFailFast(options, counters);
                    }
                }
                catch (OperationCanceledException) when (runCt.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref cappedFail, ex);
                    try { runCts.Cancel(); } catch { /* already cancelled */ }
                }
            }, CancellationToken.None);
        }

        bool bulkRunStarted = false;
        try
        {
            await _writer.BeginBulkRunAsync(runCt);
            bulkRunStarted = true;

            if (syncIngest)
            {
                CpuTopology.RequirePerformanceCorePin();




                var sbatch = new List<SubstrateChange>(batchSize);
                int sbatchRows = 0;
                Hash128? sbatchSource = null;
                await foreach (var intent in decomposer
                    .DecomposeAsync(ctx, options.DecomposerOptions, runCt).WithCancellation(runCt))
                {
                    Interlocked.Increment(ref counters._unitsProduced);
                    long units = intent.Metadata.InputUnitsConsumed;
                    if (units > 0) Interlocked.Add(ref counters._inputUnitsComposed, units);
                    ThrowIfCappedFailFast(options, counters);
                    options.Progress?.Report(MakeProgress(counters));
                    if (!workingSet && batchSize == 1 && commitRows == 0)
                    {
                        await ProcessOneIntentAsync(intent, decomposer, options, rng,
                                                    counters, failures, log, runCt);
                        continue;
                    }
                    long sib = BytesOf(intent);
                    // A decomposer may orchestrate independent witness vendors. Preserve
                    // their stream order, but never coalesce them into one replay/eviction
                    // transaction: working-set ownership is exactly one SourceId.
                    if (workingSet && ShouldFlushWorkingSetSourceBoundary(
                            sbatchSource, intent.Metadata.SourceId))
                    {
                        await ProcessBatchAsync(sbatch, decomposer, options, rng,
                                                counters, failures, log, workingSet, runCt);
                        sbatch.Clear();
                        sbatchRows = 0;
                        wsBytes = 0;
                        sbatchSource = null;
                    }
                    // Flush BEFORE adding an intent that would push the accumulated COPY
                    // bytes past the budget, so a single apply never exceeds it. Adding then
                    // checking (below) let the crossing intent land first, so one apply could
                    // reach ~2× budget and build a single-table buffer near the 2 GiB wall.
                    if (workingSet && sbatch.Count > 0
                        && wsBytes + sib > Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes)
                    {
                        await ProcessBatchAsync(sbatch, decomposer, options, rng,
                                                counters, failures, log, workingSet, runCt);
                        sbatch.Clear();
                        sbatchRows = 0;
                        wsBytes = 0;
                        sbatchSource = null;
                    }
                    sbatch.Add(intent);
                    sbatchSource ??= intent.Metadata.SourceId;
                    sbatchRows += RowsOf(intent);
                    wsBytes += sib;
                    if (ShouldFlushWithCap(sbatch.Count, sbatchRows)
                        || (IsPeriodBoundary(intent) && wsBytes >= boundaryCommitFloor))
                    {
                        await ProcessBatchAsync(sbatch, decomposer, options, rng,
                                                counters, failures, log, workingSet, runCt);
                        sbatch.Clear();
                        sbatchRows = 0;
                        wsBytes = 0;
                        sbatchSource = null;
                    }
                }
                if (sbatch.Count > 0)
                    await ProcessBatchAsync(sbatch, decomposer, options, rng,
                                            counters, failures, log, workingSet, runCt);
            }
            else
            {

                int channelCap = sizing.DecomposeChannelCapacity;
                long rowBudget = sizing.RowBudget;
                long bufferedRows = 0;
                // Compose-ahead is bounded by BYTES as well as rows: with huge
                // trajectory rows the 58-intent channel alone can hold tens of
                // GB, so the row budget never constrains anything.
                long byteBudget = Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes;
                long bufferedBytes = 0;
                var drained = new SemaphoreSlim(0, channelCap);

                var channel = Channel.CreateBounded<SubstrateChange>(
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
                            .DecomposeAsync(ctx, options.DecomposerOptions, runCt).WithCancellation(runCt))
                        {
                            Interlocked.Increment(ref counters._unitsProduced);
                            long units = intent.Metadata.InputUnitsConsumed;
                            if (units > 0) Interlocked.Add(ref counters._inputUnitsComposed, units);
                            ThrowIfCappedFailFast(options, counters);
                            options.Progress?.Report(MakeProgress(counters));
                            int r = RowsOf(intent);
                            long b = BytesOf(intent);
                            while ((Interlocked.Read(ref bufferedRows) + r > rowBudget
                                    || Interlocked.Read(ref bufferedBytes) + b > byteBudget)
                                   && Volatile.Read(ref bufferedRows) > 0)
                            {
                                await drained.WaitAsync(producerCt);
                            }
                            Interlocked.Add(ref bufferedRows, r);
                            Interlocked.Add(ref bufferedBytes, b);
                            await channel.Writer.WriteAsync(intent, producerCt);
                        }
                        channel.Writer.TryComplete();
                    }
                    catch (Exception ex)
                    {
                        channel.Writer.TryComplete(ex);
                    }
                }, "ingest-decompose-pcore", runCt);

                // The writer retains its cross-process advisory transaction lock.
                // This lane only controls in-process apply workers; those remain
                // serial until claim-before-COPY is proven race-free.
                //
                // Cap by connection budget: each apply worker opens 1 control conn plus
                // up to ApplyParallelism COPY conns (and merge fans). Force-run on this
                // host (max_connections=60) with workers=8 × ~12 COPY blew 53300 too-many
                // clients, then left half-committed attestations and 23505 races.
                // Parallel apply under Deferred still 23505s on attestation PKs even with
                // claim dicts + conn budget (measured 2026-08-06 Wiktionary --force:
                // workers=2, attestations_r_has_definition_h1_pkey). Keep apply serial
                // until claim-before-COPY is proven under multi-writer; compose fan can
                // still run. Wrong parallelism is slower and corrupt.
                int applyWorkers = 1;
                if (workingSet
                    && Laplace.SubstrateCRUD.Npgsql.NpgsqlIndexCycle.Deferred
                    && options.ParallelWorkers > 1)
                {
                    log.LogInformation(
                        "INGEST_PARALLEL_APPLY disabled (serial apply); "
                        + "ParallelWorkers={W} ignored until attestation claim is race-free",
                        options.ParallelWorkers);
                }

                var applyChannel = applyWorkers > 1
                    ? Channel.CreateBounded<List<SubstrateChange>>(new BoundedChannelOptions(applyWorkers * 2)
                    {
                        SingleWriter = true,
                        SingleReader = false,
                        FullMode = BoundedChannelFullMode.Wait,
                    })
                    : null;

                Task[]? applyTasks = null;
                if (applyChannel is not null)
                {
                    applyTasks = new Task[applyWorkers];
                    for (int w = 0; w < applyWorkers; w++)
                    {
                        applyTasks[w] = Task.Run(async () =>
                        {
                            await foreach (var b in applyChannel.Reader.ReadAllAsync(runCt).ConfigureAwait(false))
                            {
                                await ProcessBatchAsync(b, decomposer, options, rng,
                                    counters, failures, log, workingSet, runCt).ConfigureAwait(false);
                            }
                        }, runCt);
                    }
                }

                async Task FlushBatchAsync(List<SubstrateChange> b)
                {
                    if (b.Count == 0) return;
                    if (applyChannel is null)
                    {
                        await ProcessBatchAsync(b, decomposer, options, rng,
                            counters, failures, log, workingSet, runCt).ConfigureAwait(false);
                        b.Clear();
                        return;
                    }
                    var copy = new List<SubstrateChange>(b);
                    b.Clear();
                    await applyChannel.Writer.WriteAsync(copy, runCt).ConfigureAwait(false);
                }

                var batch = new List<SubstrateChange>(batchSize);
                int batchRows = 0;
                Hash128? batchSource = null;
                while (await channel.Reader.WaitToReadAsync(runCt))
                {
                    while (channel.Reader.TryRead(out var intent))
                    {
                        runCt.ThrowIfCancellationRequested();
                        Interlocked.Add(ref bufferedRows, -RowsOf(intent));
                        Interlocked.Add(ref bufferedBytes, -BytesOf(intent));
                        try { drained.Release(); } catch (SemaphoreFullException) { }

                        if (!workingSet && batchSize == 1 && commitRows == 0)
                        {
                            await ProcessOneIntentAsync(intent, decomposer, options, rng,
                                                         counters, failures, log, runCt);
                            continue;
                        }
                        long ib = BytesOf(intent);
                        // Flush the ordered prefix when an orchestrator crosses into a
                        // different witness vendor. Grouping by SourceId would reorder
                        // testimony; carrying both forward would corrupt run ownership.
                        if (workingSet && ShouldFlushWorkingSetSourceBoundary(
                                batchSource, intent.Metadata.SourceId))
                        {
                            await FlushBatchAsync(batch);
                            batchRows = 0;
                            wsBytes = 0;
                            batchSource = null;
                        }
                        // Flush BEFORE adding an intent that would push accumulated COPY bytes
                        // past the budget, so a single working-set apply never exceeds it and
                        // no single-table buffer approaches the 2 GiB int wall.
                        if (workingSet && batch.Count > 0
                            && wsBytes + ib > Laplace.Decomposers.Abstractions.WorkingSetMode.BudgetBytes)
                        {
                            await FlushBatchAsync(batch);
                            batchRows = 0;
                            wsBytes = 0;
                            batchSource = null;
                        }
                        batch.Add(intent);
                        batchSource ??= intent.Metadata.SourceId;
                        batchRows += RowsOf(intent);
                        wsBytes += ib;
                        if (ShouldFlushWithCap(batch.Count, batchRows)
                            || (IsPeriodBoundary(intent) && wsBytes >= boundaryCommitFloor))
                        {
                            await FlushBatchAsync(batch);
                            batchRows = 0;
                            wsBytes = 0;
                            batchSource = null;
                        }
                    }
                }
                if (batch.Count > 0)
                    await FlushBatchAsync(batch);

                if (applyChannel is not null)
                {
                    applyChannel.Writer.TryComplete();
                    await Task.WhenAll(applyTasks!).ConfigureAwait(false);
                }

                await producer;
            }

            if (Volatile.Read(ref cappedFail) is { } cappedFailure)
                throw cappedFailure;
        }
        catch (OperationCanceledException) when (Volatile.Read(ref cappedFail) is not null)
        {
            throw Volatile.Read(ref cappedFail)!;
        }
        finally
        {
            try { runCts.Cancel(); } catch { /* ignore */ }
            if (cappedWatchdog is not null)
            {
                await cappedWatchdog.ConfigureAwait(false);
            }
            // CompleteBulkRun owns the one fold drain and the index rebuild. Rebuild after
            // every successfully opened run, including failures — a fatal
            // apply error must not leave the table index-less. The one
            // exception is cancellation: the user is tearing the process
            // down, so don't block exit on a minutes-scale rebuild — the
            // journal recovers the drops at the next run's begin.
            try
            {
                if (bulkRunStarted)
                    await _writer.CompleteBulkRunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                log.LogWarning(
                    "bulk-run index rebuild skipped (cancelled) — journaled "
                    + "drops will be recovered at the next run's begin");
            }
        }

        if (Volatile.Read(ref cappedFail) is { } terminalCappedFailure)
            throw terminalCappedFailure;

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
        int governedWithoutPhysicality = ValidateEntityAdmission(
            counters, log, enforceEntityAdmission);

        // An explicitly unknown record denominator is more honest than files disguised
        // as records. Once the stream is complete, its observed count becomes exact and
        // feeds the terminal journal/LapSight amplification record.
        if (inventory is { EffectiveTotalInputUnits: 0 } && counters.InputUnitsDone > 0)
            inventory.PublishExactTotal(counters.InputUnitsDone);

        long filesTotalForMarker = inventory?.FileCount ?? 0;
        bool filesComplete = filesTotalForMarker <= 0
            || counters.FilesDone == filesTotalForMarker;
        if (!options.SkipSourceCompletion
            && counters.UnitsFailed == 0
            && failures.Count == 0
            && counters.UnitsApplied > 0
            && options.DecomposerOptions.MaxInputUnits <= 0
            && filesComplete)
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
            GovernedIdentitiesWithoutPhysicality: governedWithoutPhysicality);





        long declaredInput = inventory?.EffectiveTotalInputUnits ?? 0;
        long declaredFiles = inventory?.FileCount ?? 0;
        bool emptySourceNoOp = result.UnitsApplied == 0 && (declaredInput > 0 || declaredFiles > 0);

        // An empty run the decomposer can ACCOUNT FOR is not the failure this guard is
        // for. See IIngestNoOpExplainer: idempotent re-ingest, a caught-up marker-gated
        // backfill, and an unset optional dependency all applied zero and all failed the
        // run. A decomposer that cannot explain itself still fails.
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
        // 'capped' = a MaxInputUnits smoke run: it succeeded but deliberately did not
        // ingest the whole source, which is also why it never mints a completion marker —
        // the run journal must not let it masquerade as a full 'ok'.
        // File-tracking lanes (TracksFileCompletion): files_done must equal files_total.
        // Failed files emit file-failed/ instead of period-boundary/, so they do not
        // inflate files_done; a cut-off or partial run cannot report status=ok
        // (CONSOLIDATION Q5 / FrameNet 33/14900).
        string status = explained is { } exp
            ? exp.Status
            : DeriveRunStatus(
                result.UnitsFailed,
                emptySourceNoOp,
                capped: options.DecomposerOptions.MaxInputUnits > 0,
                filesDone: counters.FilesDone,
                filesTotal: declaredFiles);
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
        string? failureReason = status == "failed"
            ? DescribeRunFailure(result.UnitsFailed, counters.FilesDone, declaredFiles)
            : null;
        _obs.OnRunFinished(decomposer.SourceName, result, status, failureReason);
        // A run that wrote status=failed to the ledger MUST NOT return normally. It used to:
        // the journal recorded failed, this method returned the result, the CLI saw no
        // exception and exited 0. MEASURED 2026-08-10 — `INGEST_TIMING source=document
        // elapsed_s=594 rc=0` over a row reading `failed, files 199/207`. Eight files'
        // content was absent from the substrate and every downstream step read green.
        // The empty-noop branch below already throws for exactly this reason; a partial
        // run is the same class of lie and gets the same treatment.
        if (status == "failed")
            throw new InvalidOperationException(
                $"{decomposer.SourceName}: ingest run recorded status=failed — "
                + (failureReason ?? "no reason derived")
                + ". Failing the process so the exit code matches the ledger.");
        // Zero-novel re-ingest did not add traffic — the default-partition scan is a
        // multi-second catalog read on a populated box and must not sit on the process
        // completion envelope after a no-op fold.
        // Partition-pressure scan walks consensus_rdefault; with secondaries down
        // under DEFER it is a multi-minute heap scan after INGEST_COMPLETE.
        if (result.EntitiesInserted + result.PhysicalitiesInserted + result.AttestationsInserted > 0
            && !Laplace.SubstrateCRUD.Npgsql.NpgsqlIndexCycle.Deferred)
            await ReportPartitionPressureAsync(log, ct);
        if (emptySourceNoOp)
            throw new InvalidOperationException(
                $"{decomposer.SourceName}: source declares {declaredInput} input unit(s) / {declaredFiles} file(s) "
                + "but ingested 0 — grammar/format mismatch (silent no-op). Failing instead of reporting success. "
                + "Check the decomposer's modality/grammar matches the actual file format.");
        return result;
    }

    /// <summary>
    /// Journal / INGEST_COMPLETE status. File-tracking sources cannot report <c>ok</c>
    /// when <paramref name="filesDone"/> is short of <paramref name="filesTotal"/> —
    /// that was the CONSOLIDATION Q5 lie (<c>FrameNet 33/14900 ok</c>).
    /// </summary>
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
        // Exact match: undercount was Q5; overcount (segment markers counted as files) is the same lie.
        if (filesTotal > 0 && filesDone != filesTotal) return "failed";
        return "ok";
    }

    /// <summary>
    /// Apply batching is governed by the finalized COPY payload, never by how many
    /// compose-memory fragments produced it. File workers deliberately close small
    /// fragments under high fan-out; using their count here reintroduces per-file-ish
    /// transactions and indexed probes into the shared generic apply lane.
    /// </summary>
    internal static bool ShouldFlushWorkingSet(
        long bufferedBytes, int bufferedRows, long byteCap, int rowCap) =>
        bufferedBytes >= byteCap || bufferedRows >= rowCap;

    /// <summary>
    /// A working-set apply is owned by one witness vendor. Composite decomposers may
    /// emit several vendors, but the runner must close the ordered prefix before the
    /// source changes so replay and source eviction retain an unambiguous owner.
    /// </summary>
    internal static bool ShouldFlushWorkingSetSourceBoundary(
        Hash128? bufferedSource, Hash128 nextSource) =>
        bufferedSource is { } source && source != nextSource;

    /// <summary>
    /// The operator-facing reason a run derived <c>failed</c>, written into
    /// <c>ingest_run_journal.error</c>. Without it the ledger says a run failed and nothing
    /// about why: the document lane recorded <c>failed, files_done=199/207, units_failed=0,
    /// error=NULL</c> twice on 2026-08-10, and the row was the only surviving artifact.
    /// </summary>
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

    /// <summary>
    /// Names any relation crowding the consensus DEFAULT partition, at the end of every run.
    ///
    /// WHY THIS IS A RUN-TIME REPORT AND NOT A CI GATE: the hot roster is a judgement about
    /// TRAFFIC, and traffic only exists on a populated database. CI can and does recreate
    /// laplace empty, so a fixture-backed gate would pass while the real box degrades. The
    /// ingest that generates the traffic is the only thing that reliably knows — so it is the
    /// thing that reports. MEASURED cost of not doing this: Tatoeba's HAS_EXTERNAL_ID and
    /// IS_TRANSLATION_OF reached 69% of consensus_rdefault (5.2 GB, one heap, one btree)
    /// before anyone noticed, and the only symptom was an ingest getting slower.
    ///
    /// It warns, it does not throw: a layout problem must not fail an otherwise-clean
    /// multi-hour ingest whose rows are all correctly recorded. Promotion is a manifest
    /// edit plus a codegen run — the partition seed adopts it in place, no reseed.
    /// </summary>
    private async Task ReportPartitionPressureAsync(ILogger log, CancellationToken ct)
    {
        // Floor, not a fraction: below this a relation cannot meaningfully crowd anything,
        // and a small/empty substrate reports nothing rather than noise.
        const long MinRows = 1_000_000;
        IReadOnlyList<PartitionPressure> pressure;
        try
        {
            pressure = await _reader.PartitionPressureAsync(MinRows, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;  // a diagnostic must never be the reason a finished run reports failure
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
                var apply = workingSet
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
                    TrackIntent(counters, intent, failures);

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

        // Per-file failure marker (file-failure isolation): the file's read/parse/compose
        // threw but the rest of the run continued. Count it as a failed unit WITH its
        // reason — it blocks the completion marker and drives run status to 'failed';
        // the file itself has no per-file marker, so a re-run retries exactly it.
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
                    + $"files={done}/{total} "
                    + $"run_elapsed_s={c.Sw?.Elapsed.TotalSeconds ?? 0:F0}");
            }
            return;
        }
        if (unit.StartsWith("layer-complete/", StringComparison.Ordinal)) return;

        // Operational boundary/marker entities above are runner scaffolding, not source
        // admission. Only the decomposer's semantic payload feeds the identity metric.
        c.EntityAdmission.Observe(intent);
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
            c.InputUnitsComposed);
    }

    /// <summary>
    /// Capped <see cref="DecomposerOptions.MaxInputUnits"/> runs are smoke gates, not seeds.
    /// They used to sit for minutes at composed=0/committed=0 with CommandTimeout=0 and no
    /// wall — legal under the old code, useless as a gate. Floor 3s; scale with cap at
    /// 7k input units/s (the Wiktionary 10-minute full-corpus bar).
    /// </summary>
    private static void ThrowIfCappedFailFast(IngestRunOptions options, RunCounters c)
    {
        long cap = options.DecomposerOptions.MaxInputUnits;
        if (cap <= 0) return;
        double sec = c.Sw?.Elapsed.TotalSeconds ?? 0;
        if (sec < 3.0) return;

        if (c.UnitsProduced == 0 && c.InputUnitsComposed == 0 && c.InputUnitsDone == 0)
        {
            throw new InvalidOperationException(
                $"INGEST_STALL_FAILFAST source={c.SourceName} MaxInputUnits={cap} "
                + $"elapsed_s={sec:F1} produced=0 composed=0 committed=0 — "
                + "capped smoke made no progress in 3s");
        }

        double wall = Math.Max(3.0, cap / 7000.0);
        if (sec <= wall) return;

        throw new InvalidOperationException(
            $"INGEST_WALL_FAILFAST source={c.SourceName} MaxInputUnits={cap} "
            + $"elapsed_s={sec:F1} wall_s={wall:F1} composed={c.InputUnitsComposed} "
            + $"committed={c.InputUnitsDone} — capped smoke exceeded wall");
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
        internal EntityAdmissionTracker EntityAdmission { get; } = new();
        internal Stopwatch? Sw;
        internal string? SourceName;
        internal int LayerOrder;
        internal IngestInventory? Inventory;
        public long InputUnitsDone => Interlocked.Read(ref _inputUnitsDone);
        public long InputUnitsComposed => Interlocked.Read(ref _inputUnitsComposed);
        public int FilesDone => Volatile.Read(ref _filesDone);
        public string? CurrentFile => Volatile.Read(ref _currentFile);
        public long UnitsAttempted => Interlocked.Read(ref _unitsAttempted);
        public long UnitsProduced => Interlocked.Read(ref _unitsProduced);
        public long UnitsApplied => Interlocked.Read(ref _unitsApplied);
        public long UnitsFailed => Interlocked.Read(ref _unitsFailed);
        public long EntitiesInserted => Interlocked.Read(ref _entitiesInserted);
        public long PhysicalitiesInserted => Interlocked.Read(ref _physicalitiesInserted);
        public long AttestationsInserted => Interlocked.Read(ref _attestationsInserted);
        public long RoundTrips => Interlocked.Read(ref _roundTrips);
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
