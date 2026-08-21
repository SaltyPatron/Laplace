using System.Globalization;
using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// PostgresResourcePlan is the resource authority and pg-machine-tuning.sh is the
/// bare-host fallback. These tests keep their equations identical and reject the
/// independent fixed caps that previously let the two paths oversubscribe RAM.
/// </summary>
public class PgTuningParityTests
{
    private const long MiB = 1L << 20;

    private static string TuningScript()
    {
        var path = Path.Combine(TypeIdLawTests.FindRepoRootPublic(), "scripts", "pg-machine-tuning.sh");
        Assert.True(File.Exists(path), $"pg-machine-tuning.sh not found at {path}");
        return File.ReadAllText(path);
    }

    private static string BootstrapScript()
    {
        var path = Path.Combine(TypeIdLawTests.FindRepoRootPublic(), "scripts", "bootstrap-laplace-runner.sh");
        Assert.True(File.Exists(path), $"bootstrap-laplace-runner.sh not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The POLICY literals -- the ones that are not derived from RAM -- exist in TWO
    /// bodies, and only one of them ever runs. pg_apply_machine_tuning invokes
    /// `cpu-topology --pg-tuning` (CpuTopologyCommands.EmitPgTuning) FIRST and treats it as
    /// authoritative; the bash formulas are reached only when that emitter fails. So a
    /// policy value edited in the shell alone never reaches any cluster, silently.
    ///
    /// That happened: max_wal_size was changed to 64GB in the shell on 2026-07-31 while the
    /// emitter kept '32GB'. The shell carries the comment "this script is what actually
    /// issues the ALTER SYSTEM", which is false, and nothing contradicted it. The cluster
    /// stayed at 32GB and the change looked applied.
    ///
    /// Pinning the pair by parsing both sources, so whichever side moves next fails here.
    /// </summary>
    [Theory]
    [InlineData("max_wal_size", "PG_TUNE_MAX_WAL")]
    [InlineData("min_wal_size", "PG_TUNE_MIN_WAL")]
    [InlineData("checkpoint_timeout", "PG_TUNE_CHECKPOINT")]
    public void PolicyLiterals_MatchBetweenTheEmitterAndTheShellFallback(string guc, string shellVar)
    {
        var emitterPath = Path.Combine(TypeIdLawTests.FindRepoRootPublic(),
            "app", "Laplace.Cli", "CpuTopologyCommands.cs");
        Assert.True(File.Exists(emitterPath), $"emitter not found at {emitterPath}");
        var emitter = File.ReadAllText(emitterPath);

        var em = Regex.Match(emitter, Regex.Escape(guc) + @"\s*=\s*'([^']+)'");
        Assert.True(em.Success, $"{guc} literal not found in CpuTopologyCommands.EmitPgTuning");

        var sm = Regex.Match(TuningScript(), @"^\s*" + Regex.Escape(shellVar) + @"=(\S+)\s*$",
            RegexOptions.Multiline);
        Assert.True(sm.Success, $"{shellVar} not found in pg-machine-tuning.sh");

        Assert.True(
            string.Equals(em.Groups[1].Value, sm.Groups[1].Value, StringComparison.OrdinalIgnoreCase),
            $"{guc} diverged: emitter (AUTHORITATIVE) says '{em.Groups[1].Value}', shell "
            + $"{shellVar} says '{sm.Groups[1].Value}'. The emitter is what runs -- a value "
            + "changed only in the shell never reaches a cluster.");
    }

    /// <summary>
    /// The host bootstrap sizes the dedicated WAL volume before the tuning script can
    /// start PostgreSQL. It therefore carries the same max_wal_size target as a shell
    /// integer. This was left at 32 after the emitter and tuning fallback moved to 96,
    /// causing setup-host to demand 96 GiB FREE on the 128 GiB volume whose intended
    /// steady state is 96 GiB WAL plus 32 GiB reserve. Pin the third policy consumer.
    /// </summary>
    [Fact]
    public void BootstrapWalCapacity_MatchesTuningAndIncludesRecoveryReserve()
    {
        var tuning = Regex.Match(TuningScript(), @"^\s*PG_TUNE_MAX_WAL=(\d+)GB\s*$",
            RegexOptions.Multiline);
        Assert.True(tuning.Success, "PG_TUNE_MAX_WAL integer GiB literal not found");

        var bootstrap = BootstrapScript();
        var target = Regex.Match(bootstrap,
            @"^LAPLACE_PG_MAX_WAL_GB=""\$\{LAPLACE_PG_MAX_WAL_GB:-(\d+)\}""$",
            RegexOptions.Multiline);
        var reserve = Regex.Match(bootstrap,
            @"^LAPLACE_PG_WAL_RESERVE_GB=""\$\{LAPLACE_PG_WAL_RESERVE_GB:-(\d+)\}""$",
            RegexOptions.Multiline);

        Assert.True(target.Success, "bootstrap WAL target literal not found");
        Assert.True(reserve.Success, "bootstrap WAL recovery reserve literal not found");
        Assert.Equal(tuning.Groups[1].Value, target.Groups[1].Value);
        Assert.True(int.Parse(reserve.Groups[1].Value, CultureInfo.InvariantCulture) > 0,
            "dedicated WAL volume must reserve capacity beyond the soft max_wal_size target");
    }

    /// <summary>
    /// The same pin for UNQUOTED numeric GUCs, which the quoted-literal theory above could
    /// never see: its regex requires <c>= '...'</c>, so an integer setting like
    /// <c>effective_io_concurrency = 64</c> was unpinnable and silently exempt.
    ///
    /// MEASURED 2026-08-16: with the emitter on 64 and the shell on 256 the whole
    /// PgTuningParityTests suite passed, 18/18. That is exactly the max_wal_size incident
    /// this file was written for, on a different setting, still open.
    ///
    /// effective_io_concurrency and maintenance_io_concurrency moved 256 -> 64 after
    /// pg_aios showed a backend pinned at io_max_concurrency = 64 during a 2,573 MB scan,
    /// and three cold 2.5 GB leaves came in at 3175.480 / 3175.204 / 3167.613 ms for
    /// eic 256 / 64 / 32 -- 8 ms of spread, lowest setting marginally fastest.
    /// </summary>
    [Theory]
    [InlineData("effective_io_concurrency", "PG_TUNE_IO_CONC")]
    [InlineData("maintenance_io_concurrency", "PG_TUNE_IO_CONC")]
    public void NumericLiterals_MatchBetweenTheEmitterAndTheShellFallback(string guc, string shellVar)
    {
        var emitterPath = Path.Combine(TypeIdLawTests.FindRepoRootPublic(),
            "app", "Laplace.Cli", "CpuTopologyCommands.cs");
        Assert.True(File.Exists(emitterPath), $"emitter not found at {emitterPath}");
        var emitter = File.ReadAllText(emitterPath);

        Assert.Contains($"{guc} = {{pg.IoConcurrency}}", emitter, StringComparison.Ordinal);
        Assert.Contains($"{shellVar}=$iow", TuningScript(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShellFormula_MatchesTheAccountedResourcePlan()
    {
        var sh = TuningScript();
        Assert.Contains("backend_kb=$(( mem_kb / 4 ))", sh, StringComparison.Ordinal);
        Assert.Contains("backend_processes=$(( PG_TUNE_MAXCONN + pcores + avw ))", sh,
            StringComparison.Ordinal);
        Assert.Contains("wm_kb=$(( per_backend_kb / 2 ))", sh, StringComparison.Ordinal);
        Assert.Contains("PG_TUNE_WB=auto", sh, StringComparison.Ordinal);
        Assert.Contains("ALTER SYSTEM RESET wal_buffers", sh, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\(\(\s*(sb|ecs|mwm|wm|iow)\s*>\s*\d+", sh);
    }

    /// <summary>
    /// Pin the C# resource conservation law independently of the source-shape test above.
    /// </summary>
    [Fact]
    public void MemoryTopology_UsesOneAccountedPlanWithoutMachineCaps()
    {
        var p = PostgresResourcePlan.Current;
        Assert.Equal(p.TotalPhysicalBytes, p.SharedBuffersBytes
            + p.BackendPrivateBudgetBytes + p.ClientBudgetBytes + p.OsPageCacheBudgetBytes);
        Assert.Equal(p.SharedBuffersBytes + p.OsPageCacheBudgetBytes,
            p.EffectiveCacheSizeBytes);
        Assert.Equal(p.MaintenanceWorkMemBytes, p.WorkMemBytes + p.TempBuffersBytes);
    }

    /// <summary>
    /// THE INVARIANT NOTHING COMPUTED. Every GUC above is derived in isolation from
    /// physical RAM and no code, in either language, ever added them up. shared_buffers is
    /// one pinned allocation; work_mem is per sort/hash node PER CONNECTION;
    /// maintenance_work_mem is per autovacuum worker. The resident peak is a PRODUCT of
    /// knobs owned by three different files, and that product was never expressed.
    ///
    /// 2026-07-15 was that product going over the machine. It was attributed by hand
    /// afterwards, and the response was a tighter divisor rather than a bound -- so the
    /// same failure returns the moment max_connections or any cap moves, and it returns
    /// as SWAP DEATH rather than an OOM kill, which leaves no record in the journal to
    /// attribute it by.
    ///
    /// The parameters mirror what pg-machine-tuning.sh actually emits (max_connections=60,
    /// autovacuum_work_mem=256MB, autovacuum_max_workers=cores/4 within [3,6]). Two grants
    /// per connection is deliberately optimistic -- one partitioned hash join takes more --
    /// so this is a LOWER bound. If even the lower bound does not fit, the configuration
    /// is indefensible without measuring anything.
    /// </summary>
    [Fact]
    public void EnumerableGrants_FitInsideTheMachine()
    {
        var p = PostgresResourcePlan.Current;
        long backendOwners = (long)p.MaxConnections + p.MaxParallelWorkers
            + p.AutovacuumWorkers;
        Assert.True(backendOwners * p.MaintenanceWorkMemBytes
            <= p.BackendPrivateBudgetBytes);
    }

    /// <summary>
    /// THE INGEST POOL MUST HAVE SLACK OVER ITS FAN. MaxPoolSize on the ingest
    /// connection string IS IngestConnectionOwners, and the simultaneously live
    /// owners are: one control connection, the apply COPY fan (ApplyPartitions),
    /// the consensus fold fan (the same width, gated by ConsensusAccumulatingWriter's
    /// _foldConnections), AND NpgsqlIngestObservability -- which holds the run
    /// liveness advisory-lock connection checked out for the whole run and opens one
    /// more for the file-journal pump and one for the run-journal writer.
    ///
    /// At 1 + 2p the sum equalled the fans exactly, so the observability owners had
    /// no slot to take. They waited the connection string's 15s Timeout and threw
    /// NpgsqlException "the connection pool has been exhausted", failing the ingest
    /// from the fold path -- seed runs 32417964629 (chess-syzygy), 32441233524
    /// (chess PGN) and 32502815485 (conceptnet). An equality here is not a margin;
    /// the assertion is deliberately strict so the next owner added to the ingest
    /// process has to be accounted rather than silently starving the fans.
    /// </summary>
    [Fact]
    public void IngestPool_CoversItsFansAndItsObservabilityOwners()
    {
        var p = PostgresResourcePlan.Current;
        int applyFan = IngestTopology.Current.ApplyPartitions;
        int foldFan = applyFan;
        int control = 1;

        Assert.True(p.ObservabilityConnectionOwners > 0);
        Assert.True(
            p.IngestConnectionOwners >= control + applyFan + foldFan
                + p.ObservabilityConnectionOwners,
            $"ingest pool {p.IngestConnectionOwners} cannot seat {control} control + "
            + $"{applyFan} apply + {foldFan} fold + {p.ObservabilityConnectionOwners} "
            + "observability owners; the losers time out as 'pool has been exhausted'");
    }

    /// <summary>
    /// The backend budget must be a real number. If shared_buffers plus the OS reserve
    /// consumed the machine, every work_mem grant would be carved out of nothing -- the
    /// shape of an over-large shared_buffers cap on a small host.
    /// </summary>
    [Fact]
    public void BackendBudget_IsNotAlreadySpent()
    {
        Assert.True(MemoryTopology.BackendMemoryBudgetBytes > 0,
            "shared_buffers + OS reserve consume the whole machine; no room for backend grants");
        Assert.True(PostgresResourcePlan.Current.WorkMemBytes > 0);
    }

    /// <summary>
    /// Proof the guard above can actually reject something. A bound only ever evaluated
    /// against the current machine is untestable in the direction that matters, and this
    /// tree is full of assertions that cannot fire -- the shared_buffers "cap" of 64GiB
    /// never binds below a 256GB host, so it has never once constrained anything.
    ///
    /// These are the PRE-INCIDENT values recorded in pg-machine-tuning.sh: work_mem
    /// RAM/256 capped 512MB and an uncapped RAM/4 shared_buffers, which on the 125GB seed
    /// host produced work_mem=502MB and shared_buffers=31.4GB. The guard must reject that
    /// configuration, or it would not have prevented the failure it exists to prevent.
    /// </summary>
    [Fact]
    public void LargeMachine_DoesNotFlattenAtFormerCaps()
    {
        var p = PostgresResourcePlan.Resolve(512L << 30, 64, 128, 32);
        Assert.Equal(128L << 30, p.SharedBuffersBytes);
        Assert.True(p.WorkMemBytes > 64L * MiB);
        Assert.True(p.IoWorkers > 32);
        Assert.True(p.MaxConnections > 60);
    }

    /// <summary>
    /// The formula parity above only covers five memory GUCs. The costlier divergence was
    /// in the SET of knobs each side emitted at all: CpuTopologyCommands.EmitPgTuning wrote
    /// max_connections, hash_mem_multiplier, autovacuum_work_mem and temp_buffers; the shell
    /// wrote none of them, so the Linux cluster silently kept PG defaults that multiply the
    /// memory budget (hash_mem_multiplier 2.0 doubles work_mem per hash node;
    /// autovacuum_work_mem = -1 gives every autovacuum worker the full maintenance_work_mem).
    /// The bootstrap fallback must therefore cover every GUC the emitter sets.
    /// </summary>
    [Fact]
    public void ShellFallback_CoversEveryGucTheEmitterSets()
    {
        var root = TypeIdLawTests.FindRepoRootPublic();
        var emitter = File.ReadAllText(Path.Combine(root, "app", "Laplace.Cli", "CpuTopologyCommands.cs"));
        var shell = TuningScript();

        var emitted = new HashSet<string>(
            Regex.Matches(emitter, @"ALTER SYSTEM SET (?<g>[a-z_]+)")
                 .Select(m => m.Groups["g"].Value),
            StringComparer.Ordinal);
        var shellSet = new HashSet<string>(
            Regex.Matches(shell, @"ALTER SYSTEM SET (?<g>[a-z_]+)")
                 .Select(m => m.Groups["g"].Value),
            StringComparer.Ordinal);

        Assert.NotEmpty(emitted);

        // DECLARED EXEMPTIONS. The parity rule is right and stays -- a GUC the emitter sets
        // and the fallback omits leaves a cluster on a PG default nobody chose. But these
        // four must NOT be added to the shell fallback, and the reason is a cluster outage
        // rather than a preference:
        //
        // shared_preload_libraries is validated ONLY at postmaster start. A bad value is
        // undetectable until the server is already gone, at which point SQL is unreachable
        // and even ALTER SYSTEM RESET cannot undo it. On 2026-08-01 setting it took this
        // cluster down for 80 restart attempts and recovery required root, because the data
        // directory is 0700 owned by the runner. The shell fallback runs during BARE-HOST
        // BOOTSTRAP, before the CLI exists and before anything could verify the value --
        // exactly the context where that failure is least recoverable.
        //
        // The pg_stat_statements.* knobs are inert without the library loaded, so setting
        // them in the fallback would be noise that implies profiling is active when it is
        // not.
        //
        // Exempt, not silently satisfied: adding a dangerous line to the bootstrap path to
        // make a test green is how the original divergence happened.
        var preloadExempt = new HashSet<string>(StringComparer.Ordinal)
        {
            "shared_preload_libraries",
        };

        var missing = emitted
            .Where(g => !preloadExempt.Contains(g) && !g.StartsWith("pg_stat_statements", StringComparison.Ordinal))
            .Except(shellSet)
            .OrderBy(g => g, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "scripts/pg-machine-tuning.sh's bootstrap fallback does not set: "
            + string.Join(", ", missing)
            + ". A GUC the emitter sets but the fallback omits is a cluster running a PG "
            + "default nobody chose — that is how hash_mem_multiplier=2.0 and "
            + "autovacuum_work_mem=-1 reached the seed host.");
    }

    /// <summary>
    /// The shell must prefer the emitter, not re-derive. If this call disappears the two
    /// implementations are live again and only the weaker formula gate stands behind them.
    /// </summary>
    [Fact]
    public void ShellPrefersTheAuthoritativeEmitter()
    {
        var shell = TuningScript();
        Assert.Contains("cpu-topology --pg-tuning", shell, StringComparison.Ordinal);
        Assert.Matches(@"pg_apply_machine_tuning\(\)\s*\{[^}]*cpu-topology --pg-tuning", shell);
    }

    /// <summary>
    /// effective_cache_size is a percentage, not a divisor — check its shape separately.
    /// </summary>
    [Fact]
    public void EffectiveCacheSize_MatchesMemoryTopology()
    {
        Assert.Contains("ecs=$(( mem_kb / 2 ))", TuningScript(),
            StringComparison.Ordinal);
        Assert.Equal(PostgresResourcePlan.Current.SharedBuffersBytes
            + PostgresResourcePlan.Current.OsPageCacheBudgetBytes,
            MemoryTopology.EffectiveCacheSizeBytes);
    }

    /// <summary>
    /// io_workers MUST NOT be derived from a parallel-QUERY degree.
    ///
    /// It was: the emitter set `io_workers = {gather}` and the shell set it from
    /// $PG_TUNE_PDEG. `gather` is a per-query burst multiplier where every worker allocates
    /// work_mem, and it is deliberately kept SMALL — it was halved after the 2026-07-15
    /// memory incident. io_workers allocate no work_mem and never compute; under
    /// io_method=worker they exist only to hold asynchronous reads outstanding, and the pool
    /// bounds achievable queue depth for the whole cluster.
    ///
    /// MEASURED 2026-08-03 with pgdata on a Samsung 970 EVO Plus, mid chess ingest:
    /// effective_io_concurrency=256 while io_workers=3, giving aqu-sz 11.92 at r_await
    /// 0.29ms — by Little's law ~41k IOPS, about 6% of the device, while %util read 100%
    /// (which on a queued device means "at least one request in flight", not saturation).
    ///
    /// This gate exists because the same class of silent divergence already happened once:
    /// max_wal_size was changed in the shell alone and the cluster stayed on the emitter's
    /// value for a day with nothing to contradict it.
    /// </summary>
    [Fact]
    public void IoWorkers_IsNotTiedToParallelQueryDegree()
    {
        var emitter = File.ReadAllText(Path.Combine(TypeIdLawTests.FindRepoRootPublic(),
            "app", "Laplace.Cli", "CpuTopologyCommands.cs"));
        var shell = TuningScript();

        var em = Regex.Match(emitter, @"io_workers = \{(?<expr>[A-Za-z0-9_]+)\}");
        Assert.True(em.Success, "emitter no longer emits an interpolated io_workers value");
        Assert.False(em.Groups["expr"].Value is "gather" or "maint" or "pcores",
            $"io_workers is derived from '{em.Groups["expr"].Value}', a CPU parallelism value. "
            + "I/O workers block on the device and allocate no work_mem — sizing them by a "
            + "number kept small for memory pressure caps the storage layer.");

        var sm = Regex.Match(shell, @"ALTER SYSTEM SET io_workers = \$(?<var>[A-Z_]+)");
        Assert.True(sm.Success, "pg-machine-tuning.sh no longer sets io_workers");
        Assert.NotEqual("PG_TUNE_PDEG", sm.Groups["var"].Value);

        Assert.Contains("int ioWorkers = pg.IoWorkers", emitter, StringComparison.Ordinal);
        Assert.Contains("iow=$cores", shell, StringComparison.Ordinal);
    }

    /// <summary>
    /// max_worker_processes is the SHARED pool parallel query and the io_worker pool both
    /// draw from, so it must account for the I/O pool. It previously did not — it was the
    /// logical count while max_parallel_workers alone already claimed pcores, leaving the
    /// cluster oversubscribed before io_workers took any share.
    /// </summary>
    [Fact]
    public void MaxWorkerProcesses_AccountsForTheIoWorkerPool()
    {
        var emitter = File.ReadAllText(Path.Combine(TypeIdLawTests.FindRepoRootPublic(),
            "app", "Laplace.Cli", "CpuTopologyCommands.cs"));
        Assert.Contains("int workers = pg.MaxWorkerProcesses", emitter, StringComparison.Ordinal);
        Assert.Matches(@"mwp=\$\(\(\s*pcores \+ iow\s*\)\)", TuningScript());
    }

    /// <summary>
    /// The io_method probe must run on the PRIMARY path, not only in the bootstrap fallback.
    /// It used to live in pg_apply_machine_tuning_fallback alone, so on every host where the
    /// CLI is built — i.e. normal operation — the emitter's hardcoded io_method=worker stood
    /// and the probe never executed. io_uring removes the io_workers ceiling entirely by
    /// letting each backend submit directly to the kernel, which is the point on NVMe.
    /// </summary>
    [Fact]
    public void IoMethodProbe_RunsOnThePrimaryApplyPath()
    {
        var shell = TuningScript();
        Assert.Contains("pg_apply_io_method()", shell, StringComparison.Ordinal);
        Assert.Matches(
            @"pg_apply_machine_tuning\(\)\s*\{(?:[^{}]|\{[^{}]*\})*pg_apply_io_method",
            shell);
    }
}
