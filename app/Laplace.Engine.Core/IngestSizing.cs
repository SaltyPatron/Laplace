namespace Laplace.Engine.Core;

/// <summary>
/// Derives ingest batch depths from topology so compose, probe, channel, and commit sizes
/// stay matched to apply parallelism and a bounded native in-flight memory budget.
/// </summary>
public static class IngestSizing
{
    /// <summary>Target staged bytes per record batch (~1 MiB per compose lane).</summary>
    public const int TargetBytesPerBatch = 1 << 20;

    /// <summary>Conservative grammar-row byte estimate for batch sizing.</summary>
    public const int EstBytesPerRecord = 512;

    /// <summary>Apply waves buffered per flush (2 × apply_partitions batches).</summary>
    public const int ApplyWavesPerCommit = 2;

    /// <summary>
    /// Cap commit batches by intent count — NOT derived from commitRows/10000 (that hit 26 and
    /// buffered ~26×2048 native compose heaps before apply, OOMing OMW around row 908k).
    /// </summary>
    public const int MaxIntentsPerCommitCap = 8;

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

    public static Plan Resolve(
        int performanceCoreCount,
        int fileWorkers,
        int applyPartitions,
        int? recordBatchOverride = null,
        int? commitRowsOverride = null)
    {
        int batch = recordBatchOverride ?? ResolveRecordBatch(performanceCoreCount);
        int probe = ResolveProbeChunk(batch, fileWorkers);

        int derivedCommit = batch * applyPartitions * ApplyWavesPerCommit;
        int commit = commitRowsOverride ?? Math.Clamp(derivedCommit, 50_000, 500_000);

        int maxIntents = Math.Max(1, Math.Min(MaxIntentsPerCommitCap, batch / 256));

        int decomposeChan = Math.Max(8, applyPartitions * 4 + fileWorkers);

        int slotsPerWorker = Math.Max(2,
            (applyPartitions * ApplyWavesPerCommit + fileWorkers - 1) / Math.Max(1, fileWorkers));
        int fileChan = fileWorkers * slotsPerWorker;

        long rowBudget = (long)Math.Max(commit, batch) * decomposeChan;

        return new Plan(batch, probe, commit, decomposeChan, fileChan, maxIntents, rowBudget);
    }

    public static int ResolveRecordBatch(int performanceCoreCount)
    {
        int fromMem = TargetBytesPerBatch / EstBytesPerRecord;
        if (performanceCoreCount <= 4)
            return Math.Clamp(fromMem / 2, 512, 2048);
        if (performanceCoreCount >= 16)
            return Math.Clamp(fromMem * 2, 2048, 8192);
        return Math.Clamp(fromMem, 1024, 4096);
    }

    /// <summary>
    /// Pending probe units each hold a full native compose-probe heap. Scale down with parallel
    /// file workers so total in-flight probes ≈ batch size, not batch × workers × 512.
    /// </summary>
    public static int ResolveProbeChunk(int recordBatchSize, int fileWorkers = 1) =>
        Math.Clamp(recordBatchSize / Math.Max(1, fileWorkers * 8), 32, 256);

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
