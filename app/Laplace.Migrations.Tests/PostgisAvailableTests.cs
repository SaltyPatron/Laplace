using Npgsql;
using Xunit;

namespace Laplace.Migrations.Tests;

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
