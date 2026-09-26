using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// A read-only SPI plan must be prepared parallel-eligible.
///
/// SPI_prepare plans with parallelism disabled; a parallel plan requires
/// SPI_prepare_cursor(..., CURSOR_OPT_PARALLEL_OK). Scans such as the successor probes are
/// GIN containment scans over every hash partition of laplace.physicalities (partitioned by
/// id, filtered on constituents, so nothing prunes) and need a Parallel Append. The gate
/// covers every extension source file, not a chosen few.
/// </summary>
public sealed class SpiParallelPlanGateTests
{
    /// <summary>
    /// Files whose plans execute read-write (SPI_execute_plan with read_only = false);
    /// parallel plans are not available to them.
    /// </summary>
    private static readonly HashSet<string> ReadWritePlanFiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "fold_route.c",
            "chess_rating_repair.c",
            "conversation_session.c",
            "consensus_bulk_write.c",
        };

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
                var lineEnd = text.IndexOf('\n', m.Index);
                var line = text[lineStart..(lineEnd >= 0 ? lineEnd : text.Length)];
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
    /// Every exempt file really executes a plan read-write. The SPI call's top-level
    /// arguments are parsed, so a nested call such as matched_leaf_plan(...) cannot hide the
    /// read_only=false argument.
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
            Assert.True(ContainsReadWriteSpiCall(File.ReadAllText(path)),
                $"exempt file has no SPI call with read_only=false: {name}");
        }
    }

    private static bool ContainsReadWriteSpiCall(string text)
    {
        foreach ((string name, int readOnlyIndex) in new[]
        {
            ("SPI_execute_plan", 3),
            ("SPI_execute_with_args", 5),
            ("SPI_cursor_open", 4),
        })
        {
            int search = 0;
            while ((search = text.IndexOf(name + "(", search, StringComparison.Ordinal)) >= 0)
            {
                int open = search + name.Length;
                var arguments = new List<string>();
                int depth = 0;
                int argumentStart = open + 1;
                bool complete = false;

                for (int i = argumentStart; i < text.Length; ++i)
                {
                    switch (text[i])
                    {
                        case '(':
                            depth++;
                            break;
                        case ')' when depth > 0:
                            depth--;
                            break;
                        case ',' when depth == 0:
                            arguments.Add(text[argumentStart..i].Trim());
                            argumentStart = i + 1;
                            break;
                        case ')' when depth == 0:
                            arguments.Add(text[argumentStart..i].Trim());
                            search = i + 1;
                            complete = true;
                            break;
                    }
                    if (complete) break;
                }

                if (!complete)
                {
                    search = open + 1;
                    continue;
                }
                if (arguments.Count > readOnlyIndex
                    && string.Equals(arguments[readOnlyIndex], "false", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }
}
