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
[Trait("Tier", "db")]
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

    private static SubstrateChange PrebuiltEntityChange(
        Hash128 source, string unit, Hash128 entityId)
    {
        var stage = IntentStage.New(1);
        stage.AddEntity(entityId, 2, H("type/word"), source);
        return new SubstrateChangeBuilder(source, unit)
            .AddIntentStage(stage)
            .Build();
    }

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
    public async Task WorkingSetApply_DeduplicatesEntitiesAcrossPrebuiltIntentStages()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = H("source/prebuilt-dup");
        var entityId = H("prebuilt-dup/entity");

        // High-volume decomposers yield one native IntentStage per chunk.
        // IngestRunner combines many chunks into one working-set apply, so the
        // shared writer must enforce the same cross-change identity dedup that
        // the managed-row aggregation already performs.
        var a = PrebuiltEntityChange(src, "prebuilt-dup/a", entityId);
        var b = PrebuiltEntityChange(src, "prebuilt-dup/b", entityId);

        var result = await writer.ApplyWorkingSetAsync(new[] { a, b });

        Assert.Equal(1, result.EntitiesInserted);
        Assert.Equal(1L, await CountEntityAsync(entityId));
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
    public async Task AttestationsEmbeddingNovelEntities_InsertWithoutProbe_ThenMergeWhenPresent()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = H("source/structural");
        var subj = H("structural/e1");

        SubstrateChange Change(string unit, long games) => new SubstrateChangeBuilder(src, unit)
            .AddEntity(Entity("structural/e1"))
            .AddPhysicality(Phys("structural/e1"))
            .AddAttestation(new AttestationRow(
                H("att/structural"), subj, H("rel"), null, src, subj,
                AttestationOutcome.Confirm, IntentStage.PgEpochUnixUs + 3_000_000, games,
                1_000_000_000L, 30_000_000_000L))
            .Build();

        // First working set: the attestation's subject/context entity is
        // novel in the SAME batch, so the structural filter proves it novel
        // without a probe — it must still COPY.
        var first = await writer.ApplyWorkingSetAsync(Change("structural-a", 2));
        Assert.Equal(1, first.EntitiesInserted);
        Assert.Equal(1, first.AttestationsInserted);
        var (games, _) = await AttStateAsync(H("att/structural"));
        Assert.Equal(2, games);

        // Fresh unit (new journal token), same content: the entity is
        // present now, the filter no longer fires, and the attestation rides
        // the routed merge lane.
        var second = await writer.ApplyWorkingSetAsync(Change("structural-b", 5));
        Assert.Equal(0, second.AttestationsInserted);
        (games, _) = await AttStateAsync(H("att/structural"));
        Assert.Equal(7, games);
    }

    /// <summary>
    /// Reads how many times laplace.apply_write_epoch has been bumped, or null
    /// when the installed extension predates the sequence. Two round trips on
    /// purpose: a single statement embedding "FROM laplace.apply_write_epoch"
    /// fails at PARSE time when the relation is absent, which is exactly the
    /// un-upgraded case this probe exists to detect. is_called matters: a fresh
    /// sequence reports last_value = 1 BEFORE any nextval, and the first
    /// nextval also returns 1 — raw last_value cannot see the first bump.
    /// </summary>
    private async Task<long?> ReadWriteEpochAsync()
    {
        await using (var probe = _pg.DataSource.CreateCommand(
            "SELECT to_regclass('laplace.apply_write_epoch') IS NOT NULL"))
        {
            if (await probe.ExecuteScalarAsync() is not true) return null;
        }
        await using var read = _pg.DataSource.CreateCommand(
            "SELECT CASE WHEN is_called THEN last_value ELSE last_value - 1 END "
            + "FROM laplace.apply_write_epoch");
        return (long)(await read.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task WorkingSetApply_AdvancesWriteEpoch()
    {
        // PR1 write-epoch law: every write-lane transaction bumps
        // laplace.apply_write_epoch before writing, and a database whose
        // installed extension predates the sequence degrades to the exact
        // pre-epoch behavior. The fixture installs whatever laplace_substrate
        // the host PostgreSQL ships, so both branches are legitimate here:
        // sequence present ⇒ the apply must advance it; absent ⇒ the apply
        // must still succeed untouched (the degradation guard is the assert).
        var before = await ReadWriteEpochAsync();

        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var change = new SubstrateChangeBuilder(H("source/epoch"), "epoch-unit")
            .AddEntity(Entity("epoch/e1"))
            .Build();
        var result = await writer.ApplyWorkingSetAsync(change);
        Assert.Equal(1, result.EntitiesInserted);

        var after = await ReadWriteEpochAsync();
        if (before is null)
        {
            Assert.Null(after); // un-upgraded DB: no sequence appeared, apply degraded cleanly
            return;
        }
        Assert.True(after > before,
            $"apply committed but the write epoch did not advance ({before} -> {after})");
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
        await using (var sourceClaim = _pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.ingest_flush_journal WHERE source_id = $1"))
        {
            sourceClaim.Parameters.AddWithValue(src.ToBytes());
            Assert.Equal(1L, (long)(await sourceClaim.ExecuteScalarAsync())!);
        }

        // Retry after commit-ambiguity: same change, same intent hash. The
        // journal token must block the additive attestation merge that a
        // plain re-apply would perform.
        var replay = await writer.ApplyWorkingSetAsync(new[] { change });
        Assert.True(replay.TrunkShortcircuitHit);
        Assert.Equal(0, replay.EntitiesInserted);
        Assert.Equal(0, replay.AttestationsInserted);

        var (games, _) = await AttStateAsync(H("att/journal"));
        Assert.Equal(4, games); // NOT 8 — replay did not double-count

        // Observation time is operational freshness, not semantic payload: rebuilding
        // the exact testimony later remains the same replay token.
        var laterReplay = new SubstrateChangeBuilder(src, "journal-unit")
            .AddEntity(Entity("journal/e1"))
            .AddAttestation(Att("journal", 4, IntentStage.PgEpochUnixUs + 8_000_000))
            .Build();
        Assert.True((await writer.ApplyWorkingSetAsync(laterReplay)).JournalReplayHit);
        (games, _) = await AttStateAsync(H("att/journal"));
        Assert.Equal(4, games);

        // A changed aggregate with the same row IDs is new semantic payload, so v2
        // admits it instead of mistaking the legacy ID-only token for an exact replay.
        var enriched = new SubstrateChangeBuilder(src, "journal-unit")
            .AddEntity(Entity("journal/e1"))
            .AddAttestation(Att("journal", 5, IntentStage.PgEpochUnixUs + 9_000_000))
            .Build();
        Assert.False((await writer.ApplyWorkingSetAsync(enriched)).JournalReplayHit);
        (games, _) = await AttStateAsync(H("att/journal"));
        Assert.Equal(9, games);

        // The same rows through the un-journaled lane DO merge (control).
        await writer.ApplyAsync(change);
        (games, _) = await AttStateAsync(H("att/journal"));
        Assert.Equal(13, games);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkingSetReplay_BothLegacySingletonAliasesPreventSilentPostUpgradeReapply(
        bool rawIntentAlias)
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        string suffix = rawIntentAlias ? "raw" : "list-one";
        var src = H($"source/legacy-journal/{suffix}");
        var change = new SubstrateChangeBuilder(src, $"legacy-journal-unit/{suffix}")
            .AddEntity(Entity($"legacy-journal/{suffix}/e1"))
            .AddAttestation(Att($"legacy-journal/{suffix}", 3, IntentStage.PgEpochUnixUs))
            .Build();
        byte[] intentBytes = new byte[16];
        change.Metadata.IntentId.WriteBytes(intentBytes);
        Hash128 legacyAlias = rawIntentAlias
            ? change.Metadata.IntentId
            : Hash128.Blake3(intentBytes);

        await using (var seed = _pg.DataSource.CreateCommand(
            "INSERT INTO laplace.ingest_flush_journal (working_set_id, source_id) VALUES ($1, $2)"))
        {
            seed.Parameters.AddWithValue(legacyAlias.ToBytes());
            seed.Parameters.AddWithValue(src.ToBytes());
            await seed.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<LegacyReplayRequiresReconciliationException>(
            () => writer.ApplyWorkingSetAsync(change));
        Assert.Equal(0L, await CountEntityAsync(H($"legacy-journal/{suffix}/e1")));
    }

    [Fact]
    public async Task WorkingSetReplay_NativeStageDigestTracksSemanticsButNotClocks()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = H("source/native-replay");

        SubstrateChange Change(long games, long observedAt)
        {
            var stage = IntentStage.New(1);
            stage.AddAttestation(
                H("att/native-replay"), H("subj"), H("rel"), null, H("source"), null,
                (short)AttestationOutcome.Confirm, observedAt, games,
                games * 1_000_000_000L, 30_000_000_000L);
            return new SubstrateChangeBuilder(src, "native-replay-unit")
                .AddIntentStage(stage)
                .Build();
        }

        Assert.False((await writer.ApplyWorkingSetAsync(Change(
            1, IntentStage.PgEpochUnixUs))).JournalReplayHit);
        Assert.True((await writer.ApplyWorkingSetAsync(Change(
            1, IntentStage.PgEpochUnixUs + 1_000_000))).JournalReplayHit);
        Assert.False((await writer.ApplyWorkingSetAsync(Change(
            2, IntentStage.PgEpochUnixUs + 2_000_000))).JournalReplayHit);

        var (games, _) = await AttStateAsync(H("att/native-replay"));
        Assert.Equal(3, games);
    }
}
