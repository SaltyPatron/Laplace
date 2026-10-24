using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class UnicodeSeedIntegrationTests
{
    [Fact]
    public async Task ScalarFloorRemainsClientSideWithoutPreseededDatabaseCodepoints()
    {
        CodepointPerfcache.LoadDefault();
        Assert.Equal(1_112_064, CodepointPerfcache.Count);
        await using var source = LaplaceDataSource.Create(SubstrateAccess.Ingest);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = NpgsqlCatalog.Command(connection, null, "verify.unseeded_scalar_floor");
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }
}