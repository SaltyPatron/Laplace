using System.Text;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

// Actual canonical COPY/merge/readback in the selected installed database.
// Each test rolls back its own transaction; no database or cluster is created.
[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class StagedSourceWriterTests
{
    [Fact]
    public async Task NativeWindowRoundTripsAndPrunesItsRecordedTrunk()
    {
        string text = $"Canonical native window {Guid.NewGuid():N}: Cafe\u0301 🚀 repeated repeated.";
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await using var source = LaplaceDataSource.Create(SubstrateAccess.Ingest);
        var writer = new NpgsqlSubstrateWriter(source);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var window = new StagedSourceWriter(10000, 100000, 1 << 24);
        var root = window.Text(bytes);
        var first = await writer.ApplyCanonicalWorkingSetAsync(window, connection, transaction, [root.Id]);
        Assert.True(first.Entities > 0);
        Assert.Equal(first.Entities, first.Physicalities);
        await using (var read = NpgsqlCatalog.Command(connection, transaction,
            "verify.canonical_staged_root", root.Id))
        await using (var reader = await read.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(root.Id, reader.GetFieldValue<Hash128>(0));
            Assert.Equal(root.Tier, reader.GetInt16(1));
            Assert.Equal(text, reader.GetString(2));
            // Native composes this root through the UAX #29 tiers. A flat
            // codepoint composition has a different coordinate. Verify exact
            // transport of all four fixed-point coordinates Native emitted.
            Assert.Equal(Math.ScaleB(root.X, -53), reader.GetDouble(3));
            Assert.Equal(Math.ScaleB(root.Y, -53), reader.GetDouble(4));
            Assert.Equal(Math.ScaleB(root.Z, -53), reader.GetDouble(5));
            Assert.Equal(Math.ScaleB(root.W, -53), reader.GetDouble(6));
            Assert.True(reader.GetBoolean(7));
        }
        using var again = new StagedSourceWriter(10000, 100000, 1 << 24);
        var sameRoot = again.Text(bytes);
        var dedup = await writer.ApplyCanonicalWorkingSetAsync(again, connection, transaction, [sameRoot.Id]);
        Assert.Equal(new StagedWriteResult(0, 0, 0, 1, 1), dedup);
        await transaction.RollbackAsync();
        await using var absent = NpgsqlCatalog.Command(connection, null, "write.canonical_recorded",
            new Hash128[] { root.Id });
        Assert.Equal(new[] { false }, (bool[])(await absent.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RejectedWriteDoesNotPublishAPartialGraph()
    {
        await using var source = LaplaceDataSource.Create(SubstrateAccess.Ingest);
        var writer = new NpgsqlSubstrateWriter(source);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var readOnly = NpgsqlCatalog.Command(connection, transaction, "verify.canonical_readonly"))
            await readOnly.ExecuteNonQueryAsync();
        using var window = new StagedSourceWriter(10000, 100000, 1 << 24);
        var root = window.Text(Encoding.UTF8.GetBytes($"Rejected native window {Guid.NewGuid():N}"));
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            writer.ApplyCanonicalWorkingSetAsync(window, connection, transaction, [root.Id]));
        Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, error.SqlState);
        await transaction.RollbackAsync();
        await using var absent = NpgsqlCatalog.Command(connection, null, "write.canonical_recorded",
            new Hash128[] { root.Id });
        Assert.Equal(new[] { false }, (bool[])(await absent.ExecuteScalarAsync())!);
    }
}
