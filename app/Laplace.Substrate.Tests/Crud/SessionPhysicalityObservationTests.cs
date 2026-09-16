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
    private sealed record Fixture(Hash128 Session, Hash128 Turn, SubstrateChange First, SubstrateChange Second,
        SubstrateChange[] Bootstrap);
    private sealed record Snapshot(string Placement, string Geometry, int Turns, string[] Evidence, string[] Standing);

    private static Fixture Build()
    {
        CodepointPerfcache.LoadDefault();
        string nonce = Guid.NewGuid().ToString("N");
        var scope = ConversationContent.Resolve("session-physicality-" + nonce);
        var session = ConversationContent.SessionId(scope.Tenant, "forms");
        Assert.True(ConversationContent.TryBuildTurnChange(scope, session,
            System.Text.Encoding.UTF8.GetBytes("retain this exact session body " + nonce),
            null, null, out var first, out _, out _, out Hash128[] turns,
            occurrenceKey: "first", phase: ConversationContent.TurnPhase.Input));
        var secondBuilder = new SubstrateChangeBuilder(first.Metadata.SourceId, "session-second-" + nonce)
            .DeclareSourcePrior(SourceTrust.UserPrompt)
            .AddEntity(session, EntityTier.Document, ConversationContent.SessionType, first.Metadata.SourceId);
        // The second append observes an existing turn again. It requires no new
        // turn entity; the evolving projection still has its own new exact form.
        return new(session, Assert.Single(turns), first, secondBuilder.Build(),
            ConversationContent.BuildTenantBootstrapChanges(scope));
    }

    private async Task<Snapshot> ReadAsync(Hash128 session, NpgsqlConnection? connection = null,
        NpgsqlTransaction? transaction = null)
    {
        await using var owned = connection is null ? await pg.DataSource.OpenConnectionAsync() : null;
        await using var cmd = new NpgsqlCommand("""
            SELECT encode(p.id,'hex'),encode(public.ST_AsEWKB(p.trajectory),'hex'),p.n_constituents,
                ARRAY(SELECT encode(a.id,'hex')||':'||encode(a.object_id,'hex')||':'||
                             encode(a.context_id,'hex')||':'||a.observation_count
                      FROM laplace.attestations a
                      WHERE a.subject_id=p.entity_id AND a.type_id=$2
                        AND a.source_id IN (SELECT id FROM laplace.canonical_names WHERE name='substrate/source/SessionProjection/v1')
                      ORDER BY a.id),
                ARRAY(SELECT encode(c.object_id,'hex')||':'||c.witness_count||':'||c.rating||':'||c.rd
                      FROM laplace.consensus c
                      WHERE c.subject_id=p.entity_id AND c.type_id=$2
                      ORDER BY c.object_id)
            FROM laplace.physicalities p WHERE p.entity_id=$1 AND p.type=3
            """, connection ?? owned, transaction);
        cmd.Parameters.AddWithValue(session.ToBytes());
        cmd.Parameters.AddWithValue(RelationTypeRegistry.Resolve("HAS_PHYSICALITY").Id.ToBytes());
        await using var rows = await cmd.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        var result = new Snapshot(rows.GetString(0), rows.GetString(1), rows.GetInt32(2),
            rows.GetFieldValue<string[]>(3), rows.GetFieldValue<string[]>(4));
        Assert.False(await rows.ReadAsync());
        return result;
    }

    private static void EqualSnapshot(Snapshot expected, Snapshot actual)
    {
        Assert.Equal(expected.Placement, actual.Placement);
        Assert.Equal(expected.Geometry, actual.Geometry);
        Assert.Equal(expected.Turns, actual.Turns);
        Assert.Equal(expected.Evidence, actual.Evidence);
        Assert.Equal(expected.Standing, actual.Standing);
    }

    [Fact]
    public async Task ExistingTurnAppendRetainsOldAndNewFormsAndWriterReplayDoesNotAppendAgain()
    {
        var f = Build();
        await using var writer = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource);
        foreach (var bootstrap in f.Bootstrap) await writer.ApplyWorkingSetAsync(bootstrap);
        await writer.ApplyConversationTurnAsync(f.First, f.Session, [f.Turn]);
        var first = await ReadAsync(f.Session);
        Assert.Equal(1, first.Turns);
        Assert.Single(first.Evidence);
        Assert.Single(first.Standing);
        string oldDescriptor = first.Evidence[0].Split(':')[1];
        await writer.ApplyConversationTurnAsync(f.Second, f.Session, [f.Turn]);
        var second = await ReadAsync(f.Session);
        Assert.Equal(first.Placement, second.Placement);
        Assert.NotEqual(first.Geometry, second.Geometry);
        Assert.Equal(2, second.Turns);
        Assert.Equal(3, second.Evidence.Length);
        Assert.Equal(2, second.Standing.Length);
        Assert.Equal(2, second.Evidence.Count(e => e.Split(':')[1] == oldDescriptor));
        Assert.Equal(2, second.Evidence.Select(e => e.Split(':')[1]).Distinct().Count());
        Assert.All(second.Evidence, e => Assert.Equal("1", e.Split(':')[3]));
        string newDescriptor = Assert.Single(second.Evidence.Select(e => e.Split(':')[1])
            .Distinct(), id => id != oldDescriptor);
        // The mutable session placement is still Projection. Both historical
        // immutable descriptors have their own retention bodies; typed readback
        // must recover the original projection type and exact turn counts.
        await PhysicalityWriterTestSupport.AssertDescriptorShapesAsync(pg.DataSource,
        [
            (Hash128.FromBytes(Convert.FromHexString(oldDescriptor)), f.Session, PhysicalityType.Projection, 1),
            (Hash128.FromBytes(Convert.FromHexString(newDescriptor)), f.Session, PhysicalityType.Projection, 2),
        ]);
        await using (var canonical = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.physicalities WHERE entity_id=$1 AND type=1"))
        {
            canonical.Parameters.AddWithValue(f.Session.ToBytes());
            Assert.Equal(0L, (long)(await canonical.ExecuteScalarAsync())!);
        }
        Assert.True((await writer.ApplyConversationTurnAsync(f.Second, f.Session, [f.Turn])).JournalReplayHit);
        EqualSnapshot(second, await ReadAsync(f.Session));
    }

    private async Task AppendAsync(Fixture f, NpgsqlConnection connection, NpgsqlTransaction transaction,
        int expectedTurns = 2, Hash128? unit = null)
    {
        await using var cmd = new NpgsqlCommand(
            SqlCatalog.Get("conversation.append_turns").Text, connection, transaction);
        cmd.Parameters.AddWithValue(f.Session.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] {f.Turn.ToBytes()});
        cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, DateTime.UtcNow);
        cmd.Parameters.AddWithValue((unit ?? f.Second.Metadata.IntentId).ToBytes());
        long budget = IngestSizing.ResolveWorkingSetBudgetBytes();
        cmd.Parameters.AddWithValue(budget);
        cmd.Parameters.AddWithValue(512);
        cmd.Parameters.AddWithValue(budget / MemoryTopology.Hash128Bytes);
        Assert.Equal(expectedTurns, (int)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task NativeSessionRollbackRetainsOriginalProjectionEvidenceAndFold()
    {
        var f = Build();
        await using var writer = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource);
        foreach (var bootstrap in f.Bootstrap) await writer.ApplyWorkingSetAsync(bootstrap);
        await writer.ApplyConversationTurnAsync(f.First, f.Session, [f.Turn]);
        var before = await ReadAsync(f.Session);
        await using (var connection = await pg.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            await AppendAsync(f, connection, transaction);
            var uncommitted = await ReadAsync(f.Session, connection, transaction);
            Assert.Equal(2, uncommitted.Turns);
            Assert.Equal(3, uncommitted.Evidence.Length);
            Assert.Equal(2, uncommitted.Standing.Length);
            await transaction.RollbackAsync();
        }
        EqualSnapshot(before, await ReadAsync(f.Session));
        // A loaded native floor is not persisted E evidence. Removing one
        // actual vocabulary atom must still fail the strict generated sink,
        // even after a prior successful append warmed its native providers.
        Hash128 atom = CodepointPerfcache.Records['A'].Hash;
        Assert.True(CodepointPerfcache.IsKnownCodepointId(atom));
        await using (var connection = await pg.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            await using var remove = new NpgsqlCommand(
                "DELETE FROM laplace.entities WHERE id=$1 AND tier=0", connection, transaction);
            remove.Parameters.AddWithValue(NpgsqlDbType.Bytea, atom.ToBytes());
            Assert.Equal(1, await remove.ExecuteNonQueryAsync());
            var absent = await Assert.ThrowsAsync<PostgresException>(() => AppendAsync(f, connection, transaction));
            Assert.Equal("23503", absent.SqlState);
            Assert.Equal("generated stage sink: referenced entity is not admitted", absent.MessageText);
            await transaction.RollbackAsync();
        }
        EqualSnapshot(before, await ReadAsync(f.Session));
        await using var staleConnection = await pg.DataSource.OpenConnectionAsync();
        await using var staleTransaction = await staleConnection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var error = await Assert.ThrowsAsync<PostgresException>(() => AppendAsync(f, staleConnection, staleTransaction));
        Assert.Equal("0A000", error.SqlState);
        await staleTransaction.RollbackAsync();
        EqualSnapshot(before, await ReadAsync(f.Session));
    }
    [Fact]
    public async Task WaitingReadCommittedAppenderReadsTheBodyCommittedAfterItsStatementStarted()
    {
        var f = Build();
        await using var writer = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource);
        foreach (var bootstrap in f.Bootstrap) await writer.ApplyWorkingSetAsync(bootstrap);
        await writer.ApplyConversationTurnAsync(f.First, f.Session, [f.Turn]);
        await using var first = await pg.DataSource.OpenConnectionAsync();
        await using var second = await pg.DataSource.OpenConnectionAsync();
        await using var firstTx = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var secondTx = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await AppendAsync(f, first, firstTx);
        Task waiting = AppendAsync(f, second, secondTx, expectedTurns: 3,
            unit: Hash128.OfCanonical("session-waiter/" + f.Session));
        bool sawLockWait = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15) && !waiting.IsCompleted)
        {
            await using var probe = pg.DataSource.CreateCommand(
                "SELECT wait_event_type='Lock' AND wait_event='advisory' FROM pg_stat_activity WHERE pid=$1");
            probe.Parameters.AddWithValue(second.ProcessID);
            sawLockWait = (await probe.ExecuteScalarAsync()) is true;
            if (sawLockWait) break;
            await Task.Delay(20);
        }
        Assert.True(sawLockWait, "The second real backend must wait on the shared apply advisory lock.");
        Assert.False(waiting.IsCompleted);
        await firstTx.CommitAsync();
        await waiting.WaitAsync(TimeSpan.FromSeconds(30));
        await secondTx.CommitAsync();
        var current = await ReadAsync(f.Session);
        Assert.Equal(3, current.Turns);
        Assert.Equal(5, current.Evidence.Length);
        Assert.Equal(3, current.Standing.Length);
        Assert.Equal(3, current.Evidence.Select(e => e.Split(':')[1]).Distinct().Count());
    }

}
