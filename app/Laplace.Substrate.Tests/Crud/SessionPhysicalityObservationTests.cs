using System.Data;
using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class SessionPhysicalityObservationTests(LocalPgFixture pg)
{
    private sealed record Fixture(
        Hash128 Session,
        Hash128 Turn,
        SubstrateChange First,
        SubstrateChange Second,
        SubstrateChange[] Bootstrap);

    private sealed record Snapshot(
        string Placement,
        string Geometry,
        int Turns,
        string[] Observations);

    private static Fixture Build()
    {
        CodepointPerfcache.LoadDefault();
        string nonce = Guid.NewGuid().ToString("N");
        var scope = ConversationContent.Resolve("session-physicality-" + nonce);
        var session = ConversationContent.SessionId(scope.Tenant, "forms");
        Assert.True(ConversationContent.TryBuildTurnChange(
            scope,
            session,
            System.Text.Encoding.UTF8.GetBytes(
                "retain this exact session body " + nonce),
            null,
            null,
            out var first,
            out _,
            out _,
            out Hash128[] turns,
            occurrenceKey: "first",
            phase: ConversationContent.TurnPhase.Input));

        var secondBuilder =
            new SubstrateChangeBuilder(
                first.Metadata.SourceId,
                "session-second-" + nonce)
            .DeclareSourcePrior(SourceTrust.UserPrompt)
            .AddEntity(
                session,
                EntityTier.Document,
                ConversationContent.SessionType,
                first.Metadata.SourceId);

        // The session handle is a stable identity with one mutable Projection
        // physicality. Its exact current trajectory contains all admitted turns;
        // source/unit provenance belongs in physicality_observations, not testimony.
        return new(
            session,
            Assert.Single(turns),
            first,
            secondBuilder.Build(),
            ConversationContent.BuildTenantBootstrapChanges(scope));
    }

    private static string Hex(Hash128 id) =>
        Convert.ToHexStringLower(id.ToBytes());

    private async Task<Snapshot> ReadAsync(
        Hash128 session,
        NpgsqlConnection? connection = null,
        NpgsqlTransaction? transaction = null)
    {
        await using var owned =
            connection is null
                ? await pg.DataSource.OpenConnectionAsync()
                : null;

        await using var cmd = new NpgsqlCommand("""
            SELECT encode(p.id,'hex'),
                   encode(public.ST_AsEWKB(p.trajectory),'hex'),
                   p.n_constituents,
                   ARRAY(
                     SELECT encode(o.physicality_id,'hex') || ':' ||
                            encode(o.source_unit_id,'hex') || ':' ||
                            o.observed_at_unix_us
                     FROM laplace.physicality_observations o
                     JOIN laplace.canonical_names n
                       ON n.id=o.source_id
                      AND n.name='substrate/source/SessionProjection/v1'
                     WHERE o.entity_id=p.entity_id
                       AND o.physicality_id=p.id
                     ORDER BY o.source_unit_id
                   )
            FROM laplace.physicalities p
            WHERE p.entity_id=$1 AND p.type=3
            """,
            connection ?? owned,
            transaction);
        cmd.Parameters.AddWithValue(session.ToBytes());

        await using var rows = await cmd.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        var result = new Snapshot(
            rows.GetString(0),
            rows.GetString(1),
            rows.GetInt32(2),
            rows.GetFieldValue<string[]>(3));
        Assert.False(await rows.ReadAsync());
        return result;
    }

    private async Task AssertNoStructuralTestimonyAsync(
        Hash128 session,
        NpgsqlConnection? connection = null,
        NpgsqlTransaction? transaction = null)
    {
        await using var owned =
            connection is null
                ? await pg.DataSource.OpenConnectionAsync()
                : null;
        await using var cmd = new NpgsqlCommand("""
            SELECT
              (SELECT count(*)
               FROM laplace.attestations
               WHERE subject_id=$1 AND type_id=$2),
              (SELECT count(*)
               FROM laplace.consensus
               WHERE subject_id=$1 AND type_id=$2)
            """,
            connection ?? owned,
            transaction);
        cmd.Parameters.AddWithValue(session.ToBytes());
        cmd.Parameters.AddWithValue(
            RelationTypeRegistry.Resolve("HAS_PHYSICALITY").Id.ToBytes());

        await using var rows = await cmd.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(0L, rows.GetInt64(0));
        Assert.Equal(0L, rows.GetInt64(1));
        Assert.False(await rows.ReadAsync());
    }

    private static void EqualSnapshot(Snapshot expected, Snapshot actual)
    {
        Assert.Equal(expected.Placement, actual.Placement);
        Assert.Equal(expected.Geometry, actual.Geometry);
        Assert.Equal(expected.Turns, actual.Turns);
        Assert.Equal(expected.Observations, actual.Observations);
    }

    [Fact]
    public async Task ExistingTurnAppendRecordsDirectProjectionProvenanceAndReplayDoesNotAppendAgain()
    {
        var f = Build();
        await using var writer =
            new ConsensusAccumulatingWriter(
                new NpgsqlSubstrateWriter(pg.DataSource),
                pg.DataSource);

        foreach (var bootstrap in f.Bootstrap)
            await writer.ApplyWorkingSetAsync(bootstrap);

        await writer.ApplyConversationTurnAsync(
            f.First,
            f.Session,
            [f.Turn]);
        var first = await ReadAsync(f.Session);
        Assert.Equal(1, first.Turns);
        Assert.Single(first.Observations);
        Assert.Equal(
            first.Placement,
            first.Observations[0].Split(':')[0]);
        Assert.Equal(
            Hex(f.First.Metadata.IntentId),
            first.Observations[0].Split(':')[1]);
        await AssertNoStructuralTestimonyAsync(f.Session);

        await writer.ApplyConversationTurnAsync(
            f.Second,
            f.Session,
            [f.Turn]);
        var second = await ReadAsync(f.Session);
        Assert.Equal(first.Placement, second.Placement);
        Assert.NotEqual(first.Geometry, second.Geometry);
        Assert.Equal(2, second.Turns);
        Assert.Equal(2, second.Observations.Length);
        Assert.All(
            second.Observations,
            row => Assert.Equal(second.Placement, row.Split(':')[0]));
        Assert.Equal(
            new[]
            {
                Hex(f.First.Metadata.IntentId),
                Hex(f.Second.Metadata.IntentId),
            }.Order().ToArray(),
            second.Observations
                .Select(row => row.Split(':')[1])
                .Order()
                .ToArray());
        await AssertNoStructuralTestimonyAsync(f.Session);

        // The stable session handle is deliberately Projection-only. Its current
        // turn history is the projection trajectory, not unrelated canonical Content.
        await using (var canonical = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.physicalities "
            + "WHERE entity_id=$1 AND type=1"))
        {
            canonical.Parameters.AddWithValue(f.Session.ToBytes());
            Assert.Equal(
                0L,
                (long)(await canonical.ExecuteScalarAsync())!);
        }

        Assert.True(
            (await writer.ApplyConversationTurnAsync(
                f.Second,
                f.Session,
                [f.Turn])).JournalReplayHit);
        EqualSnapshot(second, await ReadAsync(f.Session));
        await AssertNoStructuralTestimonyAsync(f.Session);
    }

    private async Task AppendAsync(
        Fixture f,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int expectedTurns = 2,
        Hash128? unit = null)
    {
        await using var cmd = new NpgsqlCommand(
            SqlCatalog.Get("conversation.append_turns").Text,
            connection,
            transaction);
        cmd.Parameters.AddWithValue(f.Session.ToBytes());
        cmd.Parameters.AddWithValue(
            NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            new[] { f.Turn.ToBytes() });
        cmd.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            DateTime.UtcNow);
        cmd.Parameters.AddWithValue(
            (unit ?? f.Second.Metadata.IntentId).ToBytes());

        long budget = IngestSizing.ResolveWorkingSetBudgetBytes();
        cmd.Parameters.AddWithValue(budget);
        cmd.Parameters.AddWithValue(512);
        cmd.Parameters.AddWithValue(
            budget / MemoryTopology.Hash128Bytes);
        Assert.Equal(
            expectedTurns,
            (int)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task NativeSessionRollbackRetainsOriginalProjectionAndDirectProvenance()
    {
        var f = Build();
        await using var writer =
            new ConsensusAccumulatingWriter(
                new NpgsqlSubstrateWriter(pg.DataSource),
                pg.DataSource);

        foreach (var bootstrap in f.Bootstrap)
            await writer.ApplyWorkingSetAsync(bootstrap);

        await writer.ApplyConversationTurnAsync(
            f.First,
            f.Session,
            [f.Turn]);
        var before = await ReadAsync(f.Session);

        await using (var connection =
            await pg.DataSource.OpenConnectionAsync())
        await using (var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted))
        {
            await AppendAsync(f, connection, transaction);
            var uncommitted =
                await ReadAsync(f.Session, connection, transaction);
            Assert.Equal(2, uncommitted.Turns);
            Assert.Equal(2, uncommitted.Observations.Length);
            await AssertNoStructuralTestimonyAsync(
                f.Session,
                connection,
                transaction);
            await transaction.RollbackAsync();
        }
        EqualSnapshot(before, await ReadAsync(f.Session));

        // The SessionProjection source declaration is ordinary content over the
        // persisted Unicode floor. Removing an actual source-name constituent
        // must fail the strict generated sink rather than pretending the floor
        // cache itself is durable entity evidence.
        Hash128 atom = CodepointPerfcache.Records['S'].Hash;
        Assert.True(CodepointPerfcache.IsKnownCodepointId(atom));
        await using (var connection =
            await pg.DataSource.OpenConnectionAsync())
        await using (var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted))
        {
            await using var remove = new NpgsqlCommand(
                "DELETE FROM laplace.entities WHERE id=$1 AND tier=0",
                connection,
                transaction);
            remove.Parameters.AddWithValue(
                NpgsqlDbType.Bytea,
                atom.ToBytes());
            Assert.Equal(1, await remove.ExecuteNonQueryAsync());

            var absent =
                await Assert.ThrowsAsync<PostgresException>(
                    () => AppendAsync(
                        f,
                        connection,
                        transaction));
            Assert.Equal("23503", absent.SqlState);
            Assert.Equal(
                "generated stage sink: referenced entity is not admitted",
                absent.MessageText);
            await transaction.RollbackAsync();
        }

        EqualSnapshot(before, await ReadAsync(f.Session));
        await AssertNoStructuralTestimonyAsync(f.Session);

        await using var staleConnection =
            await pg.DataSource.OpenConnectionAsync();
        await using var staleTransaction =
            await staleConnection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead);
        var error =
            await Assert.ThrowsAsync<PostgresException>(
                () => AppendAsync(
                    f,
                    staleConnection,
                    staleTransaction));
        Assert.Equal("0A000", error.SqlState);
        await staleTransaction.RollbackAsync();

        EqualSnapshot(before, await ReadAsync(f.Session));
        await AssertNoStructuralTestimonyAsync(f.Session);
    }

    [Fact]
    public async Task WaitingReadCommittedAppenderReadsTheBodyCommittedAfterItsStatementStarted()
    {
        var f = Build();
        await using var writer =
            new ConsensusAccumulatingWriter(
                new NpgsqlSubstrateWriter(pg.DataSource),
                pg.DataSource);

        foreach (var bootstrap in f.Bootstrap)
            await writer.ApplyWorkingSetAsync(bootstrap);
        await writer.ApplyConversationTurnAsync(
            f.First,
            f.Session,
            [f.Turn]);

        await using var first =
            await pg.DataSource.OpenConnectionAsync();
        await using var second =
            await pg.DataSource.OpenConnectionAsync();
        await using var firstTx =
            await first.BeginTransactionAsync(
                IsolationLevel.ReadCommitted);
        await using var secondTx =
            await second.BeginTransactionAsync(
                IsolationLevel.ReadCommitted);

        await AppendAsync(f, first, firstTx);
        Hash128 waiterUnit = Hash128.OfCanonical(
            "session-waiter/" + f.Session);
        Task waiting = AppendAsync(
            f,
            second,
            secondTx,
            expectedTurns: 3,
            unit: waiterUnit);

        bool sawLockWait = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15)
               && !waiting.IsCompleted)
        {
            await using var probe = pg.DataSource.CreateCommand(
                "SELECT wait_event_type='Lock' "
                + "AND wait_event='transactionid' "
                + "FROM pg_stat_activity WHERE pid=$1");
            probe.Parameters.AddWithValue(second.ProcessID);
            sawLockWait =
                (await probe.ExecuteScalarAsync()) is true;
            if (sawLockWait)
                break;
            await Task.Delay(20);
        }

        Assert.True(
            sawLockWait,
            "The second real backend must wait on the first session row transaction.");
        Assert.False(waiting.IsCompleted);

        await firstTx.CommitAsync();
        await waiting.WaitAsync(TimeSpan.FromSeconds(30));
        await secondTx.CommitAsync();

        var current = await ReadAsync(f.Session);
        Assert.Equal(3, current.Turns);
        Assert.Equal(3, current.Observations.Length);
        Assert.All(
            current.Observations,
            row => Assert.Equal(current.Placement, row.Split(':')[0]));
        Assert.Equal(
            3,
            current.Observations
                .Select(row => row.Split(':')[1])
                .Distinct()
                .Count());
        Assert.Contains(
            current.Observations,
            row => row.Split(':')[1] == Hex(waiterUnit));
        await AssertNoStructuralTestimonyAsync(f.Session);
    }
}
