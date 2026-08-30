using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Regression gate for GH #933: endpoint execution may borrow a PostgreSQL pool,
/// but a request handler must never create its own NpgsqlDataSource. Pool limits are
/// per datasource, so per-request construction multiplies the process connection
/// budget even when every individual datasource is correctly capped.
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

        Assert.Equal(1, Regex.Matches(owner,
            @"LaplaceDataSource\.Create\(SubstrateAccess\.Serving\)").Count);
        Assert.Equal(1, Regex.Matches(owner,
            @"LaplaceDataSource\.Create\(SubstrateAccess\.Ingest\)").Count);
    }

    [Fact]
    public void AdminMaintenance_MapsPoolAcquisitionFailuresToUnavailable()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var endpoint = File.ReadAllText(Path.Combine(
            repoRoot, "app", "Laplace.Endpoints.OpenAICompat", "EndpointMappings.Admin.cs"));

        var routeAt = endpoint.IndexOf("/v1/admin/maintenance/vacuum", StringComparison.Ordinal);
        var tryAt = endpoint.IndexOf("try\n            {", routeAt, StringComparison.Ordinal);
        var resolveAt = endpoint.IndexOf("ResolveSubstrateTableAsync", routeAt, StringComparison.Ordinal);
        var vacuumAt = endpoint.IndexOf("NpgsqlMaintenance.VacuumAsync", routeAt, StringComparison.Ordinal);
        var unavailableAt = endpoint.IndexOf(
            "ServiceUnavailable(\"substrate_unavailable\"", routeAt, StringComparison.Ordinal);

        Assert.True(routeAt >= 0);
        Assert.True(tryAt > routeAt);
        Assert.True(resolveAt > tryAt);
        Assert.True(vacuumAt > resolveAt);
        Assert.True(unavailableAt > vacuumAt);
        Assert.Contains("catch (NpgsqlException ex)", endpoint[routeAt..]);
        Assert.Contains("catch (TimeoutException ex)", endpoint[routeAt..]);
    }
}
