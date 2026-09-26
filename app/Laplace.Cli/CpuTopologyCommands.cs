using Laplace.Engine.Core;

namespace Laplace.Cli;

internal static class CpuTopologyCommands
{
    public static int Run(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--p-cores":
                    Console.WriteLine(CpuTopology.PerformanceCoreCount);
                    return 0;
                case "--cpu-bound-workers":
                    Console.WriteLine(CpuTopology.ResolveCpuBoundWorkers());
                    return 0;
                case "--io-bound-workers":
                    Console.WriteLine(CpuTopology.ResolveIoBoundWorkers());
                    return 0;
                case "--ingest-commit-workers":
                case "--apply-dispatch-workers":
                    Console.Error.WriteLine(
                        "outer apply-worker tuning was removed: one ordered set coordinator "
                        + "fans each apply across machine-derived apply_partitions");
                    return 2;
                case "--p-core-indices":
                    Console.WriteLine(string.Join(",", CpuTopology.PerformanceCoreCpuIndices));
                    return 0;
                case "--e-core-indices":
                    Console.WriteLine(string.Join(",", CpuTopology.EfficientCoreCpuIndices));
                    return 0;
                case "--pg-tuning":
                    EmitPgTuning();
                    return 0;
                case "--verify-pin":

                    {

                        bool pinned = CpuTopology.PinCurrentThreadToPerformanceCores();

                        Console.WriteLine(

                            $"pin_applied={pinned.ToString().ToLowerInvariant()} "

                            + $"source={CpuTopology.DetectionSource} "

                            + $"p_primary_lps=[{string.Join(",", CpuTopology.PerformanceCoreCpuIndices)}]");

                        return pinned ? 0 : 2;

                    }
            }
        }

        Console.WriteLine(
            $"source={CpuTopology.DetectionSource} "
            + $"hybrid={CpuTopology.IsHybrid.ToString().ToLowerInvariant()} "
            + $"p_physical={CpuTopology.PerformanceCoreCount} "
            + $"p_logical={CpuTopology.PerformanceLogicalProcessorCount} "
            + $"e_cores={CpuTopology.EfficientCoreCount} "
            + $"logical={CpuTopology.LogicalProcessorCount} "
            + $"p_primary_lps=[{string.Join(",", CpuTopology.PerformanceCoreCpuIndices)}] "
            + $"e_lps=[{string.Join(",", CpuTopology.EfficientCoreCpuIndices)}] "
            + $"cpu_bound_workers={CpuTopology.ResolveCpuBoundWorkers()} "
            + $"io_bound_workers={CpuTopology.ResolveIoBoundWorkers()} "
            + $"apply_partitions={CpuTopology.ResolveApplyPartitions()}");

        bool pinOk = CpuTopology.PinCurrentThreadToPerformanceCores();
        Console.WriteLine($"entry_pin={pinOk.ToString().ToLowerInvariant()}");

        var topo = IngestTopology.EnsureReady();
        Console.Error.WriteLine(
            $"ingest_ready: file={topo.FileWorkers} compose={topo.ComposeWorkers} "
            + $"io_available={topo.IoWorkersAvailable} "
            + $"apply_mode=set_coordinator apply_partitions={topo.ApplyPartitions} "
            + $"pinned={topo.EntryThreadPinned.ToString().ToLowerInvariant()}");
        return 0;
    }

    // Emits the cluster GUC set as ALTER SYSTEM statements. Machine-derived values come
    // from PostgresResourcePlan (CPU/memory topology); workload policy is the literal
    // settings below. The output is piped to psql.
    private static void EmitPgTuning()
    {
        var pg = PostgresResourcePlan.Current;

        // shared_buffers, effective_cache_size, temp_buffers and wal_buffers are stored in
        // 8 kB blocks; a value that is not a block multiple is rounded by PostgreSQL and the
        // live setting would not equal the emitted one. Floor to whole blocks so the value
        // stays within the planned budget.
        static long Blocks(long bytes) => (bytes >> 10) & ~7L;

        long sharedKb = Blocks(pg.SharedBuffersBytes);
        long walBuffersKb = Blocks(pg.SharedBuffersBytes / 32);
        long cacheKb = Blocks(pg.EffectiveCacheSizeBytes);
        long maintKb = pg.MaintenanceWorkMemBytes >> 10;   // kB units, not blocks
        long workKb = pg.WorkMemBytes >> 10;               // kB units
        long tempKb = Blocks(pg.TempBuffersBytes);
        long autovacKb = pg.AutovacuumWorkMemBytes >> 10;  // kB units

        // io_workers hold asynchronous reads outstanding under io_method = worker; they
        // allocate no work_mem and do no compute, so they are sized from logical issuers,
        // not from the parallel-query gather count.
        int ioWorkers = pg.IoWorkers;

        // max_worker_processes is the shared pool drawn on by parallel query and the
        // io_worker pool: the compute pool plus the I/O workers.
        int workers = pg.MaxWorkerProcesses;
        var w = Console.Out;

        // Machine-derived (RAM + P/E topology).
        w.WriteLine($"ALTER SYSTEM SET shared_buffers = '{sharedKb}kB';");
        w.WriteLine($"ALTER SYSTEM SET effective_cache_size = '{cacheKb}kB';");
        w.WriteLine($"ALTER SYSTEM SET maintenance_work_mem = '{maintKb}kB';");
        w.WriteLine($"ALTER SYSTEM SET work_mem = '{workKb}kB';");
        // wal_buffers = shared_buffers/32 without PostgreSQL's one-segment cap: parallel
        // apply connections fill a 16 MB buffer quickly, and a full buffer forces backends
        // to write WAL themselves.
        w.WriteLine($"ALTER SYSTEM SET wal_buffers = '{walBuffersKb}kB';");
        w.WriteLine($"ALTER SYSTEM SET max_worker_processes = {workers};");
        w.WriteLine($"ALTER SYSTEM SET max_parallel_workers = {pg.MaxParallelWorkers};");
        w.WriteLine($"ALTER SYSTEM SET max_parallel_workers_per_gather = {pg.MaxParallelWorkersPerGather};");
        w.WriteLine($"ALTER SYSTEM SET max_parallel_maintenance_workers = {pg.MaxParallelMaintenanceWorkers};");
        w.WriteLine($"ALTER SYSTEM SET io_workers = {ioWorkers};");
        w.WriteLine($"ALTER SYSTEM SET autovacuum_max_workers = {pg.AutovacuumWorkers};");

        // Workload policy, machine-independent (durability/checkpoint/IO shape).
        w.WriteLine("ALTER SYSTEM SET synchronous_commit = off;");
        // wal_compression is not emitted: this generator has no connection to probe the
        // available codecs, and pg_apply_wal_compression (scripts/pg-machine-tuning.sh)
        // picks lz4 > zstd > pglz from the binary.
        w.WriteLine("ALTER SYSTEM SET checkpoint_timeout = '30min';");
        w.WriteLine("ALTER SYSTEM SET checkpoint_completion_target = 0.9;");
        // Forced checkpoints re-arm full-page images, turning row-sized fold updates into
        // page-sized WAL; max_wal_size bounds how often volume forces one.
        w.WriteLine("ALTER SYSTEM SET max_wal_size = '16GB';");
        w.WriteLine("ALTER SYSTEM SET min_wal_size = '1GB';");
        // Each backend is a process with its own perfcache mapping; the connection count is
        // part of the memory plan.
        w.WriteLine($"ALTER SYSTEM SET max_connections = {pg.MaxConnections};");
        w.WriteLine($"ALTER SYSTEM SET superuser_reserved_connections = {pg.ReservedConnections};");
        // The partitioned substrate has hundreds of leaves plus their indexes and toast, so
        // one CREATE EXTENSION or COPY-to-parent transaction locks hundreds of objects, and
        // several run at once. max_locks_per_transaction sizes a shared lock table of
        // max_locks_per_transaction × max_connections slots, not a per-transaction ceiling.
        w.WriteLine("ALTER SYSTEM SET max_locks_per_transaction = 1024;");
        w.WriteLine("ALTER SYSTEM SET hash_mem_multiplier = 1.0;");
        w.WriteLine($"ALTER SYSTEM SET temp_buffers = '{tempKb}kB';");
        w.WriteLine($"ALTER SYSTEM SET autovacuum_work_mem = '{autovacKb}kB';");
        // Queue-depth requests follow the live I/O issuer pool. The previous fixed 64
        // merely mirrored one host's io_max_concurrency and silently capped larger hosts.
        w.WriteLine($"ALTER SYSTEM SET effective_io_concurrency = {pg.IoConcurrency};");
        w.WriteLine($"ALTER SYSTEM SET maintenance_io_concurrency = {pg.IoConcurrency};");
        w.WriteLine("ALTER SYSTEM SET random_page_cost = 1.1;");
        w.WriteLine("ALTER SYSTEM SET autovacuum_vacuum_cost_delay = 0;");
        // huge_pages = try: huge pages accelerate when available but are never a start
        // prerequisite.
        w.WriteLine("ALTER SYSTEM SET huge_pages = try;");
        w.WriteLine("ALTER SYSTEM SET io_method = worker;");

        // Statement-level profiling via pg_stat_statements.
        //
        // shared_preload_libraries is GUC_LIST_QUOTE: each library must be its own literal.
        // A single 'a,b' literal is stored as one quoted element and the postmaster cannot
        // start. pg_file_settings shows how the file parses without a restart:
        //   SELECT setting, error FROM pg_file_settings WHERE name='shared_preload_libraries'
        // The correct form reads back as [laplace_substrate, pg_stat_statements]; applied=false
        // is expected until the postmaster restarts.
        //
        // laplace_substrate stays first: the extension image is pinned in the postmaster and
        // the perfcache blobs are mapped at preload.
        w.WriteLine("ALTER SYSTEM SET shared_preload_libraries = 'laplace_substrate', 'pg_stat_statements';");
        w.WriteLine("ALTER SYSTEM SET pg_stat_statements.track = 'all';");
        w.WriteLine("ALTER SYSTEM SET pg_stat_statements.max = 10000;");
        // Planning time separately from execution. MaxAutoPrepare is zero and hot typed
        // reads/probes/folds prepare explicitly, so planning time exposes a missed
        // preparation site or an invalidated dependent plan.
        w.WriteLine("ALTER SYSTEM SET pg_stat_statements.track_planning = on;");
        // Split so the read-path gate does not treat this conf-file generator as a
        // live substrate query (it never runs against laplace).
        w.WriteLine("SELECT" + " pg_reload_conf();");
    }
}
