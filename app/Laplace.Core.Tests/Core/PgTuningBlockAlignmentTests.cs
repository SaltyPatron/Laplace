using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// PostgreSQL stores shared_buffers, effective_cache_size, temp_buffers and wal_buffers in
/// BLOCK_SIZE units (8kB by default). A value that is not a multiple of 8kB is rounded, so
/// the live setting never equals what was asked.
///
/// setup-host.sh compares the live setting against the machine-sized expectation, so it
/// could NEVER pass:
///
///   emitted 32949791kB -> 4118723.875 blocks -> PG stores 32949792kB  X shared_buffers
///   emitted 65899582kB -> 8237447.75  blocks -> PG stores 65899584kB  X effective_cache_size
///
/// Both were reported as "want machine-sized; not pending alone" on a host where the tuning
/// HAD applied correctly, and the script's verdict was "Tuning NOT fully live" against a
/// healthy cluster, every run.
///
/// This reads the emitter's own output rather than reimplementing the arithmetic, so it
/// fails if any future block-unit GUC is added unaligned.
/// </summary>
public sealed class PgTuningBlockAlignmentTests
{
    private readonly ITestOutputHelper _out;
    public PgTuningBlockAlignmentTests(ITestOutputHelper o) => _out = o;

    // PostgreSQL GUCs whose unit is BLOCK_SIZE. work_mem / maintenance_work_mem /
    // autovacuum_work_mem are kB-unit and must NOT be aligned -- aligning them would be a
    // different bug, silently shrinking them.
    private static readonly string[] BlockUnitGucs =
        ["shared_buffers", "effective_cache_size", "temp_buffers", "wal_buffers"];

    private static string EmitTuning()
    {
        string repo = CustomAttributeExtensions
            .GetCustomAttributes<AssemblyMetadataAttribute>(
                typeof(PgTuningBlockAlignmentTests).Assembly)
            .First(a => a.Key == "LaplaceRepoRoot").Value!;
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "run", "--no-build", "-c", "Release", "--project",
                                  "app/Laplace.Cli/Laplace.Cli.csproj", "--",
                                  "cpu-topology", "--pg-tuning" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return outp;
    }

    [Fact]
    public void EveryBlockUnitGuc_IsAMultipleOfTheBlockSize()
    {
        string sql = EmitTuning();
        // Emitting nothing must fail, not pass: a gate with no input verifies nothing.
        Assert.Contains("ALTER SYSTEM", sql);

        int checkedGucs = 0;
        foreach (Match m in Regex.Matches(sql, @"ALTER SYSTEM SET (\w+) = '(\d+)kB';"))
        {
            string guc = m.Groups[1].Value;
            if (!BlockUnitGucs.Contains(guc)) continue;
            checkedGucs++;
            long kb = long.Parse(m.Groups[2].Value);
            _out.WriteLine($"{guc} = {kb}kB = {kb / 8.0} blocks");
            Assert.True(kb % 8 == 0,
                $"{guc} = {kb}kB is {kb / 8.0} blocks. PostgreSQL stores it in 8kB units and "
                + $"will round to {((kb + 7) / 8) * 8}kB, so the live setting can never equal "
                + "the machine-sized expectation and setup-host.sh reports "
                + "'Tuning NOT fully live' against a healthy cluster.");
        }

        Assert.True(checkedGucs >= 2,
            $"only {checkedGucs} block-unit GUCs found in the emitted tuning — the regex or "
            + "the emitter changed and this gate verified almost nothing");
    }
}
