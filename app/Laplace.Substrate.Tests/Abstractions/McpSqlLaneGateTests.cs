using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// GH #863 acceptance 3: a gate asserting no MCP tool accepts free-form SQL
/// outside the operator lane. The typed surface is `op` (live-catalog, bound
/// args, no SQL across the boundary); `sql` survives only as the operator-lane
/// debugging escape hatch behind LAPLACE_MCP_OPERATOR=1, off by default.
/// These pins fail the build if the gate is loosened: the lane flag deleted,
/// the listing filter dropped, the dispatch un-gated, or a second free-form
/// SQL entry point added beside the sanctioned one.
/// </summary>
public sealed class McpSqlLaneGateTests
{
    private static string ToolsSource()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var path = Path.Combine(repoRoot, "app", "Laplace.Endpoints.Mcp", "SubstrateTools.cs");
        Assert.True(File.Exists(path), $"MCP tool surface moved: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void SqlTool_IsOperatorLaneOnly_OffByDefault()
    {
        var src = ToolsSource();
        Assert.Contains("LAPLACE_MCP_OPERATOR", src);          // the off-by-default flag exists
        Assert.Contains("OperatorLane || t.Name != \"sql\"", src); // hidden from listing off-lane

        // Dispatch is gated on the lane. Pinned as the INVARIANT (ExecuteSql is
        // only reachable behind OperatorLane), not as one syntax for it: the
        // previous pin was the literal switch arm `"sql" => OperatorLane`, and
        // when #913 moved dispatch into a compile-enforced ToolSpec Handler the
        // gate got STRONGER while the grep went red. A test that fails on a
        // refactor it should be indifferent to is a false alarm, and a false
        // alarm on a security gate is worse than no alarm.
        var flat = new string(src.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.True(
            flat.Contains("OperatorLane?s.ExecuteSql(")   // ToolSpec handler ternary
            || flat.Contains("\"sql\"=>OperatorLane"),    // legacy switch arm
            "free-form SQL dispatch is no longer gated on OperatorLane — the `sql` "
            + "hatch must stay operator-lane only (GH #863).");
    }

    [Fact]
    public void NoSecondFreeFormSqlEntryPoint()
    {
        var src = ToolsSource();
        // ExecuteSql is the one free-form executor; its only dispatch site is the
        // operator-gated "sql" case. A second call site is a new backdoor.
        int dispatchSites = src.Split("ExecuteSql(").Length - 1;
        Assert.True(dispatchSites <= 2,
            $"ExecuteSql referenced {dispatchSites}x — expected its definition plus the one "
            + "operator-gated dispatch. A new free-form SQL entry point is the #863 backdoor "
            + "pattern; route it through op instead.");
    }
}
