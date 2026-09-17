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

    [Fact]
    public async Task MultirowDirectInsertPublishesFacetsSetwiseWithoutAdvisoryMutex()
    {
        byte[] type = Fill(0x47);
        byte[][] ids = Enumerable.Range(0,512).Select(_ => Guid.NewGuid().ToByteArray()).ToArray();
        await using var connection = await pg.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO laplace.entities(id,tier,type_id)
                    SELECT id,3,$2 FROM unnest($1::bytea[]) AS rows(id)
                    """;
                insert.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,ids);
                insert.Parameters.AddWithValue(type);
                Assert.Equal(ids.Length,await insert.ExecuteNonQueryAsync());
            }

            await using var inspect = connection.CreateCommand();
            inspect.Transaction = transaction;
            inspect.CommandText = """
                SELECT (SELECT count(*) FROM laplace.entities WHERE id=ANY($1)),
                       (SELECT count(*) FROM laplace.entity_interpretations
                        WHERE entity_id=ANY($1) AND tier=3 AND type_id=$2),
                       (SELECT count(*) FROM pg_locks
                        WHERE pid=pg_backend_pid() AND locktype='advisory' AND granted)
                """;
            inspect.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,ids);
            inspect.Parameters.AddWithValue(type);
            await using var rows = await inspect.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal((long)ids.Length,rows.GetInt64(0));
            Assert.Equal((long)ids.Length,rows.GetInt64(1));
            Assert.Equal(0L,rows.GetInt64(2));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task DuplicateCanonicalIdIsAUniqueViolationAndCannotInventAnotherFacet()
    {
        byte[] id = Guid.NewGuid().ToByteArray();
        byte[] firstType = Fill(0x51);
        byte[] secondType = Fill(0x11);
        try
        {
            await using (var insert = pg.DataSource.CreateCommand(
                "INSERT INTO laplace.entities(id,tier,type_id) VALUES($1,5,$2)"))
            {
                insert.Parameters.AddWithValue(id);
                insert.Parameters.AddWithValue(firstType);
                Assert.Equal(1,await insert.ExecuteNonQueryAsync());
            }

            await using (var connection = await pg.DataSource.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var duplicate = connection.CreateCommand();
                duplicate.Transaction = transaction;
                duplicate.CommandText =
                    "INSERT INTO laplace.entities(id,tier,type_id) VALUES($1,3,$2)";
                duplicate.Parameters.AddWithValue(id);
                duplicate.Parameters.AddWithValue(secondType);
                var error = await Assert.ThrowsAsync<PostgresException>(
                    () => duplicate.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.UniqueViolation,error.SqlState);
                await transaction.RollbackAsync();
            }

            await using var inspect = pg.DataSource.CreateCommand("""
                SELECT e.tier,e.type_id,
                       (SELECT count(*) FROM laplace.entities WHERE id=$1),
                       (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id=$1),
                       EXISTS(SELECT FROM laplace.entity_interpretations
                              WHERE entity_id=$1 AND tier=3 AND type_id=$3)
                FROM laplace.entities e WHERE e.id=$1
                """);
            inspect.Parameters.AddWithValue(id);
            inspect.Parameters.AddWithValue(firstType);
            inspect.Parameters.AddWithValue(secondType);
            await using var rows = await inspect.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(5,rows.GetInt16(0));
            Assert.Equal(firstType,rows.GetFieldValue<byte[]>(1));
            Assert.Equal(1L,rows.GetInt64(2));
            Assert.Equal(1L,rows.GetInt64(3));
            Assert.False(rows.GetBoolean(4));
        }
        finally
        {
            await DeleteIdentityAsync(id);
        }
    }

    [Fact]
    public async Task PublisherAddsObservedFacetWithoutChangingCanonicalIdentity()
    {
        byte[] id = Guid.NewGuid().ToByteArray();
        byte[] high = Fill(0xee);
        byte[] low = Fill(0x11);
        byte[] source = Fill(0x72);
        try
        {
            await using (var insert = pg.DataSource.CreateCommand(
                "INSERT INTO laplace.entities(id,tier,type_id) VALUES($1,5,$2)"))
            {
                insert.Parameters.AddWithValue(id);
                insert.Parameters.AddWithValue(high);
                await insert.ExecuteNonQueryAsync();
            }

            await using (var publish = pg.DataSource.CreateCommand("""
                SELECT laplace.entity_interpretations_publish(
                    ARRAY[$1]::bytea[],ARRAY[3::smallint],ARRAY[$2]::bytea[],
                    ARRAY[$3]::bytea[],ARRAY[false]::boolean[])
                """))
            {
                publish.Parameters.AddWithValue(id);
                publish.Parameters.AddWithValue(low);
                publish.Parameters.AddWithValue(source);
                Assert.False((bool)(await publish.ExecuteScalarAsync())!);
            }

            await using var inspect = pg.DataSource.CreateCommand("""
                SELECT e.tier,e.type_id,
                       (SELECT count(*) FROM laplace.entities WHERE id=$1),
                       (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id=$1),
                       EXISTS(SELECT FROM laplace.entity_interpretations
                              WHERE entity_id=$1 AND tier=5 AND type_id=$2),
                       EXISTS(SELECT FROM laplace.entity_interpretations
                              WHERE entity_id=$1 AND tier=3 AND type_id=$3)
                FROM laplace.entities e WHERE e.id=$1
                """);
            inspect.Parameters.AddWithValue(id);
            inspect.Parameters.AddWithValue(high);
            inspect.Parameters.AddWithValue(low);
            await using var rows = await inspect.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(3,rows.GetInt16(0));
            Assert.Equal(low,rows.GetFieldValue<byte[]>(1));
            Assert.Equal(1L,rows.GetInt64(2));
            Assert.Equal(2L,rows.GetInt64(3));
            Assert.True(rows.GetBoolean(4));
            Assert.True(rows.GetBoolean(5));
        }
        finally
        {
            await DeleteIdentityAsync(id);
        }
    }

    [Fact]
    public async Task PublisherRefusesFacetForMissingCanonicalIdentityWithoutSynthesizingIt()
    {
        byte[] id = Guid.NewGuid().ToByteArray();
        byte[] type = Fill(0x22);
        byte[] source = Fill(0x73);
        await using (var publish = pg.DataSource.CreateCommand("""
            SELECT laplace.entity_interpretations_publish(
                ARRAY[$1]::bytea[],ARRAY[4::smallint],ARRAY[$2]::bytea[],
                ARRAY[$3]::bytea[],ARRAY[false]::boolean[])
            """))
        {
            publish.Parameters.AddWithValue(id);
            publish.Parameters.AddWithValue(type);
            publish.Parameters.AddWithValue(source);
            Assert.True((bool)(await publish.ExecuteScalarAsync())!);
        }
        await using var inspect = pg.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM laplace.entities WHERE id=$1)
                 + (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id=$1)
            """);
        inspect.Parameters.AddWithValue(id);
        Assert.Equal(0L,(long)(await inspect.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SameIdentityPublishersSerializeOnTheCanonicalRowOnly()
    {
        byte[] id = Guid.NewGuid().ToByteArray();
        byte[] firstType = Fill(0x61);
        byte[] secondType = Fill(0x62);
        byte[] source = Fill(0x74);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await using (var insert = pg.DataSource.CreateCommand(
                "INSERT INTO laplace.entities(id,tier,type_id) VALUES($1,7,$2)"))
            {
                insert.Parameters.AddWithValue(id);
                insert.Parameters.AddWithValue(firstType);
                await insert.ExecuteNonQueryAsync(stop.Token);
            }

            await using var first = await pg.DataSource.OpenConnectionAsync(stop.Token);
            await using var second = await pg.DataSource.OpenConnectionAsync(stop.Token);
            await using var observer = await pg.DataSource.OpenConnectionAsync(stop.Token);
            await using var tx1 = await first.BeginTransactionAsync(stop.Token);
            await using var tx2 = await second.BeginTransactionAsync(stop.Token);

            await PublishAsync(first,tx1,id,3,firstType,source,stop.Token);
            await using (var lockCheck = first.CreateCommand())
            {
                lockCheck.Transaction = tx1;
                lockCheck.CommandText =
                    "SELECT count(*) FROM pg_locks WHERE pid=pg_backend_pid() AND locktype='advisory' AND granted";
                Assert.Equal(0L,(long)(await lockCheck.ExecuteScalarAsync(stop.Token))!);
            }

            Task secondPublish = PublishAsync(second,tx2,id,5,secondType,source,stop.Token);
            try
            {
                var wait = Stopwatch.StartNew();
                bool blocked = false;
                while (wait.Elapsed < TimeSpan.FromSeconds(20))
                {
                    await using var query = observer.CreateCommand();
                    query.CommandText = "SELECT $1=ANY(pg_blocking_pids($2))";
                    query.Parameters.AddWithValue(first.ProcessID);
                    query.Parameters.AddWithValue(second.ProcessID);
                    blocked = (bool)(await query.ExecuteScalarAsync(stop.Token))!;
                    if (blocked) break;
                    Assert.False(secondPublish.IsCompleted,
                        "same canonical id publisher escaped its row serialization");
                    await Task.Delay(20,stop.Token);
                }
                Assert.True(blocked,"same-id publisher did not wait on the canonical entity row");
                await tx1.CommitAsync(stop.Token);
                await secondPublish;
                await tx2.CommitAsync(stop.Token);
            }
            finally
            {
                if (!secondPublish.IsCompleted) stop.Cancel();
                try { await secondPublish; } catch { }
            }

            await using var inspect = pg.DataSource.CreateCommand("""
                SELECT count(*) FROM laplace.entity_interpretations
                WHERE entity_id=$1 AND ((tier=3 AND type_id=$2) OR (tier=5 AND type_id=$3))
                """);
            inspect.Parameters.AddWithValue(id);
            inspect.Parameters.AddWithValue(firstType);
            inspect.Parameters.AddWithValue(secondType);
            Assert.Equal(2L,(long)(await inspect.ExecuteScalarAsync())!);
        }
        finally
        {
            await DeleteIdentityAsync(id);
        }
    }

    [Fact]
    public async Task DifferentIdentityPublishersDoNotShareAProcessWideLock()
    {
        byte[] firstId = Guid.NewGuid().ToByteArray();
        byte[] secondId = Guid.NewGuid().ToByteArray();
        byte[] type = Fill(0x66);
        byte[] source = Fill(0x75);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using (var insert = pg.DataSource.CreateCommand("""
                INSERT INTO laplace.entities(id,tier,type_id) VALUES($1,7,$3),($2,7,$3)
                """))
            {
                insert.Parameters.AddWithValue(firstId);
                insert.Parameters.AddWithValue(secondId);
                insert.Parameters.AddWithValue(type);
                Assert.Equal(2,await insert.ExecuteNonQueryAsync(stop.Token));
            }

            await using var first = await pg.DataSource.OpenConnectionAsync(stop.Token);
            await using var second = await pg.DataSource.OpenConnectionAsync(stop.Token);
            await using var tx1 = await first.BeginTransactionAsync(stop.Token);
            await using var tx2 = await second.BeginTransactionAsync(stop.Token);
            await PublishAsync(first,tx1,firstId,3,type,source,stop.Token);

            Task other = PublishAsync(second,tx2,secondId,3,type,source,stop.Token);
            Task finished = await Task.WhenAny(other,Task.Delay(TimeSpan.FromSeconds(5),stop.Token));
            Assert.Same(other,finished);
            await other;
            await tx2.CommitAsync(stop.Token);
            await tx1.RollbackAsync(stop.Token);
        }
        finally
        {
            await DeleteIdentityAsync(firstId);
            await DeleteIdentityAsync(secondId);
        }
    }

    private static async Task PublishAsync(
        NpgsqlConnection connection,NpgsqlTransaction transaction,byte[] id,short tier,
        byte[] type,byte[] source,CancellationToken ct)
    {
        await using var publish = connection.CreateCommand();
        publish.Transaction = transaction;
        publish.CommandText = """
            SELECT laplace.entity_interpretations_publish(
                ARRAY[$1]::bytea[],ARRAY[$2]::smallint[],ARRAY[$3]::bytea[],
                ARRAY[$4]::bytea[],ARRAY[false]::boolean[])
            """;
        publish.Parameters.AddWithValue(id);
        publish.Parameters.AddWithValue(tier);
        publish.Parameters.AddWithValue(type);
        publish.Parameters.AddWithValue(source);
        Assert.False((bool)(await publish.ExecuteScalarAsync(ct))!);
    }

    private async Task DeleteIdentityAsync(byte[] id)
    {
        await using var cleanup = pg.DataSource.CreateCommand("""
            WITH facets AS (DELETE FROM laplace.entity_interpretations WHERE entity_id=$1)
            DELETE FROM laplace.entities WHERE id=$1
            """);
        cleanup.Parameters.AddWithValue(id);
        await cleanup.ExecuteNonQueryAsync();
    }

    private static byte[] Fill(byte value) => Enumerable.Repeat(value,16).ToArray();
}