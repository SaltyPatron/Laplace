using System;
using System.IO;
using System.Text.RegularExpressions;
using Laplace.Cli;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Cli.Tests;

/// <summary>
/// PostgreSQL stores shared_buffers, effective_cache_size, temp_buffers and wal_buffers in
/// BLOCK_SIZE units (8kB by default) and rounds any other value, so a live setting equals
/// the emitted one only when the emitter aligns it. Calls the emitter behind <c>laplace
/// cpu-topology --pg-tuning</c> in process and checks every emitted block-unit GUC is a
/// multiple of 8kB.
/// </summary>
public sealed class PgTuningBlockAlignmentTests
{
    private readonly ITestOutputHelper _out;
    public PgTuningBlockAlignmentTests(ITestOutputHelper o) => _out = o;

    // GUCs whose unit is BLOCK_SIZE. The *_work_mem settings are kB-unit and not aligned.
    private static readonly string[] BlockUnitGucs =
        ["shared_buffers", "effective_cache_size", "temp_buffers", "wal_buffers"];

    private static string EmitTuning()
    {
        using var sql = new StringWriter();
        CpuTopologyCommands.EmitPgTuning(sql);
        return sql.ToString();
    }

    [Fact]
    public void EveryBlockUnitGuc_IsAMultipleOfTheBlockSize()
    {
        string sql = EmitTuning();
        // Empty emitter output fails rather than passing vacuously.
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
