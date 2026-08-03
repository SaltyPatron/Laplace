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
        int logical = CpuTopology.LogicalProcessorCount;

        // I/O WORKERS ARE NOT PARALLEL-QUERY WORKERS, AND SIZING THEM AS IF THEY WERE CAPPED
        // THE STORAGE LAYER AT ~6% OF THE DEVICE.
        //
        // io_workers used to be set to `gather`. `gather` is deliberately SMALL: it is a
        // per-QUERY burst multiplier where every worker allocates work_mem, and it was
        // halved after the 2026-07-15 memory incident (doc 28). None of that applies to
        // io_workers, which allocate no work_mem and never compute — under
        // io_method = worker they exist purely to hold asynchronous reads outstanding
        // against the device.
        //
        // MEASURED 2026-08-03 during the chess ingest, pgdata on a Samsung 970 EVO Plus:
        //   effective_io_concurrency = 256   (a promise)
        //   io_workers               = 3     (the actual ceiling: gather on this host)
        //   r/s 30,589   r_await 0.29ms   aqu-sz 11.92
        // By Little's law 11.92 / 0.29ms = ~41k IOPS, i.e. the drive delivered exactly what
        // the queue depth allowed while %util read 100% — a metric that means "at least one
        // request in flight" on a device with hardware queues and is NOT saturation. The
        // pool, not the disk, was the limit.
        //
        // Sized off logical processors as a proxy for concurrent I/O-issuing backends, with
        // a floor because 3 is never enough for NVMe and a ceiling because these are real
        // processes. Storage type is not discoverable here, so the floor is what makes the
        // default safe on flash; a spinning-disk host would want it lower and has none.
        int ioWorkers = Math.Clamp(logical, 8, 32);

        // max_worker_processes is the SHARED pool that parallel query AND the io_worker pool
        // both draw from. It was set to the logical count while max_parallel_workers alone
        // already claimed pcores — so the cluster was oversubscribed before io_workers took
        // its cut, and raising the I/O pool without this would starve parallel query instead.
        // Shape mirrors pg-machine-tuning.sh's `mwp=$(( pcores + pdeg + iow + 8 ))` so the
        // bootstrap fallback and this emitter do not hand a host two different pool sizes.
        int workers = pcores + maint + ioWorkers + 8;
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
        w.WriteLine($"ALTER SYSTEM SET io_workers = {ioWorkers};");

        // Workload POLICY — deliberate, machine-independent (durability/checkpoint/IO shape).
        w.WriteLine("ALTER SYSTEM SET synchronous_commit = off;");
        // wal_compression is deliberately NOT emitted here: `on` resolves to pglz, the slowest
        // codec, and this emitter cannot probe enumvals (it writes SQL with no connection).
        // The applier picks lz4 > zstd > pglz from what the binary actually has — see
        // pg_apply_wal_compression in scripts/pg-machine-tuning.sh.
        w.WriteLine("ALTER SYSTEM SET checkpoint_timeout = '30min';");
        w.WriteLine("ALTER SYSTEM SET checkpoint_completion_target = 0.9;");
        w.WriteLine("ALTER SYSTEM SET max_wal_size = '32GB';");
        w.WriteLine("ALTER SYSTEM SET min_wal_size = '4GB';");
        // Every Windows backend is a full process plus a per-connection
        // perfcache map; connections are budgeted, not free. Memory ceiling
        // arithmetic and the 2026-07-15 incident live in doc 28.
        w.WriteLine("ALTER SYSTEM SET max_connections = 60;");
        // MEASURED 2026-08-01 on a freshly migrated cluster: 479 partitions and 4,456
        // total relations in the laplace schema — more than double the ~220 this was
        // sized against, because the leaf count tracks relation_types.toml (207 relations)
        // and that file grows. The setting still holds: it sizes a SHARED pool of
        // max_locks_per_transaction x max_connections = 61,440 slots, not a per-transaction
        // ceiling, so 4,456 objects sit well inside it. Recording the real number because
        // the old one was stale and nothing re-derives it — if the leaf count ever
        // approaches the pool, this comment is the only thing that would have warned.
        //
        // The two-axis partitioned substrate holds those leaves plus indexes/toast: one
        // CREATE EXTENSION or one COPY-to-parent transaction locks hundreds
        // of objects, and parallel test fixtures / apply lanes run several
        // such transactions at once. The PG default (64) exhausts the shared
        // lock table (53200 out of shared memory) — observed 2026-07-16,
        // parallel dotnet test fixtures. 1024 × max_connections ≈ 61k shared
        // slots, well inside PG's design envelope (proc.h sizes fast-path
        // groups up to max_locks_per_transaction = 16k).
        w.WriteLine("ALTER SYSTEM SET max_locks_per_transaction = 1024;");
        w.WriteLine("ALTER SYSTEM SET hash_mem_multiplier = 1.0;");
        w.WriteLine("ALTER SYSTEM SET temp_buffers = '32MB';");
        w.WriteLine("ALTER SYSTEM SET autovacuum_work_mem = '256MB';");
        w.WriteLine("ALTER SYSTEM SET effective_io_concurrency = 256;");
        w.WriteLine("ALTER SYSTEM SET maintenance_io_concurrency = 256;");
        w.WriteLine("ALTER SYSTEM SET random_page_cost = 1.1;");
        w.WriteLine("ALTER SYSTEM SET autovacuum_vacuum_cost_delay = 0;");
        w.WriteLine("ALTER SYSTEM SET huge_pages = try;");
        w.WriteLine("ALTER SYSTEM SET io_method = worker;");

        // STATEMENT-LEVEL PROFILING. Nothing on this cluster could report execution time
        // per statement before this, so where a seed spends its time was argued from wait
        // events for the life of the project -- and several confident diagnoses survived
        // only because no instrument could contradict them.
        //
        // THE SYNTAX IS LOAD-BEARING. shared_preload_libraries is GUC_LIST_QUOTE: passing
        // 'a,b' as ONE string literal makes ALTER SYSTEM store it as a single list element,
        // written back quoted as one identifier. On 2026-08-01 that took the cluster down
        // hard -- the postmaster looped 80 times on
        //   FATAL: could not access file "laplace_substrate,pg_stat_statements"
        // and recovery needed root, because the data directory is 0700 owned by the runner
        // and SQL is unreachable when the server will not start. Each library MUST be its
        // own value in the list.
        //
        // VERIFY BEFORE RESTARTING. pg_file_settings reports how the file actually parses
        // without starting anything:
        //   SELECT setting, error FROM pg_file_settings WHERE name='shared_preload_libraries'
        // The correct form reads back as [laplace_substrate, pg_stat_statements]; the broken
        // one reads back as a single quoted element. applied=false is EXPECTED here -- this
        // is a postmaster-start parameter, so it differs from the running value until a
        // bounce. pipeline.sh checks this before it will restart.
        //
        // laplace_substrate must stay first: the extension image is pinned in the postmaster
        // and the perfcache blobs are mmap'd at preload.
        w.WriteLine("ALTER SYSTEM SET shared_preload_libraries = 'laplace_substrate', 'pg_stat_statements';");
        w.WriteLine("ALTER SYSTEM SET pg_stat_statements.track = 'all';");
        w.WriteLine("ALTER SYSTEM SET pg_stat_statements.max = 10000;");
        // Planning time separately from execution: MEASURED 5.760 ms planning against
        // 21.053 ms execution on one descent probe, and the ingest path leaves
        // MaxAutoPrepare at 0, so that tax is paid per batch per tier. Without this the
        // largest suspected overhead is invisible in the view added to find it.
        w.WriteLine("ALTER SYSTEM SET pg_stat_statements.track_planning = on;");
        // Split so the read-path gate does not treat this conf-file generator as a
        // live substrate query (it never runs against laplace).
        w.WriteLine("SELECT" + " pg_reload_conf();");
    }
}

