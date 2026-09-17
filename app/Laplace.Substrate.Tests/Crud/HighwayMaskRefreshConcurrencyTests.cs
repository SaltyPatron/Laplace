using System.Data;
using System.Diagnostics;
using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class HighwayMaskRefreshConcurrencyTests(LocalPgFixture pg)
{
    private const string A = "IS_A";
    private const string B = "HAS_PART";
    private static Hash128 Id(string name) => Hash128.OfCanonical("test/highway-refresh-concurrency/" + name);

    [Fact]
    public async Task RefreshReadsTheCommittedNewBitAfterWaitingForItsEntityLock()
    {
        var id = Id("new-bit-before-lock");
        try
        {
            await using var writer = await OpenAsync();
            await using var refreshConnection = await OpenAsync();
            await using var observer = await OpenAsync();
            await InsertEntityAsync(observer, id);
            await InsertEdgeAsync(observer, id, A);
            await using var transaction = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await InsertEdgeAsync(writer, id, B);
            Assert.Equal(1L, await DepositAsync(writer, id, B));

            // The writer's actual tuple update holds the entity lock. Refresh must
            // wait there, and a backend wait observation is the scheduling barrier.
            await using var refresh = new RunningRefresh(refreshConnection, id);
            await WaitForBlockerAsync(observer, refreshConnection.ProcessID, writer.ProcessID);
            await transaction.CommitAsync();
            await refresh.Completion.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(await ExpectedBitsAsync(observer, A, B), await MaskBitsAsync(observer, id));
            Assert.False(await IsDirtyAsync(observer, id));
        }
        finally { await CleanupAsync(id); }
    }

    [Fact]
    public async Task ApparentlyPresentOrBitRetainsDirtyWorkWhileRefreshOwnsItsEntity()
    {
        var id = Id("already-present-or");
        try
        {
            await using var barrier = await OpenAsync();
            await using var writer = await OpenAsync();
            await using var refreshConnection = await OpenAsync();
            await using var observer = await OpenAsync();
            await InsertEntityAsync(observer, id);
            Assert.Equal(1L, await DepositAsync(observer, id, B));
            await using var barrierTransaction = await barrier.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await ExecuteAsync(barrier, "LOCK TABLE ONLY laplace.consensus IN ACCESS EXCLUSIVE MODE");

            // Refresh reaches its incident scan only after capturing/locking its
            // entity targets. The table lock pauses that scan, without test hooks.
            await using var refresh = new RunningRefresh(refreshConnection, id);
            await WaitForBlockerAsync(observer, refreshConnection.ProcessID, barrier.ProcessID);
            Assert.Equal(0L, await DepositAsync(writer, id, B, seconds: 5));
            Assert.True(await IsDirtyAsync(observer, id));

            // Publish the relation after refresh's incident snapshot. That scan
            // may clear the stale bit; the retained dirty obligation must repair it.
            await InsertEdgeAsync(barrier, id, B);
            await barrierTransaction.CommitAsync();
            await refresh.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(await IsDirtyAsync(observer, id));
            await RepairOwnDirtyObligationAsync(observer, id);
            Assert.Equal(await ExpectedBitsAsync(observer, B), await MaskBitsAsync(observer, id));
            Assert.False(await IsDirtyAsync(observer, id));
        }
        finally { await CleanupAsync(id); }
    }

    [Fact]
    public async Task EntityCreatedAfterTargetCaptureKeepsItsOwnDeposit()
    {
        var id = Id("created-after-capture");
        try
        {
            await using var barrier = await OpenAsync();
            await using var writer = await OpenAsync();
            await using var refreshConnection = await OpenAsync();
            await using var observer = await OpenAsync();
            // A requested identity may have incident evidence before its carrier
            // is present. No production constraint is disabled for this case.
            await InsertEdgeAsync(observer, id, A);
            await using var barrierTransaction = await barrier.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await ExecuteAsync(barrier, "LOCK TABLE ONLY laplace.consensus IN ACCESS EXCLUSIVE MODE");
            await using var refresh = new RunningRefresh(refreshConnection, id);
            await WaitForBlockerAsync(observer, refreshConnection.ProcessID, barrier.ProcessID);

            await InsertEntityAsync(writer, id);
            Assert.Equal(1L, await DepositAsync(writer, id, B));
            await barrierTransaction.CommitAsync();
            Assert.Equal(0L, Convert.ToInt64(await refresh.Completion.WaitAsync(TimeSpan.FromSeconds(15))));

            // It was outside the captured physical target set: refresh must not
            // overwrite its newly deposited B with the older requested A mask.
            Assert.Equal(await ExpectedBitsAsync(observer, B), await MaskBitsAsync(observer, id));
        }
        finally { await CleanupAsync(id); }
    }

    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task TransactionSnapshotRefreshRefusesBeforeMutationAndRollsBack(IsolationLevel isolation)
    {
        var id = Id("isolation-" + isolation);
        try
        {
            await using var connection = await OpenAsync();
            await InsertEntityAsync(connection, id);
            await InsertEdgeAsync(connection, id, A);
            Assert.Equal(1L, await DepositAsync(connection, id, B));
            var before = await MaskBitsAsync(connection, id);
            await using var transaction = await connection.BeginTransactionAsync(isolation);
            await using (var dirty = WithId(connection,
                "INSERT INTO laplace.highway_mask_dirty(id) VALUES(@id)", id))
                await dirty.ExecuteNonQueryAsync();
            await using var command = RefreshCommand(connection, id);
            var error = await Assert.ThrowsAsync<PostgresException>(
                async () => { await command.ExecuteScalarAsync(); });
            Assert.Equal(PostgresErrorCodes.SerializationFailure, error.SqlState);
            Assert.Contains("READ COMMITTED", error.MessageText, StringComparison.Ordinal);
            await transaction.RollbackAsync();
            Assert.Equal(before, await MaskBitsAsync(connection, id));
            Assert.False(await IsDirtyAsync(connection, id));
            Assert.True(await ScalarAsync<bool>(connection, "SELECT true"));
        }
        finally { await CleanupAsync(id); }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = await pg.DataSource.OpenConnectionAsync();
        try
        {
            await ExecuteAsync(connection,
                "SET default_transaction_isolation='read committed'; SET statement_timeout='20s'");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static NpgsqlCommand WithId(NpgsqlConnection connection, string sql, Hash128 id, int seconds = 20)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = seconds;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bytea, id.ToBytes());
        return command;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 20;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 20;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertEntityAsync(NpgsqlConnection connection, Hash128 id)
    {
        await using var command = WithId(connection, """
            INSERT INTO laplace.entities(id,tier,type_id)
            VALUES(@id,2,laplace.entity_type_id('Word'))
            """, id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertEdgeAsync(NpgsqlConnection connection, Hash128 id, string relation)
    {
        await using var command = WithId(connection, """
            INSERT INTO laplace.consensus
                (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
            SELECT laplace.consensus_id(@id,r,@id),@id,r,@id,
                   1500000000000,350000000000,60000000,1,now()
            FROM (SELECT laplace.relation_type_id(@relation) AS r) resolved
            """, id);
        command.Parameters.AddWithValue("relation", NpgsqlDbType.Text, relation);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> DepositAsync(
        NpgsqlConnection connection, Hash128 id, string relation, int seconds = 20)
    {
        await using var command = WithId(connection, """
            SELECT consensus.highway_mask_deposit(ARRAY[@id],ARRAY[laplace.relation_type_id(@relation)])
            """, id, seconds);
        command.Parameters.AddWithValue("relation", NpgsqlDbType.Text, relation);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static NpgsqlCommand RefreshCommand(NpgsqlConnection connection, Hash128 id) =>
        WithId(connection, "SELECT consensus.highway_mask_refresh(ARRAY[@id])", id);

    private static async Task<bool> IsDirtyAsync(NpgsqlConnection connection, Hash128 id)
    {
        await using var command = WithId(connection,
            "SELECT EXISTS(SELECT 1 FROM laplace.highway_mask_dirty WHERE id=@id)", id);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task RepairOwnDirtyObligationAsync(NpgsqlConnection connection, Hash128 id)
    {
        // Exercise the drain's claim-before-refresh transaction for this fixture's
        // obligation only; the shared database's other dirty work is untouched.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var claim = WithId(connection, """
            WITH claimed AS MATERIALIZED (
                SELECT id FROM laplace.highway_mask_dirty
                WHERE id=@id FOR UPDATE SKIP LOCKED
            )
            DELETE FROM laplace.highway_mask_dirty d USING claimed q
            WHERE d.id=q.id RETURNING d.id
            """, id))
        {
            Assert.Equal(id.ToBytes(), Assert.IsType<byte[]>(await claim.ExecuteScalarAsync()));
        }
        await using (var refresh = RefreshCommand(connection, id))
            await refresh.ExecuteScalarAsync();
        await transaction.CommitAsync();
    }

    private static async Task<int[]> MaskBitsAsync(NpgsqlConnection connection, Hash128 id)
    {
        await using var command = WithId(connection, """
            SELECT COALESCE(consensus.highway_mask_bits(highway_mask),ARRAY[]::integer[])
            FROM laplace.entities WHERE id=@id AND tier=2
            """, id);
        return Assert.IsType<int[]>(await command.ExecuteScalarAsync());
    }

    private static async Task<int[]> ExpectedBitsAsync(NpgsqlConnection connection, params string[] relations)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT array_agg(bit ORDER BY bit) FROM (
                SELECT consensus.relation_highway_bit(laplace.relation_type_id(name)) AS bit
                FROM unnest(@relations) name
            ) bits
            """;
        command.Parameters.AddWithValue("relations", NpgsqlDbType.Array | NpgsqlDbType.Text, relations);
        return Assert.IsType<int[]>(await command.ExecuteScalarAsync());
    }

    private static async Task WaitForBlockerAsync(NpgsqlConnection observer, int blocked, int holder)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            await using var command = observer.CreateCommand();
            command.CommandText = "SELECT @holder=ANY(pg_blocking_pids(@blocked))";
            command.Parameters.AddWithValue("holder", holder);
            command.Parameters.AddWithValue("blocked", blocked);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Backend {blocked} never reported blocker {holder}");
    }

    private sealed class RunningRefresh : IAsyncDisposable
    {
        private readonly NpgsqlCommand _command;
        internal Task<object?> Completion { get; }

        internal RunningRefresh(NpgsqlConnection connection, Hash128 id)
        {
            _command = RefreshCommand(connection, id);
            Completion = _command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (!Completion.IsCompleted) _command.Cancel();
            try { await Completion; } catch { /* Main path observes failures; cleanup releases locks. */ }
            await _command.DisposeAsync();
        }
    }

    private async Task CleanupAsync(Hash128 id)
    {
        await using var connection = await OpenAsync();
        await using var command = WithId(connection, """
            DELETE FROM laplace.consensus WHERE subject_id=@id OR object_id=@id;
            DELETE FROM laplace.highway_mask_dirty WHERE id=@id;
            DELETE FROM laplace.highway_mask_pending WHERE entity_id=@id;
            DELETE FROM laplace.entities WHERE id=@id
            """, id);
        await command.ExecuteNonQueryAsync();
    }
}
