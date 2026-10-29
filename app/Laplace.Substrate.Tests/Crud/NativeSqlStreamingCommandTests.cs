using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class NativeSqlStreamingCommandTests
{
    [Fact]
    public void StreamingCommandRetainsTypedPositionalContractIncludingNullArrays()
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var query = SqlCatalog.Get("generation.adjacency_manifests");
        using var command = NpgsqlRead.CreateCommand(source, query, parameters =>
        {
            parameters.AddWithValue("contexts", NpgsqlDbType.Array | NpgsqlDbType.Bytea, DBNull.Value);
            parameters.AddWithValue("limit", NpgsqlDbType.Integer, 17);
        }, timeoutSeconds: 10);
        Assert.Equal(query.Text, command.CommandText);
        Assert.Equal(10, command.CommandTimeout);
        Assert.Equal(2, command.Parameters.Count);
        Assert.All(command.Parameters.Cast<NpgsqlParameter>(), parameter => Assert.Empty(parameter.ParameterName));
        Assert.Equal(DBNull.Value, command.Parameters[0].Value);
        Assert.Equal(NpgsqlDbType.Array | NpgsqlDbType.Bytea, command.Parameters[0].NpgsqlDbType);
        Assert.Equal(17, command.Parameters[1].Value);
    }

    [Fact]
    public void StreamingCommandRejectsMissingOrMistypedParametersBeforeOpeningConnection()
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var query = SqlCatalog.Get("entity.facets");
        Assert.Throws<ArgumentException>(() => NpgsqlRead.CreateCommand(source, query));
        Assert.Throws<ArgumentException>(() => NpgsqlRead.CreateCommand(source, query,
            parameters => parameters.AddWithValue(NpgsqlDbType.Bytea, new byte[16])));
    }

    [Fact]
    public void CatalogNameOwnsStatementAndTypesEvenWhenSuppliedRecordIsAltered()
    {
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused");
        var contract = SqlCatalog.Get("entity.facets");
        var supplied = contract with { Text = "caller-selected text", ParameterTypes = ["int4"] };
        using var command = NpgsqlRead.CreateCommand(source, supplied, parameters =>
            parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea, new byte[][] { new byte[16] }));
        Assert.Equal(contract.Text, command.CommandText);
        Assert.Equal(NpgsqlDbType.Array | NpgsqlDbType.Bytea, command.Parameters[0].NpgsqlDbType);
        Assert.Empty(command.Parameters[0].ParameterName);
    }

    [Fact]
    public void ScorePairRetainsBindingOrderWhenSqlReferencesTheSecondParameterFirst()
    {
        using var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=unused");
        var query = SqlCatalog.Get("laplace.score_pair");
        using var command = NpgsqlCatalog.Command(connection, null, query, parameters =>
        {
            parameters.AddWithValue("v", 0.75d);
            parameters.AddWithValue("m", 2d);
        });
        Assert.Equal(new[] { "float8", "float8" }, query.ParameterTypes);
        Assert.Contains("laplace.laplace_score($1, $2)", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("laplace.laplace_score_inverse(s.score, $2)", command.CommandText, StringComparison.Ordinal);
        Assert.Equal(0.75d, command.Parameters[0].Value);
        Assert.Equal(2d, command.Parameters[1].Value);
        Assert.All(command.Parameters.Cast<NpgsqlParameter>(), parameter => Assert.Empty(parameter.ParameterName));
    }

    [Fact]
    public void CatalogCommandsRetainSmallintArraysAndTypedNullsOnConnectionsAndBatches()
    {
        using var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=unused");
        var trajectories = SqlCatalog.Get("generation.typed_trajectory_constituents");
        using var command = NpgsqlCatalog.Command(connection, null, trajectories, parameters =>
        {
            parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { new byte[16] });
            parameters.AddWithValue("types", NpgsqlDbType.Array | NpgsqlDbType.Smallint, new short[] { 1, 3 });
        });
        Assert.Equal(NpgsqlDbType.Array | NpgsqlDbType.Smallint, command.Parameters[1].NpgsqlDbType);
        var sourceStatus = SqlCatalog.Get("ops.source_status");
        var batch = NpgsqlCatalog.Command(sourceStatus, parameters =>
            parameters.AddWithValue("source", NpgsqlDbType.Text, DBNull.Value));
        Assert.Equal(sourceStatus.Text, batch.CommandText);
        Assert.Equal(NpgsqlDbType.Text, batch.Parameters[0].NpgsqlDbType);
        Assert.Equal(DBNull.Value, batch.Parameters[0].Value);
        Assert.Empty(batch.Parameters[0].ParameterName);
    }

    [Fact]
    public void SingleReferenceArrayIsOnePostgresArgumentForEitherCommandOwner()
    {
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused");
        byte[][] ids = [new byte[16], new byte[16]];
        using var entities = NpgsqlCatalog.Command(source, "entity.facets", ids);
        Assert.Single(entities.Parameters);
        Assert.Same(ids, entities.Parameters[0].Value);
        Assert.Equal("bytea[]", entities.Parameters[0].DataTypeName);

        using var connection = new NpgsqlConnection(source.ConnectionString);
        string[] tags = ["en", "eng"];
        using var language = NpgsqlCatalog.Command(connection, null, "language.tags", tags);
        Assert.Single(language.Parameters);
        Assert.Same(tags, language.Parameters[0].Value);
        Assert.Equal("text[]", language.Parameters[0].DataTypeName);
    }

    [Theory]
    [InlineData("laplace.entities; VACUUM")]
    [InlineData("laplace.entities WHERE true")]
    [InlineData("laplace.entities.extra")]
    [InlineData("laplace.entities\n")]
    public void MaintenanceRelationSlotRejectsStatementsAndUnquotedSyntax(string relation)
    {
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused");
        Assert.Throws<ArgumentException>(() => NpgsqlCatalog.VacuumCommand(source, relation, false, false));
    }

    [Fact]
    public void SourceAttributionBindsTheWholeScopeAsOneBinaryArray()
    {
        using var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=unused");
        byte[][] ids = [Enumerable.Repeat((byte)0xff, 16).ToArray(), new byte[16]];
        using var command = NpgsqlCatalog.Command(connection, null, "generation.export_source_attribution", ids, ids);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Same(ids, command.Parameters[1].Value);
        Assert.Equal("bytea[]", command.Parameters[1].DataTypeName);
        Assert.DoesNotContain(Convert.ToHexString(ids[0]), command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(';', command.CommandText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public async Task SourceScopedPathRejectsMalformedIdsBeforeAnyConnection(int length)
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused");
        await Assert.ThrowsAsync<ArgumentException>(() => NpgsqlFoundryReads.PathPlaneAsync(
            source, [], "[]", 10, 10, false, [new byte[length]]));
    }

    [Theory]
    [InlineData("consensus.upsert", false, false)]
    [InlineData("consensus.upsert_epoch", false, true)]
    [InlineData("consensus.upsert_type", true, false)]
    [InlineData("consensus.upsert_type_epoch", true, true)]
    public void ConsensusRoutesRetainScalarVersusArrayTypeAndOneStatementEpochOrdering(
        string name, bool direct, bool epoch)
    {
        using var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=unused");
        object[] values = Enumerable.Repeat<object>(Array.Empty<long>(), 13).ToArray();
        values[0] = direct ? new byte[16] : new byte[][] { new byte[16] };
        values[1] = values[2] = Array.Empty<byte[]>();
        values[6] = Array.Empty<DateTime>();
        using var command = NpgsqlCatalog.Command(connection, null, name, arguments: values);
        Assert.Equal(13, command.Parameters.Count);
        Assert.Equal(direct ? "bytea" : "bytea[]", command.Parameters[0].DataTypeName);
        Assert.Equal("timestamptz[]", command.Parameters[6].DataTypeName);
        Assert.Equal(epoch, command.CommandText.Contains("MATERIALIZED", StringComparison.Ordinal));
        Assert.DoesNotContain(';', command.CommandText);
    }
}
