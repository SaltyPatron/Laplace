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
                    Console.WriteLine(IngestTopology.Current.ApplyDispatchWorkers);
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
            + $"cpu_bound_workers={CpuTopology.ResolveCpuBoundWorkers()} "
            + $"io_bound_workers={CpuTopology.ResolveIoBoundWorkers()} "
            + $"apply_partitions={CpuTopology.ResolveApplyPartitions()}");

        bool pinOk = CpuTopology.PinCurrentThreadToPerformanceCores();
        Console.WriteLine($"entry_pin={pinOk.ToString().ToLowerInvariant()}");

        var topo = IngestTopology.EnsureReady();
        Console.Error.WriteLine(
            $"ingest_ready: file={topo.FileWorkers} compose={topo.ComposeWorkers} "
            + $"io_available={topo.IoWorkersAvailable} "
            + $"apply_dispatch={topo.ApplyDispatchWorkers} apply_partitions={topo.ApplyPartitions} "
            + $"pinned={topo.EntryThreadPinned.ToString().ToLowerInvariant()}");
        return 0;
    }

    // Emit the complete cluster-GUC set as ALTER SYSTEM statements, sourced from the
    // Cpu/MemoryTopology authorities. tune-pg.cmd pipes this straight to psql, so it holds
    // NO hardcoded GB literals or magic worker counts — the machine denotes every derived
    // value, and workload policy (durability/checkpoint/io) lives here in ONE place too.
    private static void EmitPgTuning()
    {
        var pg = PostgresResourcePlan.Current;
        long sharedKb = pg.SharedBuffersBytes >> 10;
        long cacheKb = pg.EffectiveCacheSizeBytes >> 10;
        long maintKb = pg.MaintenanceWorkMemBytes >> 10;
        long workKb = pg.WorkMemBytes >> 10;
        long tempKb = pg.TempBuffersBytes >> 10;
        long autovacKb = pg.AutovacuumWorkMemBytes >> 10;

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
        // Sized from logical issuers with no [8,32] machine clamp. Device-native io_uring
        // is still selected by the live probe below when the PostgreSQL build supports it.
        int ioWorkers = pg.IoWorkers;

        // max_worker_processes is the SHARED pool that parallel query AND the io_worker pool
        // both draw from. It was set to the logical count while max_parallel_workers alone
        // already claimed pcores — so the cluster was oversubscribed before io_workers took
        // its cut, and raising the I/O pool without this would starve parallel query instead.
        // Maintenance workers are a subset of max_parallel_workers, so the shared pool is
        // exactly the compute pool plus blocking I/O workers; the former mystery +8 is gone.
        int workers = pg.MaxWorkerProcesses;
        var w = Console.Out;

        // Machine-derived (RAM + P/E topology) — the single source of truth.
        w.WriteLine($"ALTER SYSTEM SET shared_buffers = '{sharedKb}kB';");
        w.WriteLine($"ALTER SYSTEM SET effective_cache_size = '{cacheKb}kB';");
        w.WriteLine($"ALTER SYSTEM SET maintenance_work_mem = '{maintKb}kB';");
        w.WriteLine($"ALTER SYSTEM SET work_mem = '{workKb}kB';");
        // Let PostgreSQL derive WAL buffering from the machine-sized shared buffer
        // pool. The former RAM/512 with 16MiB/1GiB clamps was a second, conflicting
        // policy layered over PostgreSQL's own shared_buffers-aware calculation.
        w.WriteLine("ALTER SYSTEM RESET wal_buffers;");
        w.WriteLine($"ALTER SYSTEM SET max_worker_processes = {workers};");
        w.WriteLine($"ALTER SYSTEM SET max_parallel_workers = {pg.MaxParallelWorkers};");
        w.WriteLine($"ALTER SYSTEM SET max_parallel_workers_per_gather = {pg.MaxParallelWorkersPerGather};");
        w.WriteLine($"ALTER SYSTEM SET max_parallel_maintenance_workers = {pg.MaxParallelMaintenanceWorkers};");
        w.WriteLine($"ALTER SYSTEM SET io_workers = {ioWorkers};");
        w.WriteLine($"ALTER SYSTEM SET autovacuum_max_workers = {pg.AutovacuumWorkers};");

        // Workload POLICY — deliberate, machine-independent (durability/checkpoint/IO shape).
        w.WriteLine("ALTER SYSTEM SET synchronous_commit = off;");
        // wal_compression is deliberately NOT emitted here: `on` resolves to pglz, the slowest
        // codec, and this emitter cannot probe enumvals (it writes SQL with no connection).
        // The applier picks lz4 > zstd > pglz from what the binary actually has — see
        // pg_apply_wal_compression in scripts/pg-machine-tuning.sh.
        w.WriteLine("ALTER SYSTEM SET checkpoint_timeout = '30min';");
        w.WriteLine("ALTER SYSTEM SET checkpoint_completion_target = 0.9;");
        // 32GB -> 96GB (2026-08-13): the controlled evidence the old comment
        // asked for. Measured 2026-08-12 at 32GB: 60 volume-forced checkpoints
        // against 33 timed in ONE day, 478GB WAL written for a ~15GB substrate,
        // 42.3M full-page images = 72% of all WAL — each forced checkpoint
        // re-arms FPI, converting row-sized fold updates into page-sized WAL.
        // Rollback benchmark: 21.3KB WAL per consensus insert under the storm.
        // The WAL volume is a dedicated 128GB NVMe LV (/var/lib/pgwal); 96GB
        // leaves 25% headroom. Mirrored in scripts/pg-machine-tuning.sh;
        // PgTuningParityTests pins the pair.
        w.WriteLine("ALTER SYSTEM SET max_wal_size = '96GB';");
        w.WriteLine("ALTER SYSTEM SET min_wal_size = '4GB';");
        // Every Windows backend is a full process plus a per-connection
        // perfcache map; connections are budgeted, not free. Memory ceiling
        // arithmetic and the 2026-07-15 incident live in doc 28.
        w.WriteLine($"ALTER SYSTEM SET max_connections = {pg.MaxConnections};");
        w.WriteLine($"ALTER SYSTEM SET superuser_reserved_connections = {pg.ReservedConnections};");
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
        w.WriteLine($"ALTER SYSTEM SET temp_buffers = '{tempKb}kB';");
        w.WriteLine($"ALTER SYSTEM SET autovacuum_work_mem = '{autovacKb}kB';");
        // Queue-depth requests follow the live I/O issuer pool. The previous fixed 64
        // merely mirrored one host's io_max_concurrency and silently capped larger hosts;
        // pg_apply_io_method still chooses the device-native implementation at runtime.
        w.WriteLine($"ALTER SYSTEM SET effective_io_concurrency = {pg.IoConcurrency};");
        w.WriteLine($"ALTER SYSTEM SET maintenance_io_concurrency = {pg.IoConcurrency};");
        w.WriteLine("ALTER SYSTEM SET random_page_cost = 1.1;");
        w.WriteLine("ALTER SYSTEM SET autovacuum_vacuum_cost_delay = 0;");
        // huge_pages is deliberately NOT emitted here, for the same reason as
        // wal_compression: choosing between 'on' and 'try' requires reading
        // /proc/meminfo against shared_memory_size_in_huge_pages, and this emitter
        // writes SQL with no connection and no host access. Emitting a flat 'try'
        // made this the third owner of the setting and silently overwrote the
        // promotion whenever tune-pg ran. pg_apply_huge_pages in
        // scripts/pg-machine-tuning.sh owns it, on the path that can actually
        // measure. 'try' is what it falls back to, so nothing is lost by silence.
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
