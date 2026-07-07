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
    /// Working-set byte budget: phys/32 capped at 2 GiB (matches WorkingSetMode).
    /// </summary>
    public static long ResolveWorkingSetBudgetBytes()
    {
        long phys = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return Math.Clamp(phys / 32, 512L << 20, 2_048L << 20);
    }

    public static Plan Resolve(
        int performanceCoreCount,
        int fileWorkers,
        int applyPartitions,
        int? recordBatchOverride = null,
        int? commitRowsOverride = null,
        IngestSourceProfile? profile = null,
        long? workingSetBudgetBytes = null)
    {
        profile ??= IngestSourceProfile.Default;

        int batch = recordBatchOverride
            ?? ResolveRecordBatch(performanceCoreCount, profile.EstBytesPerRecord);
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

    public static int ResolveRecordBatch(
        int performanceCoreCount, int estBytesPerRecord = DefaultEstBytesPerRecord)
    {
        int fromMem = TargetBytesPerBatch / Math.Max(1, estBytesPerRecord);

        if (performanceCoreCount <= 4)
            return Math.Clamp(fromMem / 2, 512, 2048);

        if (performanceCoreCount >= 16)
            return Math.Clamp(fromMem * 2, 2048, 8192);

        return Math.Clamp(fromMem, 1024, 4096);
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
            "ingest_sizing: record_batch={0} probe_chunk={1} commit_rows={2} "
            + "decompose_channel={3} file_channel={4} max_intents_per_commit={5} row_budget={6}",
            plan.RecordBatchSize,
            plan.ProbeChunkSize,
            plan.CommitRows,
            plan.DecomposeChannelCapacity,
            plan.FileWorkerChannelDepth,
            plan.MaxIntentsPerCommit,
            plan.RowBudget);
    }
}
