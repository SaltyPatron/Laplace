using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public class ConsensusAccumulatingWriterTests
{
    private readonly LocalPgFixture _pg;
    public ConsensusAccumulatingWriterTests(LocalPgFixture pg) => _pg = pg;

    private static Hash128 H(ulong n) => Hash128.OfCanonical($"substrate/test/consensus-accumulation/{n}");

    private const long PhiTrust = 30_000_000_000L;
    private const long PhiCrank = 300_000_000_000L;

    private async Task<Hash128> EnsureScaffoldAsync(params Hash128[] ids)
    {
        var typeId = Hash128.OfCanonical("FoldingTestFixture");
        await using var cmd = _pg.DataSource.CreateCommand(
            "INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) "
          + "VALUES ($1, 0::smallint, $1, NULL) ON CONFLICT (id, tier) DO NOTHING");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, typeId.ToBytes());
        await cmd.ExecuteNonQueryAsync();

        foreach (var id in ids)
        {
            await using var c = _pg.DataSource.CreateCommand(
                "INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) "
              + "VALUES ($1, 0::smallint, $2, NULL) ON CONFLICT (id, tier) DO NOTHING");
            c.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, id.ToBytes());
            c.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, typeId.ToBytes());
            await c.ExecuteNonQueryAsync();
        }
        return typeId;
    }

    private static AttestationRow Obs(
        Hash128 id, Hash128 subj, Hash128 relType, Hash128? obj, Hash128 src,
        long score, long phi = PhiTrust, long games = 1, Hash128? ctx = null) =>
        new(id, subj, relType, obj, src, ctx,
            Outcome: score > 500_000_000L ? AttestationOutcome.Confirm
                   : score < 500_000_000L ? AttestationOutcome.Refute
                                          : AttestationOutcome.Draw,
            LastObservedAtUnixUs: 1_770_000_000_000_000L, ObservationCount: games,
            ScoreFp1e9: score, OpponentRdFp1e9: phi);

    private static SubstrateChange Change(Hash128 src, string unit, params AttestationRow[] rows) =>
        new(ImmutableArray<EntityRow>.Empty,
            ImmutableArray<PhysicalityRow>.Empty,
            rows.ToImmutableArray(),
            new SubstrateChangeMetadata(Hash128.OfCanonical($"intent/{unit}"), src, unit,
                                        DateTimeOffset.UnixEpoch, null));

    private async Task<(long rating, long rd, long vol, long wc)?> ConsensusRowAsync(
        Hash128 subj, Hash128 relType, Hash128? obj)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT rating, rd, volatility, witness_count FROM laplace.consensus "
          + "WHERE subject_id = $1 AND type_id = $2 AND object_id IS NOT DISTINCT FROM $3");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, subj.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, relType.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea,
            (object?)obj?.ToBytes() ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
    }

    private const long KernelToleranceFp = 1_000;

    private static void AssertWithinKernelTolerance(long expected, long actual)
        => Assert.InRange(actual, expected - KernelToleranceFp, expected + KernelToleranceFp);

    [Fact]
    public async Task Accumulation_FoldsSignedMultisetIntoConsensus()
    {
        var src = H(100); var relType = H(102);
        var s1 = H(110); var s2 = H(111);
        var o1 = H(120); var o2 = H(121);
        var c1 = H(130); var c2 = H(131); var c3 = H(132);
        await EnsureScaffoldAsync(src, relType, s1, s2, o1, o2, c1, c2, c3);

        var rows = new[]
        {
            Obs(H(2000), s1, relType, o1, src, score: 900_000_000, ctx: c1),
            Obs(H(2001), s1, relType, o1, src, score: 731_058_578, ctx: c2),
            Obs(H(2002), s1, relType, o1, src, score: 500_000_000, games: 5, ctx: c3),
            Obs(H(2003), s2, relType, o1, src, score: 100_000_000, ctx: c1),
            Obs(H(2004), s2, relType, o2, src, score: 999_999_999, ctx: c1),
            Obs(H(2005), s1, relType, null, src, score: 800_000_001, games: 3, ctx: c2),
        };

        await using var accumulator = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await accumulator.ApplyManyAsync(new[] { Change(src, "acc-fold", rows) });
        Assert.Equal(4L, accumulator.CellsFolded);

        const long Neutral = 1_500_000_000_000L;
        var confirmed = await ConsensusRowAsync(s1, relType, o1);
        Assert.NotNull(confirmed);
        Assert.Equal(7L, confirmed!.Value.wc);
        Assert.True(confirmed.Value.rating > Neutral);

        var refuted = await ConsensusRowAsync(s2, relType, o1);
        Assert.NotNull(refuted);
        Assert.Equal(1L, refuted!.Value.wc);
        Assert.True(refuted.Value.rating < Neutral);

        var strong = await ConsensusRowAsync(s2, relType, o2);
        Assert.NotNull(strong);
        Assert.True(strong!.Value.rating > confirmed.Value.rating - 500_000_000_000L);

        var unary = await ConsensusRowAsync(s1, relType, null);
        Assert.NotNull(unary);
        Assert.Equal(3L, unary!.Value.wc);
    }

    [Fact]
    public async Task BulkRun_PipelinesFolds_AndDrainsAtComplete()
    {
        var src = H(900); var relType = H(901); var subj = H(910);
        var o1 = H(920); var o2 = H(921);
        await EnsureScaffoldAsync(src, relType, subj, o1, o2);

        await using var accumulator = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await accumulator.BeginBulkRunAsync();
        await accumulator.ApplyWorkingSetAsync(
            Change(src, "bulk-fold-a", Obs(H(930), subj, relType, o1, src, 900_000_000)));
        await accumulator.ApplyWorkingSetAsync(
            Change(src, "bulk-fold-b", Obs(H(931), subj, relType, o2, src, 900_000_000)));
        // Bulk-run folds are queued behind the apply lane; completing the run
        // drains the pipeline, so both cells must be folded and visible here.
        await accumulator.CompleteBulkRunAsync();

        Assert.Equal(2L, accumulator.CellsFolded);
        Assert.NotNull(await ConsensusRowAsync(subj, relType, o1));
        Assert.NotNull(await ConsensusRowAsync(subj, relType, o2));
    }

    [Fact]
    public async Task Accumulation_PersistsProvenanceOnlyEvidence()
    {
        var src = H(200); var relType = H(201); var subj = H(210); var obj = H(220);
        await EnsureScaffoldAsync(src, relType, subj, obj);

        await using var accumulator = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        var r = await accumulator.ApplyManyAsync(new[]
            { Change(src, "acc-prov",
                Obs(H(230), subj, relType, obj, src, 900_000_000),
                Obs(H(231), subj, relType, null, src, 100_000_000)) });
        Assert.Equal(2, r.AttestationsInserted);

        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT outcome, observation_count FROM laplace.attestations "
          + "WHERE type_id = $1 ORDER BY outcome DESC");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, relType.ToBytes());
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync());
        Assert.Equal((short)2, rd.GetInt16(0));
        Assert.True(await rd.ReadAsync());
        Assert.Equal((short)0, rd.GetInt16(0));
        Assert.False(await rd.ReadAsync());

        Assert.NotNull(await ConsensusRowAsync(subj, relType, obj));
    }

    [Fact]
    public async Task Production_PassesThroughLayerCompletionMarker()
    {
        var src = H(300); var relType = H(301);
        await EnsureScaffoldAsync(src, relType);

        await using var accumulator = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await accumulator.ApplyAsync(Change(src, "layer-complete/4",
            Obs(H(330), src, relType, src, src, 1_000_000_000)));

        Assert.Equal(0L, accumulator.CellsFolded);
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.attestations WHERE type_id = $1");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, relType.ToBytes());
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Production_MixedPhiFailsLoud()
    {
        var src = H(400); var relType = H(401); var subj = H(410); var obj = H(420);
        await using var accumulator = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accumulator.ApplyManyAsync(new[] { Change(src, "acc-phi",
                Obs(H(430), subj, relType, obj, src, 900_000_000, phi: PhiTrust),
                Obs(H(431), subj, relType, obj, src, 800_000_000, phi: PhiCrank)) }));
    }

    [Fact]
    public async Task Production_SecondPeriodAccumulatesOnPrior()
    {
        var src = H(500); var relType = H(501); var subj = H(510); var obj = H(520);
        await EnsureScaffoldAsync(src, relType, subj, obj);

        await using var p1 = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await p1.ApplyAsync(Change(src, "acc-p1",
            Obs(H(530), subj, relType, obj, src, 900_000_000, games: 2)));
        var after1 = await ConsensusRowAsync(subj, relType, obj);
        Assert.NotNull(after1);
        Assert.Equal(2L, after1!.Value.wc);
        Assert.True(after1.Value.rating > 1_500_000_000_000L);

        await using var p2 = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await p2.ApplyAsync(Change(src, "acc-p2",
            Obs(H(531), subj, relType, obj, src, 900_000_000, games: 3)));
        var after2 = await ConsensusRowAsync(subj, relType, obj);
        Assert.NotNull(after2);
        Assert.Equal(5L, after2!.Value.wc);
        Assert.True(after2.Value.rating > after1.Value.rating);
        Assert.True(after2.Value.rd < after1.Value.rd);
    }

    [Fact]
    public async Task Production_IsBitExactDeterministic()
    {
        var src = H(600); var relTypeX = H(601); var relTypeY = H(602);
        var subj = H(610); var obj = H(620); var c1 = H(630); var c2 = H(631);
        await EnsureScaffoldAsync(src, relTypeX, relTypeY, subj, obj, c1, c2);

        AttestationRow[] Rows(Hash128 relType, ulong idBase) => new[]
        {
            Obs(H(idBase + 0), subj, relType, obj, src, score: 873_215_991, ctx: c1),
            Obs(H(idBase + 1), subj, relType, obj, src, score: 212_999_117, games: 4, ctx: c2),
        };

        await using var ax = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await ax.ApplyManyAsync(new[] { Change(src, "acc-det/x", Rows(relTypeX, 3000)) });

        await using var ay = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await ay.ApplyManyAsync(new[] { Change(src, "acc-det/y", Rows(relTypeY, 3100)) });

        var x = await ConsensusRowAsync(subj, relTypeX, obj);
        var y = await ConsensusRowAsync(subj, relTypeY, obj);
        Assert.NotNull(x);
        Assert.NotNull(y);
        Assert.Equal(x!.Value.rating, y!.Value.rating);
        Assert.Equal(x.Value.rd, y.Value.rd);
        Assert.Equal(x.Value.vol, y.Value.vol);
        Assert.Equal(x.Value.wc, y.Value.wc);
    }
}
