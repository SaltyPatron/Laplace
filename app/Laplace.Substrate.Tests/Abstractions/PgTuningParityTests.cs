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

        var missing = emitted.Except(shellSet).OrderBy(g => g, StringComparer.Ordinal).ToList();
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
}
