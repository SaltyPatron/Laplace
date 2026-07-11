using System.Collections.Immutable;
using System.Text;
using global::Npgsql;
using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public class NpgsqlSubstrateWriterTests
{
    private readonly LocalPgFixture _pg;

    public NpgsqlSubstrateWriterTests(LocalPgFixture pg) => _pg = pg;

    private static Hash128 H(int seed) => Hash128.Blake3(BitConverter.GetBytes(seed));

    [Fact]
    public async Task ApplyAsync_EmptyIntentIsNoOp()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = Hash128.OfCanonical("substrate/source/test/empty");
        var change = new SubstrateChangeBuilder(src, "empty-unit").Build();
        var result = await writer.ApplyAsync(change);
        Assert.Equal(0, result.EntitiesInserted);
        Assert.Equal(0, result.PhysicalitiesInserted);
        Assert.Equal(0, result.AttestationsInserted);
        Assert.True(result.TrunkShortcircuitHit);
    }

    [Fact]
    public async Task ApplyAsync_InsertsNovelEntities()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = Hash128.OfCanonical("substrate/source/test/novel-ents");
        var typeId = await EnsureTestTypeAsync(src);

        var idA = H(1001);
        var idB = H(1002);
        var change = new SubstrateChangeBuilder(src, "novel-unit")
            .AddEntity(idA, 0, typeId)
            .AddEntity(idB, 0, typeId)
            .Build();
        var result = await writer.ApplyAsync(change);

        Assert.Equal(2, result.EntitiesAttempted);
        Assert.Equal(2, result.EntitiesInserted);
        Assert.False(result.TrunkShortcircuitHit);

        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.entities WHERE id = ANY($1::bytea[])");
        var p = cmd.Parameters.AddWithValue(new[] { idA.ToBytes(), idB.ToBytes() });
        p.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea;
        var n = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(2L, n);
    }

    [Fact]
    public async Task ApplyAsync_IsIdempotentOnReapply()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = Hash128.OfCanonical("substrate/source/test/idempotent");
        var typeId = await EnsureTestTypeAsync(src);

        var change = new SubstrateChangeBuilder(src, "idem-unit")
            .AddEntity(H(2001), 0, typeId)
            .AddEntity(H(2002), 0, typeId)
            .Build();

        var first = await writer.ApplyAsync(change);
        Assert.Equal(2, first.EntitiesInserted);
        Assert.False(first.TrunkShortcircuitHit);

        var second = await writer.ApplyAsync(change);
        Assert.True(second.TrunkShortcircuitHit);
        Assert.Equal(0, second.EntitiesInserted);
    }

    [Fact]
    public async Task ApplyAsync_DedupsEntitiesAcrossIntents()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = Hash128.OfCanonical("substrate/source/test/dedup");
        var typeId = await EnsureTestTypeAsync(src);

        var shared = H(3001);
        var first = new SubstrateChangeBuilder(src, "dedup-A")
            .AddEntity(shared, 0, typeId)
            .AddEntity(H(3002), 0, typeId)
            .Build();
        var second = new SubstrateChangeBuilder(src, "dedup-B")
            .AddEntity(shared, 0, typeId)
            .AddEntity(H(3003), 0, typeId)
            .Build();

        await writer.ApplyAsync(first);
        var r = await writer.ApplyAsync(second);
        Assert.Equal(2, r.EntitiesAttempted);
        Assert.Equal(1, r.EntitiesInserted);
    }

    [Fact]
    public async Task ApplyAsync_InsertsPhysicalityAndAttestation()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = Hash128.OfCanonical("substrate/source/test/full-row");
        var typeId = await EnsureTestTypeAsync(src);
        var relTypeId = await EnsureTestRelationTypeAsync(src, "HAS_TEST");

        var subjId = H(4001);
        var change = new SubstrateChangeBuilder(src, "full-unit")
            .AddEntity(subjId, 0, typeId)
            .AddPhysicality(new PhysicalityRow(
                Id: H(4002), EntityId: subjId, SourceId: src,
                Type: PhysicalityType.Content,
                CoordX: 0.1, CoordY: 0.2, CoordZ: 0.3, CoordM: 0.4,
                HilbertIndex: Hilbert128.Encode(stackalloc double[] { 0.1, 0.2, 0.3, 0.4 }),
                TrajectoryXyzm: null,
                NConstituents: 0,
                AlignmentResidual: null,
                SourceDim: null,
                ObservedAtUnixUs: IntentStage.PgEpochUnixUs))
            .AddAttestation(new AttestationRow(
                Id: H(4003), SubjectId: subjId, TypeId: typeId,
                ObjectId: null, SourceId: src, ContextId: null,
                Outcome: AttestationOutcome.Confirm,
                LastObservedAtUnixUs: IntentStage.PgEpochUnixUs,
                ObservationCount: 1L,
                ScoreFp1e9: 1_000_000_000L,
                OpponentRdFp1e9: 30_000_000_000L))
            .Build();

        var result = await writer.ApplyAsync(change);
        Assert.Equal(1, result.EntitiesInserted);
        Assert.Equal(1, result.PhysicalitiesInserted);
        Assert.Equal(1, result.AttestationsInserted);

        await using var pCmd = _pg.DataSource.CreateCommand(
            "SELECT ST_X(coord), ST_Y(coord), ST_Z(coord), ST_M(coord) FROM laplace.physicalities WHERE id = $1");
        pCmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, H(4002).ToBytes());
        await using var rdr = await pCmd.ExecuteReaderAsync();
        Assert.True(await rdr.ReadAsync());
        Assert.Equal(0.1, rdr.GetDouble(0));
        Assert.Equal(0.4, rdr.GetDouble(3));
    }

    [Fact]
    public async Task ApplyAsync_PhysicalitiesSameEntityType_DistinctIds_CoexistAndDedupById()
    {







        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = Hash128.OfCanonical("substrate/source/test/phys-natkey");
        var typeId = await EnsureTestTypeAsync(src);
        var entId = H(9001);

        PhysicalityRow Phys(int idSeed, double x) => new(
            Id: H(idSeed), EntityId: entId, SourceId: src,
            Type: PhysicalityType.Content,
            CoordX: x, CoordY: 0.2, CoordZ: 0.3, CoordM: 0.4,
            HilbertIndex: Hilbert128.Encode(stackalloc double[] { x, 0.2, 0.3, 0.4 }),
            TrajectoryXyzm: null,
            NConstituents: 0,
            AlignmentResidual: null,
            SourceDim: null,
            ObservedAtUnixUs: IntentStage.PgEpochUnixUs);


        var change = new SubstrateChangeBuilder(src, "phys-natkey-unit")
            .AddEntity(entId, 0, typeId)
            .AddPhysicality(Phys(9101, 0.10))
            .AddPhysicality(Phys(9102, 0.99))
            .Build();

        var result = await writer.ApplyAsync(change);
        Assert.Equal(2, result.PhysicalitiesInserted);

        await using var cnt = _pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.physicalities WHERE entity_id = $1");
        cnt.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, entId.ToBytes());
        Assert.Equal(2L, (long)(await cnt.ExecuteScalarAsync())!);


        var reapplySame = new SubstrateChangeBuilder(src, "phys-natkey-reapply-same")
            .AddPhysicality(Phys(9101, 0.10))
            .AddPhysicality(Phys(9102, 0.99))
            .Build();
        var same = await writer.ApplyAsync(reapplySame);
        Assert.Equal(0, same.PhysicalitiesInserted);
        Assert.Equal(2L, (long)(await cnt.ExecuteScalarAsync())!);


        var third = new SubstrateChangeBuilder(src, "phys-natkey-third")
            .AddPhysicality(Phys(9103, 0.55))
            .Build();
        var thirdResult = await writer.ApplyAsync(third);
        Assert.Equal(1, thirdResult.PhysicalitiesInserted);
        Assert.Equal(3L, (long)(await cnt.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ApplyAsync_AcceptsForwardReference_NoPreCheck()
    {




        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = Hash128.OfCanonical("substrate/source/test/forwardref");
        var typeId = await EnsureTestTypeAsync(src);

        var goodEntity = H(5001);
        var missingSubject = H(5099);
        var relationTypeId = await EnsureTestRelationTypeAsync(src, "HAS_TEST_ROLLBACK");
        var change = new SubstrateChangeBuilder(src, "forwardref-unit")
            .AddEntity(goodEntity, 0, relationTypeId)
            .AddAttestation(new AttestationRow(
                H(5002), missingSubject, relationTypeId, null, src, null,
                AttestationOutcome.Confirm, IntentStage.PgEpochUnixUs, 1,
                1_000_000_000L, 30_000_000_000L))
            .Build();

        var result = await writer.ApplyAsync(change);
        Assert.Equal(1, result.EntitiesInserted);
        Assert.Equal(1, result.AttestationsInserted);

        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.entities WHERE id = $1");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, goodEntity.ToBytes());
        var n = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, n);
    }

    [Fact]
    public async Task SubstrateReader_CountByTypeAndExistBitmapWork()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var src = Hash128.OfCanonical("substrate/source/test/reader");
        var typeId = await EnsureTestTypeAsync(src);

        var idA = H(6001);
        var idB = H(6002);
        await writer.ApplyAsync(new SubstrateChangeBuilder(src, "reader-unit")
            .AddEntity(idA, 0, typeId)
            .AddEntity(idB, 0, typeId)
            .Build());

        var count = await reader.CountEntitiesByTypeAsync(typeId);
        Assert.True(count >= 2L);

        var bitmap = await reader.EntitiesExistBitmapAsync(new[] { idA, H(6099), idB });
        Assert.Single(bitmap);
        Assert.Equal((byte)0b00000101, bitmap[0]);
    }







    [Fact]
    public async Task ApplyAsync_ReobservedAttestation_AccumulatesGames()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = Hash128.OfCanonical("substrate/source/test/att-reobserve");
        var typeId = await EnsureTestTypeAsync(src);
        var relTypeId = await EnsureTestRelationTypeAsync(src, "HAS_TEST");

        var subj = H(8101);
        var obj = H(8102);
        var attId = H(8103);
        AttestationRow Row(long games) => new(
            Id: attId, SubjectId: subj, TypeId: relTypeId,
            ObjectId: obj, SourceId: src, ContextId: null,
            Outcome: AttestationOutcome.Confirm,
            LastObservedAtUnixUs: IntentStage.PgEpochUnixUs + games,
            ObservationCount: games,
            ScoreFp1e9: 1_000_000_000L,
            OpponentRdFp1e9: 30_000_000_000L);

        var first = await writer.ApplyAsync(new SubstrateChangeBuilder(src, "att-u1")
            .AddEntity(subj, 0, typeId)
            .AddEntity(obj, 0, typeId)
            .AddAttestation(Row(3))
            .Build());
        Assert.Equal(1, first.AttestationsInserted);

        var second = await writer.ApplyAsync(new SubstrateChangeBuilder(src, "att-u2")
            .AddAttestation(Row(5))
            .Build());
        Assert.Equal(0, second.AttestationsInserted);
        Assert.False(second.TrunkShortcircuitHit);

        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT observation_count FROM laplace.attestations WHERE id = $1");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, attId.ToBytes());
        Assert.Equal(8L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ApplyManyAsync_CoalescesPrebuiltContentStages_BoundedRoundTrips()
    {
        CodepointPerfcache.LoadDefault();
        IntentStage.ResetContentBank();

        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var src = Hash128.OfCanonical("substrate/source/test/content-coalesce");
        await EnsureTestTypeAsync(src);

        var batch = Enumerable.Range(0, 8)
            .Select(i =>
            {
                var b = new SubstrateChangeBuilder(src, $"u{i}");
                Assert.True(b.ContentStage.TryAddContentWitness(
                    Encoding.UTF8.GetBytes($"word{i}"), src, out _));
                return b.Build();
            })
            .ToList();

        var result = await writer.ApplyManyAsync(batch);
        Assert.True(result.EntitiesInserted > 0);





        int parts = Math.Clamp(CpuTopology.PerformanceCoreCount, 1, 16);
        int perPartitionCalls = 5;
        int budget = parts * perPartitionCalls;
        Assert.True(result.RoundTrips <= budget,
            $"coalesced batch should be O(partitions) DB calls (<= {budget} for {parts} partitions), "
            + $"got {result.RoundTrips}");
    }

    private async Task<Hash128> EnsureTestTypeAsync(Hash128 source)
    {
        var typeId = Hash128.OfCanonical("TestFixture");
        await using var cmdType = _pg.DataSource.CreateCommand(
            "INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) "
          + "VALUES ($1, 0::smallint, $1, NULL) ON CONFLICT (id) DO NOTHING");
        cmdType.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, typeId.ToBytes());
        await cmdType.ExecuteNonQueryAsync();

        await using var cmdSrc = _pg.DataSource.CreateCommand(
            "INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) "
          + "VALUES ($1, 0::smallint, $2, NULL) ON CONFLICT (id) DO NOTHING");
        cmdSrc.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, source.ToBytes());
        cmdSrc.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, typeId.ToBytes());
        await cmdSrc.ExecuteNonQueryAsync();
        return typeId;
    }

    private async Task<Hash128> EnsureTestRelationTypeAsync(Hash128 source, string name)
    {
        var typeId = Hash128.OfCanonical($"substrate/type/{name}/v1");
        var relTypeId = Hash128.OfCanonical("TestFixture");
        await using var cmd = _pg.DataSource.CreateCommand(
            "INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) "
          + "VALUES ($1, 0::smallint, $2, NULL) ON CONFLICT (id) DO NOTHING");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, typeId.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, typeId.ToBytes());
        await cmd.ExecuteNonQueryAsync();
        return typeId;
    }
}
