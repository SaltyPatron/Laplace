using System.Text;

namespace Laplace.Engine.Core;

public static class IngestSizing
{
    public const int TargetBytesPerBatch = 1 << 20;

    // Fallback only — real bytes/record comes from IngestSourceProfile.
    public const int DefaultEstBytesPerRecord = 512;

    /// <summary>
    /// Records sampled by <see cref="MeasureBytesPerRecord"/>. Enough to average out a
    /// long tail without reading a meaningful fraction of a 20GB file.
    /// </summary>
    public const int RecordSampleCount = 4096;

    /// <summary>
    /// Widest believable mean record size. A sample that lands above this is not a
    /// measurement of records, it is a file that is not line-delimited the way the caller
    /// thinks — fall back rather than size a batch from it.
    /// </summary>
    public const int MaxCredibleBytesPerRecord = 1 << 20;

    /// <summary>
    /// MEASURE bytes/record from the file about to be read, instead of trusting the
    /// per-source constant declared in <c>IngestSourceProfile</c>.
    ///
    /// Those constants are estimates that nothing ever checked against a corpus, and they
    /// are wrong by enough to matter. MEASURED 2026-08-01 on
    /// /vault/Data/Wiktionary/raw-wiktextract-data.jsonl (20.4 GB): the mean over the
    /// first 20,000 records is 6,158 bytes. <c>IngestSourceProfile.Wiktionary</c> declares
    /// 12,000 — 1.95x too high.
    ///
    /// The error is in the slow direction, not the dangerous one:
    /// <see cref="ResolveRecordBatch"/> computes <c>TargetBytesPerBatch / estBytesPerRecord</c>,
    /// so an over-estimate halves the batch and doubles the round trips for the same
    /// corpus. Nothing fails; it just costs the whole run.
    ///
    /// Returns <paramref name="fallback"/> on any unreadable/empty/implausible input.
    /// Sizing must never be the thing that throws.
    /// </summary>
    public static int MeasureBytesPerRecord(
        string path,
        int sampleRecords = RecordSampleCount,
        int fallback = DefaultEstBytesPerRecord)
    {
        if (string.IsNullOrWhiteSpace(path) || sampleRecords <= 0) return fallback;
        try
        {
            if (!File.Exists(path)) return fallback;

            long bytes = 0;
            int records = 0;
            using var reader = new StreamReader(path);
            while (records < sampleRecords && reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;          // blank separators are not records
                bytes += Encoding.UTF8.GetByteCount(line) + 1;   // + the newline it cost
                records++;
            }

            if (records == 0 || bytes <= 0) return fallback;
            long mean = bytes / records;
            return mean is > 0 and <= MaxCredibleBytesPerRecord ? (int)mean : fallback;
        }
        catch (IOException) { return fallback; }
        catch (UnauthorizedAccessException) { return fallback; }
    }

    public const int ApplyWavesPerCommit = 2;

    public const int MaxIntentsPerCommitCap = 32;

    /// <summary>
    /// Staged-byte estimate under-counts true resident cost ~2.5× (WorkingSetMode).
    /// </summary>
    public const double WorkingSetResidentSlack = 2.5;

    /// <summary>
    /// Per-tuple byte estimate used by the ingest runner's apply gate
    /// (<c>IngestRunner.BytesOf</c>) for entities / physicalities / attestations.
    /// </summary>
    public const int ApplyTupleByteEstimate = 152;

    /// <summary>
    /// Extra apply-cost billed per attestation on top of staged/COPY tuple bytes.
    ///
    /// MUST stay 0 for chess-shaped traffic. MEASURED 2026-08-04: surcharge 2048
    /// shrunk applies to ~220k present merges each and cut committed rate
    /// ~63 g/s → ~21 g/s. Cause: present attestation ids are content-addressed and
    /// shared across games; one large apply collapses duplicate ids to a single
    /// <c>attestation_merge</c> with summed observations, while many small applies
    /// re-merge the same id once per apply — more total merge work, same merge
    /// rows/s (~10–25k/s). Wall-clock needs fewer applies (coalesce), not a
    /// tighter envelope. Speedup belongs in merge throughput / run-scoped fold,
    /// not BytesOf inflation.
    /// </summary>
    public const int AttestationApplySurchargeBytes = 0;

    /// <summary>
    /// Apply-gate byte estimate: staged/COPY bytes plus attestation merge surcharge
    /// so <see cref="MemoryTopology.WorkingSetFlushEnvelopeBytes"/> bounds merge work.
    /// </summary>
    public static long EstimateApplyGateBytes(
        int entityCount,
        int physicalityCount,
        int attestationCount,
        long trajectoryBytes,
        long intentStageTupleBytes,
        int intentStageAttestationCount)
    {
        long att = (long)attestationCount + intentStageAttestationCount;
        long bytes =
            ((long)entityCount + physicalityCount + attestationCount) * ApplyTupleByteEstimate
            + Math.Max(0, trajectoryBytes)
            + Math.Max(0, intentStageTupleBytes);
        if (att > 0)
            bytes += att * AttestationApplySurchargeBytes;
        return bytes;
    }

    public sealed record Plan(
        int RecordBatchSize,
        int ProbeChunkSize,
        int CommitRows,
        int DecomposeChannelCapacity,
        int FileWorkerChannelDepth,
        int MaxIntentsPerCommit,
        long RowBudget)
    {
        public int IntentsPerCommit =>
            CommitRows / Math.Max(1, RecordBatchSize) + 1;
    }

    /// <summary>
    /// Per-source ingest plan derived from Intel topology (P/E pools), RAM budget,
    /// and the source byte/compose model. Single entry point for pipeline config.
    /// </summary>
    public sealed record SourcePlan(
        long WorkingSetBudgetBytes,
        long TotalMemoryBytes,
        int RecordBatchSize,
        int CommitRows,
        int WorkingSetRecordCap,
        int WorkingSetProbeInterval,
        int ComposeWorkers,
        int FileWorkers,
        int CommitWorkers,
        int ApplyPartitions,
        int ProbeChunkSize,
        int DecomposeChannelCapacity,
        int MaxIntentsPerCommit,
        long RowBudget)
    {
        public void Log(string sourceLabel)
        {
            Console.Error.WriteLine(
                "ingest_source_sizing: source={0} budget_bytes={1} total_ram_bytes={2} "
                + "record_batch={3} commit_rows={4} ws_record_cap={5} ws_probe={6} "
                + "compose_workers={7} file_workers={8} commit_workers={9} apply_partitions={10} "
                + "probe_chunk={11} decompose_channel={12} max_intents={13} row_budget={14}",
                sourceLabel,
                WorkingSetBudgetBytes,
                TotalMemoryBytes,
                RecordBatchSize,
                CommitRows,
                WorkingSetRecordCap,
                WorkingSetProbeInterval,
                ComposeWorkers,
                FileWorkers,
                CommitWorkers,
                ApplyPartitions,
                ProbeChunkSize,
                DecomposeChannelCapacity,
                MaxIntentsPerCommit,
                RowBudget);
        }
    }

    public static long TotalPhysicalMemoryBytes() => MemoryTopology.TotalPhysicalBytes;

    /// <summary>
    /// Working-set apply byte budget — delegated to <see cref="MemoryTopology"/>, the single
    /// RAM authority. Derived from real physical memory and clamped to the hard COPY-buffer
    /// safety ceiling so a single-table apply buffer can never approach the 2 GiB int wall.
    /// The former inline phys/16 (~3 GiB on this box) was itself the source of the >2 GiB
    /// COPY overflow that aborted UD/ConceptNet/chess with committed=0.
    /// </summary>
    public static long ResolveWorkingSetBudgetBytes() => MemoryTopology.WorkingSetBudgetBytes;

    /// <summary>
    /// Compose-side flush envelope (resident-memory bound that closes a working set before
    /// its builder + content bank are reset) — delegated to <see cref="MemoryTopology"/>.
    /// Far below the apply COPY budget so compose flushes continuously and stays fast; see
    /// <see cref="MemoryTopology.WorkingSetFlushEnvelopeBytes"/> for the rationale.
    /// </summary>
    public static long ResolveWorkingSetFlushEnvelopeBytes(int concurrentWorkingSets = 1) =>
        Math.Max(1, MemoryTopology.WorkingSetFlushEnvelopeBytes
            / Math.Max(1, concurrentWorkingSets));

    /// <summary>
    /// Hard record ceiling for one working set derived from the flush envelope and the
    /// per-source byte model — the inverse of <see cref="EstimateWorkingSetBytes"/>, so it
    /// agrees with the byte-estimate close check. Bounds the set even when
    /// <see cref="SubstrateChangeBuilder.StagedBytesEstimate"/> under-reports resident cost.
    /// </summary>
    public static int ResolveFlushEnvelopeRecordCap(
        IngestSourceProfile profile, long? flushEnvelopeBytes = null)
    {
        long envelope = flushEnvelopeBytes ?? ResolveWorkingSetFlushEnvelopeBytes();
        double perRecord = Math.Max(1, profile.WorkingSetBytesPerRecord) * WorkingSetResidentSlack;
        long cap = (long)(envelope / perRecord);
        return (int)Math.Clamp(cap, 1, int.MaxValue);
    }

    /// <summary>
    /// Resolve a full per-source plan from live Intel topology + RAM. Call after
    /// <see cref="IngestTopology.EnsureReady"/> so worker pools are initialized.
    /// </summary>
    public static SourcePlan ResolveForSource(
        IngestSourceProfile profile,
        int? recordBatchOverride = null,
        long? workingSetBudgetBytes = null)
    {
        var topo = IngestTopology.Current;
        long budget = workingSetBudgetBytes ?? ResolveWorkingSetBudgetBytes();
        long ram = TotalPhysicalMemoryBytes();

        var plan = Resolve(
            topo.PerformanceCoreCount,
            topo.FileWorkers,
            topo.ApplyPartitions,
            recordBatchOverride: recordBatchOverride,
            profile: profile,
            workingSetBudgetBytes: budget,
            composeWorkers: topo.ComposeWorkers);

        int batch = plan.RecordBatchSize;
        return new SourcePlan(
            budget,
            ram,
            batch,
            plan.CommitRows,
            plan.CommitRows,
            ResolveWorkingSetProbeInterval(batch, profile),
            topo.ComposeWorkers,
            topo.FileWorkers,
            topo.CommitWorkers,
            topo.ApplyPartitions,
            plan.ProbeChunkSize,
            plan.DecomposeChannelCapacity,
            plan.MaxIntentsPerCommit,
            plan.RowBudget);
    }

    /// <summary>
    /// Max input records per working set before descent/apply — derived from the RAM budget
    /// and per-source staged-byte model (includes compose-unit multiplier + resident slack).
    /// </summary>
    public static int ResolveWorkingSetRecordCap(
        IngestSourceProfile profile, long? workingSetBudgetBytes = null) =>
        ResolveForSource(profile, workingSetBudgetBytes: workingSetBudgetBytes).WorkingSetRecordCap;

    /// <summary>
    /// Working-set memory estimate: staged builder bytes plus deferred compose trees
    /// (tier trees / grammar ASTs held in WorkingSetDeferredBatch that
    /// SubstrateChangeBuilder.StagedBytesEstimate does not count).
    /// </summary>
    public static long EstimateWorkingSetBytes(
        long recordsInSet, long stagedBuilderBytes, IngestSourceProfile profile) =>
        stagedBuilderBytes
        + (long)(recordsInSet * profile.WorkingSetBytesPerRecord * WorkingSetResidentSlack);

    public static Plan Resolve(
        int performanceCoreCount,
        int fileWorkers,
        int applyPartitions,
        int? recordBatchOverride = null,
        int? commitRowsOverride = null,
        IngestSourceProfile? profile = null,
        long? workingSetBudgetBytes = null,
        int composeWorkers = 1)
    {
        profile ??= IngestSourceProfile.Default;

        int batch = recordBatchOverride
            ?? ResolveRecordBatch(
                performanceCoreCount,
                profile.EstBytesPerRecord,
                profile.EstComposeUnitsPerRecord,
                composeWorkers,
                workingSetBudgetBytes);
        int probe = ResolveProbeChunk(batch, fileWorkers);

        int commit = commitRowsOverride
            ?? ResolveCommitRows(batch, applyPartitions, profile, workingSetBudgetBytes);

        int maxIntents = ResolveMaxIntentsPerCommit(batch, commit, commitRowsOverride);

        int decomposeChan = Math.Max(8, applyPartitions * 4 + fileWorkers);

        int slotsPerWorker = Math.Max(2,
            (applyPartitions * ApplyWavesPerCommit + fileWorkers - 1) / Math.Max(1, fileWorkers));
        int fileChan = fileWorkers * slotsPerWorker;

        long rowBudget = (long)Math.Max(commit, batch) * decomposeChan;

        return new Plan(batch, probe, commit, decomposeChan, fileChan, maxIntents, rowBudget);
    }

    /// <summary>
    /// Record batch from RAM budget, per-record bytes, P-core count, and compose parallelism.
    /// Cheap records (unicode) scale up; fat records (wiktionary, relation triples) scale down.
    /// </summary>
    public static int ResolveRecordBatch(
        int performanceCoreCount,
        int estBytesPerRecord = DefaultEstBytesPerRecord,
        int estComposeUnits = 1,
        int composeWorkers = 1,
        long? workingSetBudgetBytes = null)
    {
        long budget = workingSetBudgetBytes ?? ResolveWorkingSetBudgetBytes();
        int workingBytes = Math.Max(1, estBytesPerRecord) * Math.Max(1, estComposeUnits);

        int fromTarget = TargetBytesPerBatch / Math.Max(1, estBytesPerRecord);
        int fromMemory = (int)Math.Clamp(
            budget / (8L * workingBytes * Math.Max(1, composeWorkers)),
            256,
            32_768);

        int coreCeiling = performanceCoreCount switch
        {
            <= 4 => 2048,
            <= 8 => 4096,
            <= 16 => 8192,
            _ => 8192,
        };
        int coreFloor = performanceCoreCount <= 4 ? 512 : 1024;

        int raw = Math.Min(Math.Min(fromTarget, fromMemory), coreCeiling);
        // Only truly fat input units (chess games, documents) skip coreFloor.
        int batch = estBytesPerRecord > 256_000
            ? Math.Clamp(raw, 256, coreCeiling)
            : Math.Clamp(raw, coreFloor, coreCeiling);

        return batch;
    }

    /// <summary>
    /// Commit row budget from working-set RAM, capped by the pipeline-derived wave size.
    /// </summary>
    public static int ResolveCommitRows(
        int recordBatch,
        int applyPartitions,
        IngestSourceProfile profile,
        long? workingSetBudgetBytes = null)
    {
        long budget = workingSetBudgetBytes ?? ResolveWorkingSetBudgetBytes();
        // Fat input units (chess PGN games) use a large EstBytes solely so
        // ResolveRecordBatch takes the fat-batch lane. That same number must NOT
        // shrink commit_rows to ~6k — measured apply then pays ~80s/batch for
        // ~0.9M rows (~11k rows/s) and the 20s/250MB gate is impossible.
        // Commit sizing uses a staged-row estimate (~32 KiB/game working set).
        int workingBytes = profile.EstBytesPerRecord > 256_000
            ? 32_768 * Math.Max(1, profile.EstComposeUnitsPerRecord)
            : profile.WorkingSetBytesPerRecord;

        long maxByBudget = (long)(budget / (workingBytes * WorkingSetResidentSlack));
        int budgetCap = (int)Math.Clamp(maxByBudget, recordBatch, int.MaxValue);

        int derived = recordBatch * applyPartitions * ApplyWavesPerCommit;
        // Fat sources: do not cap commit at the tiny wave product (batch×parts×2).
        int commit = profile.EstBytesPerRecord > 256_000
            ? budgetCap
            : Math.Min(derived, budgetCap);

        int floor = Math.Min(Math.Max(recordBatch, 1_024), budgetCap);
        return Math.Clamp(commit, floor, budgetCap);
    }

    /// <summary>
    /// How many records accumulate in <c>pending</c> before
    /// <c>FlushPending</c> runs. MUST be ≤ the compose flush record cap: the close
    /// check reads <c>state.InBatch</c>, which only advances inside FlushPending.
    /// Wiktionary with EstComposeUnits=64 resolved probe=32768 (batch×units clamp)
    /// while flush recordCap≈516 — an 8k-line uncapped slice never FlushPending'd
    /// mid-stream and applied as intents=1 / ~281k entity verify (measured 2026-08-06).
    /// </summary>
    public static int ResolveWorkingSetProbeInterval(
        int recordBatchSize, IngestSourceProfile profile, long? flushEnvelopeBytes = null)
    {
        int flushCap = ResolveFlushEnvelopeRecordCap(profile, flushEnvelopeBytes);
        int raw = recordBatchSize * Math.Max(1, profile.EstComposeUnitsPerRecord);
        // Stay at or under flushCap so pending cannot outrun the envelope close.
        int capped = Math.Min(raw, flushCap);
        return Math.Clamp(capped, Math.Min(256, flushCap), flushCap);
    }

    // Presence probes are ROUND-TRIP dominated (~10ms fixed cost each, id
    // arrays are 16 bytes/id); the old [128, 2048] clamp turned big-source
    // descent into thousands of serial 512-id round trips — the tiny-codes /
    // Wiktionary "no progress, never finishes" signature (measured
    // 2026-07-16: continuous 10-12ms probe stream, zero writes). The WS-apply
    // probe already runs 131,072-id chunks through the same functions; match
    // its scale. 32k ids = 512KB parameter — noise.
    public static int ResolveProbeChunk(int recordBatchSize, int fileWorkers = 1) =>
        Math.Clamp(recordBatchSize * 16, 2048, 32_768);

    public static int ResolveMaxIntentsPerCommit(
        int recordBatch, int commitRowBudget, int? commitRowsOverride = null)
    {
        int budget = commitRowsOverride ?? commitRowBudget;

        if (budget <= 0)
            return Math.Max(1, recordBatch);

        int estRowsPerIntent = Math.Max(1, recordBatch * 8);
        int byRowBudget = Math.Max(1, budget / estRowsPerIntent);

        int heapCap = budget >= 100_000
            ? Math.Clamp(budget / 25_000, MaxIntentsPerCommitCap, 48)
            : MaxIntentsPerCommitCap;

        // Fat-record sources (chess) can resolve a commit_rows just above
        // recordBatch but below batch*8 — the estRowsPerIntent heuristic then
        // forces max_intents=1 and serializes apply on a multi-core box
        // (measured 2026-08-03: ChessPgn 4MiB estimate → commit_rows=429,
        // max_intents=1, ~50% of one core). When the budget clearly holds more
        // than one batch, allow parallel intents up to the heap cap.
        if (byRowBudget == 1 && budget >= recordBatch)
        {
            int byBatch = (budget + recordBatch - 1) / recordBatch;
            byRowBudget = Math.Min(heapCap, Math.Max(2, byBatch));
        }

        return Math.Max(1, Math.Min(byRowBudget, heapCap));
    }

    public static void LogPlan(Plan plan)
    {
        Console.Error.WriteLine(
            "ingest_sizing: total_ram_bytes={0} working_set_budget_bytes={1} record_batch={2} "
            + "probe_chunk={3} commit_rows={4} decompose_channel={5} file_channel={6} "
            + "max_intents_per_commit={7} row_budget={8}",
            TotalPhysicalMemoryBytes(),
            ResolveWorkingSetBudgetBytes(),
            plan.RecordBatchSize,
            plan.ProbeChunkSize,
            plan.CommitRows,
            plan.DecomposeChannelCapacity,
            plan.FileWorkerChannelDepth,
            plan.MaxIntentsPerCommit,
            plan.RowBudget);
    }
}
