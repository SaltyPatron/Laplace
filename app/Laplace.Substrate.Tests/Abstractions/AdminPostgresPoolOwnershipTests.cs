using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Admin endpoints borrow the host-lifetime serving and ingest pools; no request handler
/// creates its own NpgsqlDataSource. Pool limits are per datasource, so per-request
/// construction would multiply the process connection budget.
/// </summary>
public sealed class AdminPostgresPoolOwnershipTests
{
    [Fact]
    public void AdminMaintenance_BorrowsHostLifetimePools()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var endpoint = File.ReadAllText(Path.Combine(
            repoRoot, "app", "Laplace.Endpoints.OpenAICompat", "EndpointMappings.Admin.cs"));
        var owner = File.ReadAllText(Path.Combine(
            repoRoot, "app", "Laplace.Endpoints.OpenAICompat", "AdminPostgresDataSources.cs"));
        var program = File.ReadAllText(Path.Combine(
            repoRoot, "app", "Laplace.Endpoints.OpenAICompat", "Program.cs"));

        Assert.DoesNotContain("LaplaceDataSource.Create(", endpoint);
        Assert.Contains("AdminPostgresDataSources dataSources", endpoint);
        Assert.Contains("dataSources.Serving", endpoint);
        Assert.Contains("dataSources.Ingest", endpoint);
        Assert.Contains("AddSingleton<AdminPostgresDataSources>()", program);

        Assert.Single(Regex.Matches(owner,
            @"LaplaceDataSource\.Create\(SubstrateAccess\.Serving\)"));
        Assert.Single(Regex.Matches(owner,
            @"LaplaceDataSource\.Create\(SubstrateAccess\.Ingest\)"));
    }

    [Fact]
    public void AdminMaintenance_MapsPoolAcquisitionFailuresToUnavailable()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var endpoint = File.ReadAllText(Path.Combine(
            repoRoot, "app", "Laplace.Endpoints.OpenAICompat", "EndpointMappings.Admin.cs"));

        var route = Assert.Single(CSharpSyntaxTree.ParseText(endpoint).GetRoot()
            .DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
                && literal.Token.ValueText == "/v1/admin/maintenance/vacuum");
        var guarded = Assert.Single(route.DescendantNodes().OfType<TryStatementSyntax>(), statement =>
            statement.Block.ToString().Contains("NpgsqlMaintenance.VacuumAsync", StringComparison.Ordinal));
        Assert.Contains("ResolveSubstrateTableAsync", guarded.Block.ToString());
        foreach (string type in new[] { "NpgsqlException", "TimeoutException" })
        {
            var handler = Assert.Single(guarded.Catches, handler => handler.Declaration?.Type.ToString() == type);
            Assert.Contains("ServiceUnavailable(\"substrate_unavailable\"", handler.Block.ToString());
        }
    }
}
