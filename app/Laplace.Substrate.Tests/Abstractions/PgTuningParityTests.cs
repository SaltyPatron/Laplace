using System.Globalization;
using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// MemoryTopology is the single source for the Postgres memory GUCs, but
/// scripts/pg-machine-tuning.sh is what actually issues the ALTER SYSTEM
/// (pipeline.sh phase_tune_pg, setup-host, scripts\win\tune-pg.cmd). The two
/// silently drifted once: the 2026-07-15 / doc-28 hardening (work_mem
/// RAM/1536 capped 64MB, maintenance_work_mem RAM/48 capped 1GB, a 16GB
/// shared_buffers cap) landed in MemoryTopology.cs and was never propagated to
/// the shell. The stale shell kept applying the pre-incident RAM/256-cap-512MB
/// and an UNCAPPED shared_buffers, so the 125GB seed host ran with
/// work_mem=502MB / maintenance_work_mem=3.9GB / shared_buffers=31.4GB and put
/// 12.5GB into swap mid-ingest — while MemoryTopology.cs read as correct.
///
/// This gate pins the shell's arithmetic to the C# constants. If it fails, the
/// two disagree about how much memory Postgres may take: fix the divergence,
/// never the fixture.
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

        // Bare integer, not quoted, and not interpolated -- an interpolated value is a
        // formula and belongs in the arithmetic tests, not here.
        var em = Regex.Match(emitter, Regex.Escape(guc) + @"\s*=\s*(\d+)\s*;");
        Assert.True(em.Success, $"{guc} numeric literal not found in CpuTopologyCommands.EmitPgTuning");

        var sm = Regex.Match(TuningScript(), @"^\s*" + Regex.Escape(shellVar) + @"=(\d+)\s*$",
            RegexOptions.Multiline);
        Assert.True(sm.Success, $"{shellVar} not found as a bare integer in pg-machine-tuning.sh");

        Assert.Equal(em.Groups[1].Value, sm.Groups[1].Value);
    }

    /// <summary>
    /// Reads `name=$(( mem_kb / DIV / 1024 )); (( name &lt; LO )) &amp;&amp; name=LO; (( name &gt; HI )) &amp;&amp; name=HI`
    /// and returns (divisor, loMB, hiMB). Whitespace-tolerant, order-fixed (lo then hi).
    /// </summary>
    private static (long Div, long Lo, long Hi) ShellClamp(string sh, string name)
    {
        var m = Regex.Match(
            sh,
            $@"{Regex.Escape(name)}=\$\(\(\s*mem_kb\s*/\s*(?<div>\d+)\s*/\s*1024\s*\)\)\s*;\s*"
            + $@"\(\(\s*{Regex.Escape(name)}\s*<\s*(?<lo>\d+)\s*\)\)\s*&&\s*{Regex.Escape(name)}=\d+\s*;\s*"
            + $@"\(\(\s*{Regex.Escape(name)}\s*>\s*(?<hi>\d+)\s*\)\)\s*&&\s*{Regex.Escape(name)}=\d+");

        Assert.True(m.Success,
            $"pg-machine-tuning.sh no longer declares '{name}' as a clamped mem_kb/DIV/1024 expression — "
            + "the parity gate cannot read it. Keep the shape or update this gate together with the script.");

        return (long.Parse(m.Groups["div"].Value, CultureInfo.InvariantCulture),
                long.Parse(m.Groups["lo"].Value, CultureInfo.InvariantCulture),
                long.Parse(m.Groups["hi"].Value, CultureInfo.InvariantCulture));
    }

    // MemoryTopology divides *bytes*; the shell divides *kB then MB*. Both reduce to the
    // same divisor over physical RAM, so the divisor and the MB clamps compare directly.
    [Theory]
    [InlineData("wm", 1536, 16, 64)]     // MemoryTopology.WorkMemBytes
    [InlineData("mwm", 48, 256, 1024)]   // MemoryTopology.MaintenanceWorkMemBytes
    [InlineData("wb", 512, 16, 1024)]    // MemoryTopology.WalBuffersBytes
    [InlineData("sb", 4, 128, 65536)]    // MemoryTopology.SharedBuffersBytes
    public void ShellFormula_MatchesMemoryTopology(string name, long div, long loMB, long hiMB)
    {
        var (shellDiv, shellLo, shellHi) = ShellClamp(TuningScript(), name);
        Assert.Equal(div, shellDiv);
        Assert.Equal(loMB, shellLo);
        Assert.Equal(hiMB, shellHi);
    }

    /// <summary>
    /// The InlineData above is only meaningful if it still describes MemoryTopology. Pin the
    /// C# side against RAM-independent probe values so editing MemoryTopology alone fails here
    /// too — the drift must be caught from whichever side moves.
    /// </summary>
    [Fact]
    public void MemoryTopology_StillCarriesTheHardenedCaps()
    {
        // Bounds, not equality: the divisors float with host RAM, only the clamps are law.
        Assert.True(MemoryTopology.WorkMemBytes <= 64 * MiB,
            $"work_mem cap regressed: {MemoryTopology.WorkMemBytes >> 20}MB > 64MB — this is the "
            + "doc-28 cap that keeps a misplanned partitioned hash join from starving the host");
        Assert.True(MemoryTopology.MaintenanceWorkMemBytes <= 1024 * MiB,
            $"maintenance_work_mem cap regressed: {MemoryTopology.MaintenanceWorkMemBytes >> 20}MB > 1024MB");
        // Raised 16 GiB -> 64 GiB 2026-07-28: the old cap pinned a 128 GB box to 16 GiB
        // against a 173 GB database while RAM/4 was 33.5 GiB. Still a cap — above ~64 GiB
        // PostgreSQL's clock sweep and checkpoint cost stop paying back.
        Assert.True(MemoryTopology.SharedBuffersBytes <= 65536 * MiB,
            $"shared_buffers cap regressed: {MemoryTopology.SharedBuffersBytes >> 20}MB > 65536MB");
        Assert.True(MemoryTopology.WorkMemBytes >= 16 * MiB);
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
    [Theory]
    [InlineData(60, 2, 3)]      // what the shell emits on a 6-core box
    [InlineData(60, 4, 6)]      // a heavier plan and the autovacuum worker ceiling
    public void EnumerableGrants_FitInsideTheMachine(int maxConns, int nodesPerQuery, int avWorkers)
    {
        long avWorkMem = 256L * MiB;   // PG_TUNE_AVWM
        long peak = MemoryTopology.PeakResidentLowerBoundBytes(
            maxConns, nodesPerQuery, avWorkers, avWorkMem);
        long ceiling = (long)(MemoryTopology.TotalPhysicalBytes * MemoryTopology.PeakResidentSafeFraction);

        Assert.True(peak <= ceiling,
            $"tuned GUCs oversubscribe the host: peak lower bound {peak >> 30}GiB "
            + $"> {ceiling >> 30}GiB ({MemoryTopology.PeakResidentSafeFraction:P0} of "
            + $"{MemoryTopology.TotalPhysicalBytes >> 30}GiB). "
            + $"shared_buffers={MemoryTopology.SharedBuffersBytes >> 30}GiB, "
            + $"work_mem={MemoryTopology.WorkMemBytes >> 20}MB x {maxConns}x{nodesPerQuery}, "
            + $"autovacuum={avWorkers}x{avWorkMem >> 20}MB");
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
        Assert.True(MemoryTopology.WorkMemBytesFor(60, 2) >= 16 * MiB,
            "the connection-budget derivation floors below the minimum useful work_mem");
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
    public void TheGuard_RejectsTheConfigurationThatActuallyFailed()
    {
        const long host = 125L * 1024 * MiB;          // the 125GB seed host
        long preIncidentWorkMem = 502 * MiB;          // RAM/256 clamped at 512MB
        long preIncidentShared = 31L * 1024 * MiB;    // uncapped RAM/4

        long peak = MemoryTopology.PeakResidentLowerBoundBytes(
            maxConnections: 60, nodesPerQuery: 4, autovacuumWorkers: 3,
            autovacuumWorkMemBytes: 256 * MiB,
            sharedBuffersBytes: preIncidentShared, workMemBytes: preIncidentWorkMem);

        long ceiling = (long)(host * MemoryTopology.PeakResidentSafeFraction);
        Assert.True(peak > ceiling,
            $"the guard does NOT reject the configuration that starved the host to a cold "
            + $"power boot: {peak >> 30}GiB vs ceiling {ceiling >> 30}GiB. A bound that "
            + "cannot reject the known failure is decoration.");
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
        var m = Regex.Match(TuningScript(),
            @"ecs=\$\(\(\s*mem_kb\s*\*\s*(?<num>\d+)\s*/\s*(?<den>\d+)\s*/\s*1024\s*\)\)\s*;\s*"
            + @"\(\(\s*ecs\s*<\s*(?<lo>\d+)\s*\)\).*?\(\(\s*ecs\s*>\s*(?<hi>\d+)\s*\)\)");
        Assert.True(m.Success, "pg-machine-tuning.sh no longer declares a clamped 'ecs' percentage expression");

        Assert.Equal(65, long.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture));
        Assert.Equal(100, long.Parse(m.Groups["den"].Value, CultureInfo.InvariantCulture));
        Assert.Equal(512, long.Parse(m.Groups["lo"].Value, CultureInfo.InvariantCulture));
        Assert.Equal(96L * 1024, long.Parse(m.Groups["hi"].Value, CultureInfo.InvariantCulture));
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

        // Both sides clamp to the same band: a floor because 3 is never right for flash, a
        // ceiling because these are real processes out of max_worker_processes.
        Assert.Matches(@"Math\.Clamp\(logical, 8, 32\)", emitter);
        Assert.Matches(@"iow=\$cores;\s*\(\(\s*iow\s*<\s*8\s*\)\).*?\(\(\s*iow\s*>\s*32\s*\)\)", shell);
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
        Assert.Matches(@"int workers = pcores \+ maint \+ ioWorkers \+ 8;", emitter);
        Assert.Matches(@"mwp=\$\(\(\s*pcores \+ pdeg \+ iow \+ 8\s*\)\)", TuningScript());
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
