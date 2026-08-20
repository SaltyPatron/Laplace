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

        // One control connection plus simultaneous COPY and fold fans. This is the
        // actual IngestRunner/NpgsqlWorkingSetApply ownership graph.
        int ingestConnections = checked(1 + 2 * p);
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
