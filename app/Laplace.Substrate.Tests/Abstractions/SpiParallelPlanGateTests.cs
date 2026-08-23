using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// A read-only SPI plan must be prepared parallel-eligible.
///
/// SPI_prepare plans with parallelism DISABLED. A parallel plan requires
/// SPI_prepare_cursor(..., CURSOR_OPT_PARALLEL_OK). Before 2026-08-23 no file in the
/// extension used it, so every SPI plan in the tree was serial — including the successor
/// probes, which are GIN containment scans over all 64 hash partitions of
/// laplace.physicalities (the partition key is `id`, the predicate is on constituents, so
/// nothing prunes). Standalone the planner picks a Parallel Append with 7 workers at ~42ms;
/// through SPI_prepare the identical query ran serially. Measured warm after the change:
/// generation.trajectory_continuations 687ms → 173ms, structural.geometry_successors_batch
/// 975ms → 127ms, results bit-identical.
///
/// This is a whole-class gate on purpose. Fixing the two probes that happened to be
/// measured would have left thirteen other files silently serial until someone profiled
/// them one at a time.
/// </summary>
public sealed class SpiParallelPlanGateTests
{
    /// <summary>
    /// Files whose plans are executed READ-WRITE (SPI_execute_plan with read_only = false).
    /// Parallelism is not available to them and asking for it would be wrong, not slow.
    /// </summary>
    private static readonly HashSet<string> ReadWritePlanFiles =
        new(StringComparer.OrdinalIgnoreCase) { "fold_route.c" };

    // Matches a real call, not the "SPI_prepare(unpack) failed" text inside elog messages.
    private static readonly Regex SerialPrepare = new(
        @"(?<!""|_cursor)\bSPI_prepare\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void ReadOnlySpiPlans_ArePreparedParallelEligible()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var srcRoot = Path.Combine(repoRoot, "extension", "laplace_substrate", "src");
        Assert.True(Directory.Exists(srcRoot), $"extension source root missing: {srcRoot}");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.c").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (ReadWritePlanFiles.Contains(name)) continue;

            var text = File.ReadAllText(file);
            foreach (Match m in SerialPrepare.Matches(text))
            {
                // Ignore matches inside a string literal on the same line (elog messages).
                var lineStart = text.LastIndexOf('\n', m.Index) + 1;
                var line = text[lineStart..text.IndexOf('\n', m.Index)];
                if (line.TrimStart().StartsWith("*") || line.Contains("failed")) continue;
                var lineNo = text.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{name}:{lineNo}");
            }
        }

        Assert.True(offenders.Count == 0,
            "read-only SPI plans must use SPI_prepare_cursor(..., CURSOR_OPT_PARALLEL_OK); "
            + "SPI_prepare plans them serial:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The exemption must stay a real, checkable claim rather than a way to opt out.
    /// </summary>
    [Fact]
    public void ReadWriteExemptions_ActuallyExecuteReadWrite()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var srcRoot = Path.Combine(repoRoot, "extension", "laplace_substrate", "src");

        foreach (var name in ReadWritePlanFiles)
        {
            var path = Path.Combine(srcRoot, name);
            Assert.True(File.Exists(path), $"exempt file does not exist: {name}");
            Assert.Matches(
                new Regex(@"SPI_execute_plan\([^)]*,\s*false\s*,", RegexOptions.Singleline),
                File.ReadAllText(path));
        }
    }
}
