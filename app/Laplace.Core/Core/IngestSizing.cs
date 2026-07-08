namespace Laplace.Engine.Core;

public static class IngestSizing
{
    public const int TargetBytesPerBatch = 1 << 20;

    // Fallback only — real bytes/record comes from IngestSourceProfile.
    public const int DefaultEstBytesPerRecord = 512;

    public const int ApplyWavesPerCommit = 2;

    public const int MaxIntentsPerCommitCap = 8;

    /// <summary>
    /// Staged-byte estimate under-counts true resident cost ~2.5× (WorkingSetMode).
    /// </summary>
    public const double WorkingSetResidentSlack = 2.5;

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
        int workingBytes = profile.WorkingSetBytesPerRecord;

        long maxByBudget = (long)(budget / (workingBytes * WorkingSetResidentSlack));
        int budgetCap = (int)Math.Clamp(maxByBudget, recordBatch, int.MaxValue);

        int derived = recordBatch * applyPartitions * ApplyWavesPerCommit;
        int commit = Math.Min(derived, budgetCap);

        int floor = Math.Min(Math.Max(recordBatch, 1_024), budgetCap);
        return Math.Clamp(commit, floor, budgetCap);
    }

    public static int ResolveWorkingSetProbeInterval(
        int recordBatchSize, IngestSourceProfile profile) =>
        Math.Clamp(
            recordBatchSize * Math.Max(1, profile.EstComposeUnitsPerRecord),
            256,
            32_768);

    public static int ResolveProbeChunk(int recordBatchSize, int fileWorkers = 1) =>
        Math.Clamp(recordBatchSize / Math.Max(1, Math.Min(fileWorkers, 4)), 128, 2048);

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
