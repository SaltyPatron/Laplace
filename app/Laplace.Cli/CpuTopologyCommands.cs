using Laplace.Engine.Core;

namespace Laplace.Cli;

internal static class CpuTopologyCommands
{
    public static int Run(string[] args)
    {
        int headroom = 2;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--p-cores":
                    Console.WriteLine(CpuTopology.PerformanceCoreCount);
                    return 0;
                case "--cpu-bound-workers":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int hr) && hr >= 0)
                    {
                        headroom = hr;
                        i++;
                    }
                    Console.WriteLine(CpuTopology.ResolveCpuBoundWorkers(headroom: headroom));
                    return 0;
                case "--io-bound-workers":
                    Console.WriteLine(CpuTopology.ResolveIngestCommitWorkers(headroom: 1));
                    return 0;
                case "--ingest-commit-workers":
                    Console.WriteLine(CpuTopology.ResolveIngestCommitWorkers(headroom: 1));
                    return 0;
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
            + $"cpu_bound_workers={CpuTopology.ResolveCpuBoundWorkers(headroom: 1)} "
            + $"io_bound_workers={CpuTopology.ResolveIngestCommitWorkers(headroom: 1)} "
            + $"apply_partitions={CpuTopology.ResolveApplyPartitions()}");

        bool pinOk = CpuTopology.PinCurrentThreadToPerformanceCores();
        Console.WriteLine($"entry_pin={pinOk.ToString().ToLowerInvariant()}");

        var topo = IngestTopology.EnsureReady();
        Console.Error.WriteLine(
            $"ingest_ready: file={topo.FileWorkers} compose={topo.ComposeWorkers} "
            + $"commit={topo.CommitWorkers} apply={topo.ApplyPartitions} pinned={topo.EntryThreadPinned.ToString().ToLowerInvariant()}");
        return 0;
    }

    // Emit the complete cluster-GUC set as ALTER SYSTEM statements, sourced from the
    // Cpu/MemoryTopology authorities. tune-pg.cmd pipes this straight to psql, so it holds
    // NO hardcoded GB literals or magic worker counts — the machine denotes every derived
    // value, and workload policy (durability/checkpoint/io) lives here in ONE place too.
    private static void EmitPgTuning()
    {
        long sharedMb = MemoryTopology.SharedBuffersBytes >> 20;
        long cacheMb = MemoryTopology.EffectiveCacheSizeBytes >> 20;
        long maintMb = MemoryTopology.MaintenanceWorkMemBytes >> 20;
        long workMb = MemoryTopology.WorkMemBytes >> 20;
        long walMb = MemoryTopology.WalBuffersBytes >> 20;
        int pcores = CpuTopology.PerformanceCoreCount;
        int maint = CpuTopology.ParallelMaintenanceWorkers;
        // Gather parallelism is a per-QUERY burst multiplier on work_mem and
        // CPU; index builds keep full maintenance parallelism, but scans get
        // half (2026-07-15 incident, doc 28).
        int gather = Math.Max(2, pcores / 4);
        int workers = CpuTopology.LogicalProcessorCount;
        var w = Console.Out;

        // Machine-derived (RAM + P/E topology) — the single source of truth.
        w.WriteLine($"ALTER SYSTEM SET shared_buffers = '{sharedMb}MB';");
        w.WriteLine($"ALTER SYSTEM SET effective_cache_size = '{cacheMb}MB';");
        w.WriteLine($"ALTER SYSTEM SET maintenance_work_mem = '{maintMb}MB';");
        w.WriteLine($"ALTER SYSTEM SET work_mem = '{workMb}MB';");
        w.WriteLine($"ALTER SYSTEM SET wal_buffers = '{walMb}MB';");
        w.WriteLine($"ALTER SYSTEM SET max_worker_processes = {workers};");
        w.WriteLine($"ALTER SYSTEM SET max_parallel_workers = {pcores};");
        w.WriteLine($"ALTER SYSTEM SET max_parallel_workers_per_gather = {gather};");
        w.WriteLine($"ALTER SYSTEM SET max_parallel_maintenance_workers = {maint};");
        w.WriteLine($"ALTER SYSTEM SET io_workers = {gather};");

        // Workload POLICY — deliberate, machine-independent (durability/checkpoint/IO shape).
        w.WriteLine("ALTER SYSTEM SET synchronous_commit = off;");
        w.WriteLine("ALTER SYSTEM SET wal_compression = on;");
        w.WriteLine("ALTER SYSTEM SET checkpoint_timeout = '30min';");
        w.WriteLine("ALTER SYSTEM SET checkpoint_completion_target = 0.9;");
        w.WriteLine("ALTER SYSTEM SET max_wal_size = '32GB';");
        w.WriteLine("ALTER SYSTEM SET min_wal_size = '4GB';");
        // Every Windows backend is a full process plus a per-connection
        // perfcache map; connections are budgeted, not free. Memory ceiling
        // arithmetic and the 2026-07-15 incident live in doc 28.
        w.WriteLine("ALTER SYSTEM SET max_connections = 60;");
        w.WriteLine("ALTER SYSTEM SET hash_mem_multiplier = 1.0;");
        w.WriteLine("ALTER SYSTEM SET temp_buffers = '32MB';");
        w.WriteLine("ALTER SYSTEM SET autovacuum_work_mem = '256MB';");
        w.WriteLine("ALTER SYSTEM SET effective_io_concurrency = 256;");
        w.WriteLine("ALTER SYSTEM SET maintenance_io_concurrency = 256;");
        w.WriteLine("ALTER SYSTEM SET random_page_cost = 1.1;");
        w.WriteLine("ALTER SYSTEM SET autovacuum_vacuum_cost_delay = 0;");
        w.WriteLine("ALTER SYSTEM SET huge_pages = try;");
        w.WriteLine("ALTER SYSTEM SET io_method = worker;");
        w.WriteLine("SELECT pg_reload_conf();");
    }
}
