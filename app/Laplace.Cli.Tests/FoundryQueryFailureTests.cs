using Laplace.Engine.Core;
using Npgsql;
using Xunit;

namespace Laplace.Cli.Tests;

[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class FoundryQueryFailureTests : IAsyncLifetime
{
    private readonly string _database = "laplace_foundry_failure_" + Guid.NewGuid().ToString("N");
    private NpgsqlDataSource _admin = null!;
    private NpgsqlDataSource _data = null!;
    private bool _created;

    public async Task InitializeAsync()
    {
        var connection = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("LAPLACE_FOUNDRY_TEST_CONNECTION")
            ?? LaplaceInstall.PostgresConnectionString("postgres")) { Pooling = false };
        _admin = NpgsqlDataSource.Create(connection.ConnectionString);
        await using (var create = _admin.CreateCommand($"CREATE DATABASE \"{_database}\""))
            await create.ExecuteNonQueryAsync();
        _created = true;
        connection.Database = _database;
        _data = NpgsqlDataSource.Create(connection.ConnectionString);
        await using var schemas = _data.CreateCommand("CREATE SCHEMA generation; CREATE SCHEMA laplace; CREATE SCHEMA consensus;");
        await schemas.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_data is not null) await _data.DisposeAsync();
        if (_created)
        {
            await using var drop = _admin.CreateCommand($"DROP DATABASE \"{_database}\"");
            await drop.ExecuteNonQueryAsync();
        }
        if (_admin is not null) await _admin.DisposeAsync();
    }

    [Fact]
    public async Task MissingRequiredFunctionsCannotBecomeSuccessfulEmptyModelInputs()
    {
        var vocabulary = new Dictionary<Hash128, List<int>> { [Hash128.Zero] = [0] };
        Func<Task>[] readers =
        [
            async () => { await FoundryExport.ReadRelationPlaneAsync(_data, FoundryExport.PlaneSpec.Consensus("IS_A"), vocabulary, 8); },
            async () => { await FoundryExport.ReadConsensusPlaneAsync(_data, ["IS_A"], vocabulary, 8); },
            async () => { await FoundryExport.ReadLayerPlaneAsync(_data, 0, 1, vocabulary, 8); },
            async () => { await FoundryExport.ReadLayerPlaneMaskedAsync(_data, default, vocabulary, 8); },
            async () => { await FoundryExport.ReadTypePlanesAsync(_data, vocabulary, 8); },
            async () => { await FoundryExport.FillHilbertKeysAsync(_data, vocabulary, 1); },
            async () => { await FoundryExport.ReadTrajectoryLadderAsync(_data, 2, vocabulary, 8); },
            async () => { await FoundryExport.ReadSentenceOrderAsync(_data, vocabulary); },
            async () => { await FoundryExport.ReadTrajectoryStrideAsync(_data, 2, vocabulary, 8); },
            async () => { await FoundryExport.FillHighwayMasksAsync(_data, vocabulary, 1); },
        ];
        foreach (var reader in readers)
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(reader);
            var cause = Assert.IsType<PostgresException>(refused.InnerException);
            Assert.Contains(cause.SqlState, new[] { "42P01", "42883" });
            Assert.Contains("export refused", refused.Message, StringComparison.OrdinalIgnoreCase);
        }

        // A genuine empty result remains distinct from an unavailable query. It also
        // reaches the separate refresh failure path after the masks read succeeds.
        await using (var emptyReaders = _data.CreateCommand("""
            CREATE FUNCTION generation.entity_highway_masks(bytea[])
            RETURNS TABLE(entity_id bytea, highway_mask bytea) LANGUAGE sql
            AS $$ SELECT NULL::bytea, NULL::bytea WHERE false $$;
            CREATE FUNCTION generation.entity_relation_plane(bytea[], text[], integer)
            RETURNS TABLE(subject_id bytea, object_id bytea, w double precision) LANGUAGE sql
            AS $$ SELECT NULL::bytea, NULL::bytea, 0::double precision WHERE false $$;
            """))
            await emptyReaders.ExecuteNonQueryAsync();
        Assert.Equal(0, (await FoundryExport.ReadConsensusPlaneAsync(_data, ["IS_A"], vocabulary, 8)).Nnz);
        var refreshFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await FoundryExport.FillHighwayMasksAsync(_data, vocabulary, 1));
        Assert.Contains("highway_mask_refresh", refreshFailure.Message);
        Assert.IsType<PostgresException>(refreshFailure.InnerException);
    }
}
