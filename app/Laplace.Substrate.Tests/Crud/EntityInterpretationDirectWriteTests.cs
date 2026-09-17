using global::Npgsql;
using NpgsqlTypes;
using System.Diagnostics;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class EntityInterpretationDirectWriteTests(LocalPgFixture pg)
{
    [Fact]
    public async Task ExistingPlainCompositeIdentityTableIsRefusedWithoutChangingItsData()
    {
        Assert.True(Laplace.Engine.Core.LaplaceInstall.TryRepoRoot(out string root));
        string sql = await File.ReadAllTextAsync(Path.Combine(root, "extension",
            "laplace_substrate", "sql", "schema", "tables", "entities.sql.in"));
        string schema = "identity_legacy_" + Guid.NewGuid().ToString("N");
        byte[] id = Guid.NewGuid().ToByteArray();
        byte[] type = Enumerable.Repeat((byte)0x31,16).ToArray();
        await using var connection = await pg.DataSource.OpenConnectionAsync();
        try
        {
            await using (var setup = connection.CreateCommand())
            {
                setup.CommandText = $"""
                    CREATE SCHEMA "{schema}";
                    CREATE TABLE "{schema}".entities(
                        id bytea NOT NULL, tier smallint NOT NULL, type_id bytea NOT NULL,
                        first_observed_by bytea, created_at timestamptz DEFAULT now(), highway_mask bytea,
                        PRIMARY KEY(id,tier));
                    """;
                await setup.ExecuteNonQueryAsync();
            }
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = $"""
                    INSERT INTO "{schema}".entities(id,tier,type_id) VALUES($1,3,$2),($1,5,$2)
                    """;
                insert.Parameters.AddWithValue(id);
                insert.Parameters.AddWithValue(type);
                await insert.ExecuteNonQueryAsync();
            }
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"SET LOCAL search_path TO \"{schema}\",pg_catalog;\n"
                    + sql.Replace("@extschema@",schema,StringComparison.Ordinal);
                var error = await Assert.ThrowsAsync<PostgresException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.RaiseException,error.SqlState);
                Assert.Contains("legacy non-partitioned layout",error.MessageText);
                await transaction.RollbackAsync();
            }
            await using var verify = connection.CreateCommand();
            verify.CommandText = $"""
                SELECT (SELECT count(*) FROM "{schema}".entities WHERE id=$1 AND type_id=$2),
                       (SELECT array_agg(tier ORDER BY tier) FROM "{schema}".entities WHERE id=$1),
                       to_regclass($3)::text IS NULL,
                       (SELECT pg_get_constraintdef(oid)
                        FROM pg_constraint WHERE conrelid=$4::regclass AND contype='p'),
                       (SELECT relkind::text FROM pg_class WHERE oid=$4::regclass)
                """;
            verify.Parameters.AddWithValue(id);
            verify.Parameters.AddWithValue(type);
            verify.Parameters.AddWithValue(schema+".entity_interpretations");
            verify.Parameters.AddWithValue(schema+".entities");
            await using var rows = await verify.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(2L,rows.GetInt64(0));
            Assert.Equal(new short[]{3,5},rows.GetFieldValue<short[]>(1));
            Assert.True(rows.GetBoolean(2));
            Assert.Equal("PRIMARY KEY (id, tier)",rows.GetString(3));
            Assert.Equal("r",rows.GetString(4));
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentOrdinaryInsertOrCopyRetainsBothFacetsAndTheirSummary(bool copy)
    {
        byte[] Id(byte value) => Enumerable.Repeat(value,16).ToArray();
        byte[] id = Guid.NewGuid().ToByteArray();
        byte[] high = Id(0xee), low = Id(0x11);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await using var first = await pg.DataSource.OpenConnectionAsync(stop.Token);
            await using var second = await pg.DataSource.OpenConnectionAsync(stop.Token);
            async Task<int> IdentityAsync(NpgsqlConnection connection)
            {
                await using var identity = connection.CreateCommand();
                identity.CommandText = "SELECT pg_backend_pid(),current_setting('session_replication_role')";
                await using var row = await identity.ExecuteReaderAsync(stop.Token);
                Assert.True(await row.ReadAsync(stop.Token));
                Assert.Equal("origin",row.GetString(1));
                return row.GetInt32(0);
            }
            int firstPid = await IdentityAsync(first), secondPid = await IdentityAsync(second);
            await using var tx1 = await first.BeginTransactionAsync(stop.Token);
            await using var tx2 = await second.BeginTransactionAsync(stop.Token);
            await using (var insert = first.CreateCommand())
            {
                insert.Transaction = tx1;
                insert.CommandText = "INSERT INTO laplace.entities(id,tier,type_id) VALUES($1,3,$2)";
                insert.Parameters.AddWithValue(id);
                insert.Parameters.AddWithValue(high);
                await insert.ExecuteNonQueryAsync(stop.Token);
            }

            async Task WriteSecondAsync()
            {
                if (copy)
                {
                    await using var importer = await second.BeginBinaryImportAsync(
                        "COPY laplace.entities(id,tier,type_id) FROM STDIN (FORMAT BINARY)", stop.Token);
                    await importer.StartRowAsync(stop.Token);
                    await importer.WriteAsync(id,NpgsqlDbType.Bytea,stop.Token);
                    await importer.WriteAsync((short)5,NpgsqlDbType.Smallint,stop.Token);
                    await importer.WriteAsync(low,NpgsqlDbType.Bytea,stop.Token);
                    await importer.CompleteAsync(stop.Token);
                }
                else
                {
                    await using var insert = second.CreateCommand();
                    insert.Transaction = tx2;
                    insert.CommandText = """
                        INSERT INTO laplace.entities(id,tier,type_id) VALUES($1,5,$2)
                        ON CONFLICT(id) DO NOTHING
                        """;
                    insert.Parameters.AddWithValue(id);
                    insert.Parameters.AddWithValue(low);
                    await insert.ExecuteNonQueryAsync(stop.Token);
                }
            }
            Task pending = WriteSecondAsync();
            try
            {
                // The first E is still uncommitted and absent to the second caller.
                // Observe an actual backend wait before releasing it; no sleep-based
                // claim about the race's ordering or a particular lock implementation.
                await using var observe = await pg.DataSource.OpenConnectionAsync(stop.Token);
                var wait = Stopwatch.StartNew();
                bool blocked = false;
                while (wait.Elapsed < TimeSpan.FromSeconds(20))
                {
                    await using var query = observe.CreateCommand();
                    query.CommandText = "SELECT $1=ANY(pg_blocking_pids($2))";
                    query.Parameters.AddWithValue(firstPid);
                    query.Parameters.AddWithValue(secondPid);
                    blocked = (bool)(await query.ExecuteScalarAsync(stop.Token))!;
                    if (blocked) break;
                    Assert.False(pending.IsCompleted, "second writer returned before first E committed");
                    await Task.Delay(20,stop.Token);
                }
                Assert.True(blocked,"second backend did not block behind the first writer");
                await tx1.CommitAsync(stop.Token);
                await pending;
                await tx2.CommitAsync(stop.Token);
            }
            finally
            {
                if (!pending.IsCompleted) stop.Cancel();
                try { await pending; } catch { /* preserve the primary failure */ }
            }

            await using var state = pg.DataSource.CreateCommand("""
                SELECT e.tier,e.type_id,
                       (SELECT count(*) FROM laplace.entities WHERE id=$1),
                       (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id=$1),
                       EXISTS (SELECT FROM laplace.entity_interpretations
                               WHERE entity_id=$1 AND tier=3 AND type_id=$2),
                       EXISTS (SELECT FROM laplace.entity_interpretations
                               WHERE entity_id=$1 AND tier=5 AND type_id=$3),
                       EXISTS (SELECT FROM laplace.entity_interpretations
                               WHERE entity_id=$1 AND tier=3 AND type_id=$3)
                FROM laplace.entities e WHERE e.id=$1
                """);
            state.Parameters.AddWithValue(id);
            state.Parameters.AddWithValue(high);
            state.Parameters.AddWithValue(low);
            await using var rows = await state.ExecuteReaderAsync(stop.Token);
            Assert.True(await rows.ReadAsync(stop.Token));
            Assert.Equal(3,rows.GetInt16(0));
            Assert.Equal(low,rows.GetFieldValue<byte[]>(1));
            Assert.Equal(1L,rows.GetInt64(2));
            Assert.Equal(2L,rows.GetInt64(3));
            Assert.True(rows.GetBoolean(4));
            Assert.True(rows.GetBoolean(5));
            Assert.False(rows.GetBoolean(6)); // summary is not an invented facet
            Assert.False(await rows.ReadAsync(stop.Token));
        }
        finally
        {
            await using var cleanup = pg.DataSource.CreateCommand("""
                WITH facets AS (DELETE FROM laplace.entity_interpretations WHERE entity_id=$1)
                DELETE FROM laplace.entities WHERE id=$1
                """);
            cleanup.Parameters.AddWithValue(id);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
