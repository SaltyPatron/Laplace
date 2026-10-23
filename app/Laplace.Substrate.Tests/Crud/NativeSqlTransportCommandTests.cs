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
    public void StageTemplateSeparatesIdentifiersFromBinaryValuesAndRejectsStatementInjection()
    {
        using var connection = new NpgsqlConnection();
        var identifiers = new Dictionary<string, string> { ["stage"] = "laplace_stage_abcd" };
        byte[] id = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        using var command = NpgsqlCatalog.Command(connection, null, "stage.exclude_type", identifiers, id);
        Assert.Contains("laplace_stage_abcd.excluded_type", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("{stage}", command.CommandText, StringComparison.Ordinal);
        Assert.Equal("bytea", command.Parameters[0].DataTypeName);
        Assert.Same(id, command.Parameters[0].Value);
        identifiers["stage"] = "x; DROP SCHEMA public";
        Assert.Throws<ArgumentException>(() => NpgsqlCatalog.Command(connection, null, "stage.exclude_type", identifiers, id));
        Assert.Throws<ArgumentException>(() => NpgsqlCatalog.Command(connection, null, "stage.exclude_type",
            new Dictionary<string, string>(), id));
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
