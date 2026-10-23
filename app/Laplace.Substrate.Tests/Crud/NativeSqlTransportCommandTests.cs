using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class NativeSqlTransportCommandTests
{
    [Fact]
    public void FixedUtilitySequencePreservesOrderAndRejectsParameterizedStatements()
    {
        using var connection = new NpgsqlConnection();
        using var command = NpgsqlCatalog.Command(connection, null,
            new[] { "write.settings_sync", "write.advance_epoch" });
        Assert.True(command.CommandText.IndexOf("synchronous_commit", StringComparison.Ordinal)
            < command.CommandText.IndexOf("nextval", StringComparison.Ordinal));
        Assert.Empty(command.Parameters);
        Assert.Throws<ArgumentException>(() => NpgsqlCatalog.Command(connection, null,
            new[] { "write.settings_sync", "advisory.timeout_local" }));
        Assert.Throws<ArgumentException>(() => NpgsqlCatalog.Command(connection, null, Array.Empty<string>()));
    }

    [Fact]
    public void ActiveCatalogCommandsSeparateIdentifiersFromBinaryValuesAndRejectStatementInjection()
    {
        using var connection = new NpgsqlConnection();
        var identifiers = new Dictionary<string, string> { ["relation"] = "laplace.physicalities" };
        byte[] id = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        using var utility = NpgsqlCatalog.Command(connection, null, "maintenance.vacuum_plain_relation", identifiers: identifiers);
        Assert.Equal("VACUUM laplace.physicalities", utility.CommandText);
        Assert.Empty(utility.Parameters);
        using var query = NpgsqlCatalog.Command(connection, null, "conversation.lock", id);
        Assert.Equal("bytea", query.Parameters[0].DataTypeName);
        Assert.Same(id, query.Parameters[0].Value);
        identifiers["relation"] = "x; DROP SCHEMA public";
        Assert.Throws<ArgumentException>(() => NpgsqlCatalog.Command(connection, null, "maintenance.vacuum_plain_relation", identifiers: identifiers));
        Assert.Throws<ArgumentException>(() => NpgsqlCatalog.Command(connection, null, "maintenance.vacuum_plain_relation",
            identifiers: new Dictionary<string, string>()));
    }

    [Fact]
    public void JournalBatchRetainsEmptyAndNullableTimestampArraysWithExplicitTypes()
    {
        var timestamps = new DateTimeOffset?[] { null, DateTimeOffset.UnixEpoch };
        var command = NpgsqlCatalog.Command(SqlCatalog.Get("journal.files_started"), parameters =>
        {
            parameters.AddWithValue(NpgsqlDbType.Uuid, Guid.Empty);
            parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, new[] { "a", "b" });
            parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, new[] { "source", "source" });
            parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, new long[] { 0, 1 });
            parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, timestamps);
            parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
                new[] { DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch });
        });
        Assert.Same(timestamps, command.Parameters[4].Value);
        Assert.All(command.Parameters.Cast<NpgsqlParameter>(), parameter => Assert.Empty(parameter.ParameterName));
    }

    [Theory]
    [InlineData(IntentStageTable.Entities)]
    [InlineData(IntentStageTable.Physicalities)]
    [InlineData(IntentStageTable.Attestations)]
    public void BinaryCopyUsesTheActualNativeColumnContract(IntentStageTable table)
        => Assert.EndsWith("FROM STDIN (FORMAT BINARY)", NpgsqlCatalog.BinaryCopyStatement(table), StringComparison.Ordinal);
}
