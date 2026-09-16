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
        var query = new NativeSqlQuery("stream.contract", "SELECT $1,$2", ["bytea[]", "int4"]);
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
        var query = new NativeSqlQuery("stream.contract", "SELECT $1", ["bytea[]"]);
        Assert.Throws<ArgumentException>(() => NpgsqlRead.CreateCommand(source, query));
        Assert.Throws<ArgumentException>(() => NpgsqlRead.CreateCommand(source, query,
            parameters => parameters.AddWithValue(NpgsqlDbType.Bytea, new byte[16])));
    }
}
