using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// The Rule #8 write protocol against a live substrate: in-transaction
/// verification subtracts rows a prior apply already committed (the
/// concurrent-ingest guard), duplicate attestation collapse matches the
/// retired apply_batch semantics exactly, and the working-set journal token
/// makes a replayed flush a no-op instead of double-counting testimony.
/// </summary>
[Collection("substrate-pg")]
public class WorkingSetApplyTests
{
    private readonly LocalPgFixture _pg;

    public WorkingSetApplyTests(LocalPgFixture pg) => _pg = pg;

    private static Hash128 H(string seed) => Hash128.OfCanonical($"ws-apply-test/{seed}");

    private static EntityRow Entity(string seed) =>
        new(H(seed), 2, H("type/word"), null);

    private static PhysicalityRow Phys(string seed) => new(
        Id: H($"phys/{seed}"), EntityId: H(seed), SourceId: H("source"),
        Type: PhysicalityType.Content, CoordX: 0.1, CoordY: 0.2, CoordZ: 0.3, CoordM: 0.4,
        HilbertIndex: default, TrajectoryXyzm: null, NConstituents: 0,
        AlignmentResidual: null, SourceDim: null,
        ObservedAtUnixUs: IntentStage.PgEpochUnixUs);

    private static AttestationRow Att(string seed, long games, long unixUs) => new(
        H($"att/{seed}"), H("subj"), H("rel"), null, H("source"), null,
        AttestationOutcome.Confirm, unixUs, games,
        1_000_000_000L, 30_000_000_000L);

    private async Task<long> CountEntityAsync(Hash128 id)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.entities WHERE id = $1");
        cmd.Parameters.AddWithValue(id.ToBytes());
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(long Games, DateTime Ts)> AttStateAsync(Hash128 id)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT observation_count, last_observed_at FROM laplace.attestations WHERE id = $1");
        cmd.Parameters.AddWithValue(id.ToBytes());
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync(), $"attestation {id} not found");
        return (rd.GetInt64(0), rd.GetDateTime(1));
    }

    [Fact]
    public async Task RepeatApply_SubtractsEverything_AndFoldsAttestations()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = H("source/repeat");

        SubstrateChange Change() => new SubstrateChangeBuilder(src, "repeat-unit")
            .AddEntity(Entity("repeat/e1"))
            .AddPhysicality(Phys("repeat/e1"))
            .AddAttestation(Att("repeat", 3, IntentStage.PgEpochUnixUs + 1_000_000))
            .Build();

        var first = await writer.ApplyAsync(Change());
        Assert.Equal(1, first.EntitiesInserted);
        Assert.Equal(1, first.PhysicalitiesInserted);
        Assert.Equal(1, first.AttestationsInserted);

        var second = await writer.ApplyAsync(Change());
        Assert.Equal(0, second.EntitiesInserted);
        Assert.Equal(0, second.PhysicalitiesInserted);
        Assert.Equal(0, second.AttestationsInserted);
        Assert.Equal(1, second.EntitiesSkippedAtMerge);
        Assert.Equal(1, second.PhysicalitiesSkippedAtMerge);

        var (games, _) = await AttStateAsync(H("att/repeat"));
        Assert.Equal(6, games); // merge lane summed the repeat's counts
    }

    [Fact]
    public async Task DuplicateAttestations_CollapseToLatestRepresentative_WithSummedGames()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = H("source/dup");
        long tsOld = IntentStage.PgEpochUnixUs + 1_000_000;
        long tsNew = IntentStage.PgEpochUnixUs + 9_000_000;

        // Two changes in one apply carrying the same attestation id with
        // different counts/timestamps — apply_batch collapsed these to the
        // latest-ts representative with summed observation counts.
        var a = new SubstrateChangeBuilder(src, "dup-a")
            .AddAttestation(Att("dup", 2, tsOld)).Build();
        var b = new SubstrateChangeBuilder(src, "dup-b")
            .AddAttestation(Att("dup", 5, tsNew)).Build();

        var result = await writer.ApplyManyAsync(new[] { a, b });
        Assert.Equal(1, result.AttestationsInserted);

        var (games, ts) = await AttStateAsync(H("att/dup"));
        Assert.Equal(7, games);
        Assert.Equal(
            new DateTime((tsNew - IntentStage.PgEpochUnixUs) * 10
                         + new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
                         DateTimeKind.Utc),
            ts.ToUniversalTime());
    }

    [Fact]
    public async Task WorkingSetApply_SubtractsRowsCommittedBetweenDescentAndApply()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = H("source/subtract");

        // A concurrent ingest committed entity X (with its physicality)
        // after our descent claimed it novel — the interior-subtree case.
        var concurrent = new SubstrateChangeBuilder(src, "subtract-concurrent")
            .AddEntity(Entity("subtract/x"))
            .AddPhysicality(Phys("subtract/x"))
            .Build();
        await writer.ApplyAsync(concurrent);

        var workingSet = new SubstrateChangeBuilder(src, "subtract-ws")
            .AddEntity(Entity("subtract/x"))
            .AddPhysicality(Phys("subtract/x"))
            .AddEntity(Entity("subtract/y"))
            .AddPhysicality(Phys("subtract/y"))
            .Build();

        var result = await writer.ApplyWorkingSetAsync(workingSet);
        Assert.Equal(1, result.EntitiesInserted);
        Assert.Equal(1, result.PhysicalitiesInserted);
        Assert.Equal(1, result.EntitiesSkippedAtMerge);
        Assert.Equal(1, result.PhysicalitiesSkippedAtMerge);

        Assert.Equal(1L, await CountEntityAsync(H("subtract/y")));
        Assert.Equal(1L, await CountEntityAsync(H("subtract/x")));
    }

    [Fact]
    public async Task WorkingSetReplay_JournalTokenMakesSecondApplyNoOp()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = H("source/journal");

        var change = new SubstrateChangeBuilder(src, "journal-unit")
            .AddEntity(Entity("journal/e1"))
            .AddAttestation(Att("journal", 4, IntentStage.PgEpochUnixUs + 2_000_000))
            .Build();

        var first = await writer.ApplyWorkingSetAsync(change);
        Assert.Equal(1, first.EntitiesInserted);
        Assert.Equal(1, first.AttestationsInserted);

        // Retry after commit-ambiguity: same change, same intent hash. The
        // journal token must block the additive attestation merge that a
        // plain re-apply would perform.
        var replay = await writer.ApplyWorkingSetAsync(change);
        Assert.True(replay.TrunkShortcircuitHit);
        Assert.Equal(0, replay.EntitiesInserted);
        Assert.Equal(0, replay.AttestationsInserted);

        var (games, _) = await AttStateAsync(H("att/journal"));
        Assert.Equal(4, games); // NOT 8 — replay did not double-count

        // The same rows through the un-journaled lane DO merge (control).
        await writer.ApplyAsync(change);
        (games, _) = await AttStateAsync(H("att/journal"));
        Assert.Equal(8, games);
    }
}
