namespace Laplace.Engine.Core;

/// <summary>
/// One PostgreSQL resource equation. Values describe simultaneously live owners;
/// they are not a pile of independent clamps. The four memory domains are PostgreSQL
/// shared cache, PostgreSQL private backends, the ingest/client process, and the OS
/// page cache. Changing CPU/RAM changes the plan without crossing a hidden machine cap.
/// </summary>
public sealed record PostgresResourcePlan(
    long TotalPhysicalBytes,
    long SharedBuffersBytes,
    long EffectiveCacheSizeBytes,
    long BackendPrivateBudgetBytes,
    long ClientBudgetBytes,
    long OsPageCacheBudgetBytes,
    long WorkMemBytes,
    long MaintenanceWorkMemBytes,
    long AutovacuumWorkMemBytes,
    long TempBuffersBytes,
    int IngestConnectionOwners,
    int ObservabilityConnectionOwners,
    /// <summary>
    /// Pool slack reserved for the fold, owned by NEITHER fan. Any consumer deriving a
    /// fan width from IngestConnectionOwners must exclude it along with the control and
    /// observability owners, or it will count the slack as fan capacity.
    /// </summary>
    int FoldPoolHeadroomOwners,
    int ServingConnectionOwners,
    int MaintenanceConnectionOwners,
    int ReservedConnections,
    int MaxConnections,
    int MaxParallelWorkers,
    int MaxParallelWorkersPerGather,
    int MaxParallelMaintenanceWorkers,
    int IoWorkers,
    int MaxWorkerProcesses,
    int AutovacuumWorkers,
    int IoConcurrency)
{
    /// <summary>Shared cache, backend-private, ingest client, OS page cache.</summary>
    public const int MemoryDomains = 4;

    public static PostgresResourcePlan Current =>
        Resolve(
            MemoryTopology.TotalPhysicalBytes,
            CpuTopology.PerformanceCoreCount,
            CpuTopology.LogicalProcessorCount,
            CpuTopology.ParallelMaintenanceWorkers);

    public static PostgresResourcePlan Resolve(
        long totalPhysicalBytes, int performanceCores, int logicalProcessors,
        int parallelMaintenanceWorkers)
    {
        if (totalPhysicalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalPhysicalBytes));

        int p = Math.Max(1, performanceCores);
        int logical = Math.Max(p, logicalProcessors);
        int maintenance = Math.Max(1, parallelMaintenanceWorkers);

        // The ingest run's observability owners. These are NOT optional and they are
        // NOT part of the COPY/fold fan: NpgsqlIngestObservability holds the run
        // liveness advisory-lock connection CHECKED OUT for the entire run, and the
        // file-journal pump and the run-journal writer each open one more while the
        // fans are already at full width. Omitting them made 1 + 2p exactly equal to
        // the fan population, so the pool had zero slack and those three owners could
        // only wait the 15s Timeout and throw "connection pool has been exhausted"
        // (seed runs 32417964629, 32441233524, 32502815485). Per-file progress
        // publication did not create this; it made an already-zero-slack pool ask
        // every 2s per running file instead of once per file.
        const int observabilityConnections = 3;
        // The fold's slack, provisioned HERE rather than subtracted from the fold's own
        // width. It covers the renters the pool equation does not enumerate per-owner:
        // the run-journal/progress writer, replay-journal and route probes, finalize, and
        // a batch retry re-entering while the failed batch's folds still hold connections.
        //
        // It used to be taken out of the fold instead (IngestSizing.ConsensusFoldPoolHeadroom
        // subtracted from applyPartitions), which paid for pool slack with fold THROUGHPUT.
        // Measured on the 2026-08-23 foundation seed, scoped to the laplace database and
        // top-level statements: consensus.upsert_type cost 3,189s against 1,121s for the
        // whole apply side (COPY attestations 639s + physicalities 354s + entities 128s).
        // The fold is 2.8x its producer and was given p-2 connections against the COPY
        // fan's p. A consumer both dearer per unit and narrower than its producer
        // accumulates backlog monotonically, and DrainFoldsAsync is where that debt is
        // finally paid: CILI spent 272s of 334s there, WordNet 204s of 287s, and the drain
        // was 34.8% of total seed wall-clock.
        const int foldPoolHeadroom = 2;
        // One control connection plus simultaneous COPY and fold fans, plus the
        // observability owners above. This is the actual
        // IngestRunner/NpgsqlWorkingSetApply/NpgsqlIngestObservability ownership graph.
        int ingestConnections = checked(1 + 2 * p + foldPoolHeadroom + observabilityConnections);
        // Request concurrency follows schedulable logical processors. Queueing beyond
        // that only creates more backend memory owners without adding CPU throughput.
        int servingConnections = logical;
        // VACUUM/index/statistics work can occupy the maintenance pool concurrently.
        int maintenanceConnections = maintenance;
        // One operator connection remains available to inspect/recover a saturated host.
        int reservedConnections = 1;
        int maxConnections = checked(
            ingestConnections + servingConnections + maintenanceConnections
            + reservedConnections);

        // max_parallel_maintenance_workers is a subset of max_parallel_workers, not an
        // additional pool. I/O workers are blocking device workers and therefore follow
        // logical issuers rather than a capped query degree.
        int maxParallelWorkers = p;
        int gather = maintenance;
        int ioWorkers = logical;
        int maxWorkerProcesses = checked(maxParallelWorkers + ioWorkers);
        int autovacuumWorkers = maintenance;

        long domain = totalPhysicalBytes / MemoryDomains;
        long shared = domain;
        long backend = domain;
        long client = domain;
        long osCache = totalPhysicalBytes - shared - backend - client;

        // Client backends, parallel workers, and autovacuum workers are all distinct
        // private-memory owners. Divide the backend domain by that actual population.
        long backendProcesses = checked((long)maxConnections + maxParallelWorkers
                                        + autovacuumWorkers);
        long perBackend = Math.Max(1, backend / backendProcesses);

        // A normal backend can own executor memory and a temp-buffer arena together.
        // Split its share between those two live classes. Maintenance replaces normal
        // query execution on its backend and therefore owns the whole backend share.
        long work = Math.Max(1, perBackend / 2);
        long temp = perBackend - work;

        return new PostgresResourcePlan(
            totalPhysicalBytes,
            shared,
            shared + osCache,
            backend,
            client,
            osCache,
            work,
            perBackend,
            perBackend,
            temp,
            ingestConnections,
            observabilityConnections,
            foldPoolHeadroom,
            servingConnections,
            maintenanceConnections,
            reservedConnections,
            maxConnections,
            maxParallelWorkers,
            gather,
            maintenance,
            ioWorkers,
            maxWorkerProcesses,
            autovacuumWorkers,
            ioWorkers);
    }
}
