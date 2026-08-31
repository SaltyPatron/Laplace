using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The agent boundary is one deployed MCP apphost exposing typed tools and named
/// operations. Arbitrary SQL and per-client launchers are architecture regressions,
/// not alternate debugging modes.
/// </summary>
public sealed class McpBoundaryGateTests
{
    private static string Root => TypeIdLawTests.FindRepoRootPublic();
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, .. parts]));

    [Fact]
    public void McpSurface_HasNoFreeFormSqlLane()
    {
        var tools = Read("app", "Laplace.Endpoints.Mcp", "SubstrateTools.cs");
        var program = Read("app", "Laplace.Endpoints.Mcp", "Program.cs");
        var launcher = Read("scripts", "laplace-mcp");

        foreach (var forbidden in new[]
                 {
                     "LAPLACE_MCP_OPERATOR", "OperatorLane", "ExecuteSql(",
                     "laplace-sql-gap", "operator_lane"
                 })
        {
            Assert.DoesNotContain(forbidden, tools);
            Assert.DoesNotContain(forbidden, program);
            Assert.DoesNotContain(forbidden, launcher);
        }

        Assert.DoesNotContain("new(\"sql\"", tools);
        Assert.Contains("new(\"op\"", tools);
        Assert.Contains("new(\"mcp_runtime\"", tools);
    }

    [Fact]
    public void RepositoryLauncher_FallsBackToTheDeployedApphost()
    {
        // PR #1360 deliberately removed .mcp.json, .cursor/, and .codex/ from the
        // tracked source boundary. Client-specific configuration is local state;
        // the repository-owned contract is the canonical launcher and its deployed
        // apphost fallback.
        var launcher = Read("scripts", "laplace-mcp");
        Assert.Contains(
            "APPHOST=\"${LAPLACE_APP_DIR:-/opt/laplace/app}/laplace-mcp\"",
            launcher,
            StringComparison.Ordinal);
        Assert.Contains(
            "[[ -x \"$APPHOST\" ]] && exec \"$APPHOST\" \"$@\"",
            launcher,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeployRequiresBootstrapOwnedMcpRuntime()
    {
        var bootstrap = Read("deploy", "linux", "bootstrap-host.sh");
        var contract = Read("deploy", "linux", "app-dir-contract.sh");
        var deploy = Read("deploy", "linux", "deploy.sh");

        Assert.Contains("$APP_DIR/mcp-runtime", bootstrap);
        Assert.Contains("$app_dir/mcp-runtime", contract);
        Assert.DoesNotContain("mkdir -p \"$APP_DIR/$MCP_RUNTIME_DIR\"", deploy);
        Assert.Contains("laplace_reconcile_app_dir_contract", contract);
        Assert.Contains("laplace_reconcile_app_dir_contract \"$APP_DIR\"", deploy);
        Assert.Contains("laplace_require_app_dir_contract", deploy);
    }
}
