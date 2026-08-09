using System.Text.Json;
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
    public void EveryAgentClient_LaunchesTheDeployedApphost()
    {
        const string deployed = "/opt/laplace/app/laplace-mcp";
        foreach (var relative in new[] { ".mcp.json", ".cursor/mcp.json" })
        {
            using var doc = JsonDocument.Parse(Read(relative.Split('/')));
            var servers = doc.RootElement.GetProperty("mcpServers");
            Assert.Single(servers.EnumerateObject());
            Assert.Equal(deployed, servers.GetProperty("laplace").GetProperty("command").GetString());
        }

        var codex = Read(".codex", "config.toml");
        Assert.Contains("[mcp_servers.laplace]", codex);
        Assert.Contains($"command = \"{deployed}\"", codex);

        var verifier = Read(".github", "agents", "substrate-verifier.agent.md");
        Assert.Contains("laplace/*", verifier);
        Assert.DoesNotContain("laplace-db", verifier);
        Assert.DoesNotContain("postgres-mcp", verifier);
        Assert.DoesNotContain("`psql ", verifier);
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
        Assert.Contains("laplace_require_app_dir_contract", deploy);
    }
}
