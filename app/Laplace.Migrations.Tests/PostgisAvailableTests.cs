using Npgsql;
using Xunit;

namespace Laplace.Migrations.Tests;

/// <summary>
/// Baseline: verify the postgis/postgis:18 Testcontainer image starts and
/// PostGIS is callable. Real DbUp-on-container tests (idempotency of our
/// 20260606000000_layer1_database.sql migration, role grants, etc.)
/// land alongside the full DbUp test harness — these are the smoke tests
/// proving the Testcontainers + Npgsql wiring works.
/// </summary>
public class PostgisAvailableTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisAvailableTests(PostgisContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PostgisExtensionLoadable()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS postgis;", conn);
        await cmd.ExecuteNonQueryAsync();

        await using var versionCmd = new NpgsqlCommand("SELECT postgis_full_version();", conn);
        var version = (string?)await versionCmd.ExecuteScalarAsync();
        Assert.NotNull(version);
        Assert.Contains("POSTGIS=", version);
    }
}
