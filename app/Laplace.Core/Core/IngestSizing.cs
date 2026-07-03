namespace Laplace.Engine.Core;



public static class IngestSizing

{

    public const int TargetBytesPerBatch = 1 << 20;



    public const int EstBytesPerRecord = 512;



    public const int ApplyWavesPerCommit = 2;



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



        int maxIntents = ResolveMaxIntentsPerCommit(batch, commit, commitRowsOverride);



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

