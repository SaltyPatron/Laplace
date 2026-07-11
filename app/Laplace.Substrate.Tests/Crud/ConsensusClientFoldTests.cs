using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// The client-side period fold must be numerically indistinguishable from
/// the retired server fold. Both sides call the same native glicko-2
/// library, so parity is checked the strongest way available: fold through
/// the client lane, then recompute every edge with the SERVER's own
/// laplace_glicko2_accumulate_games() SQL binding (what
/// materialize_period_partition called per row) and demand exact int64
/// equality on rating/rd/volatility, plus witness accumulation and
/// last_observed_at semantics across a second period folding against priors.
/// </summary>
[Collection("substrate-pg")]
[Trait("Tier", "db")]
public class ConsensusClientFoldTests
{
    private readonly LocalPgFixture _pg;

    public ConsensusClientFoldTests(LocalPgFixture pg) => _pg = pg;

    private static Hash128 H(string seed) => Hash128.OfCanonical($"client-fold-test/{seed}");

    private static AttestationRow Att(
        string subj, string obj, long games, long scoreFp, long phiFp, long unixUs) => new(
        Hash128.OfCanonical($"client-fold-test/att/{subj}/{obj}/{unixUs}"),
        H(subj), H("rel"), H(obj), H("source"), null,
        AttestationOutcome.Confirm, unixUs, games, scoreFp, phiFp);

    private async Task<(long Rating, long Rd, long Vol, long Witnesses, DateTime Ts)> StoredAsync(Hash128 cid)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT rating, rd, volatility, witness_count, last_observed_at "
            + "FROM laplace.consensus WHERE id = $1");
        cmd.Parameters.AddWithValue(cid.ToBytes());
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync(), $"consensus row {cid} not found");
        return (rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2), rd.GetInt64(3),
                rd.GetDateTime(4).ToUniversalTime());
    }

    private async Task<(long Rating, long Rd, long Vol)> ServerMathAsync(
        long priorRating, long priorRd, long priorVol, long phi, long games, long sumScore)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT (r).rating, (r).rd, (r).volatility FROM (SELECT laplace.laplace_glicko2_accumulate_games("
            + "$1, $2, $3, laplace.glicko2_neutral_mu(), $4, $5, $6, laplace.glicko2_tau()) AS r) s");
        cmd.Parameters.AddWithValue(priorRating);
        cmd.Parameters.AddWithValue(priorRd);
        cmd.Parameters.AddWithValue(priorVol);
        cmd.Parameters.AddWithValue(phi);
        cmd.Parameters.AddWithValue(games);
        cmd.Parameters.AddWithValue(sumScore);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync());
        return (rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2));
    }

    private async Task<(long NeutralMu, long InitRd, long InitVol)> SeedsAsync()
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT laplace.glicko2_neutral_mu(), laplace.glicko2_initial_rd(), laplace.glicko2_initial_volatility()");
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync());
        return (rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2));
    }

    [Fact]
    public async Task ClientFold_MatchesServerMath_FreshAndSeededPeriods()
    {
        long phi = 30_000_000_000L;
        long ts1 = IntentStage.PgEpochUnixUs + 11_000_000;
        long ts2 = IntentStage.PgEpochUnixUs + 22_000_000;
        var (neutralMu, initRd, initVol) = await SeedsAsync();

        var inner = new NpgsqlSubstrateWriter(_pg.DataSource);
        await using (var writer = new ConsensusAccumulatingWriter(inner, _pg.DataSource))
        {
            var change = new SubstrateChangeBuilder(H("source"), "fold-p1")
                .AddAttestation(Att("s1", "o1", 3, 1_000_000_000L, phi, ts1))
                .AddAttestation(Att("s1", "o2", 5, 500_000_000L, phi, ts1))
                .AddAttestation(Att("s2", "o1", 1, 0L, phi, ts1))
                .Build();
            await writer.ApplyAsync(change);
            await writer.MaterializeConsensusAsync();
        }

        // Every edge exact-equal to the server's own per-row math from neutral seeds.
        var edges = new (string S, string O, long Games, long Sum)[]
        {
            ("s1", "o1", 3, 3 * 1_000_000_000L),
            ("s1", "o2", 5, 5 * 500_000_000L),
            ("s2", "o1", 1, 0L),
        };
        foreach (var (s, o, games, sum) in edges)
        {
            var cid = ConsensusKeys.EdgeId(H(s), H("rel"), H(o));
            var stored = await StoredAsync(cid);
            var expect = await ServerMathAsync(neutralMu, initRd, initVol, phi, games, sum);
            Assert.Equal(expect.Rating, stored.Rating);
            Assert.Equal(expect.Rd, stored.Rd);
            Assert.Equal(expect.Vol, stored.Vol);
            Assert.Equal(games, stored.Witnesses);
        }

        // Second period folds against the stored priors.
        var cid11 = ConsensusKeys.EdgeId(H("s1"), H("rel"), H("o1"));
        var prior = await StoredAsync(cid11);
        await using (var writer2 = new ConsensusAccumulatingWriter(inner, _pg.DataSource))
        {
            var change2 = new SubstrateChangeBuilder(H("source"), "fold-p2")
                .AddAttestation(Att("s1", "o1", 4, 750_000_000L, phi, ts2))
                .Build();
            await writer2.ApplyAsync(change2);
            await writer2.MaterializeConsensusAsync();
        }

        var stored2 = await StoredAsync(cid11);
        var expect2 = await ServerMathAsync(
            prior.Rating, prior.Rd, prior.Vol, phi, 4, 4 * 750_000_000L);
        Assert.Equal(expect2.Rating, stored2.Rating);
        Assert.Equal(expect2.Rd, stored2.Rd);
        Assert.Equal(expect2.Vol, stored2.Vol);
        Assert.Equal(prior.Witnesses + 4, stored2.Witnesses);

        // last_observed_at = the period's max ts (overwrite semantics, µs exact).
        var expectedTs = new DateTime(
            (ts2 - IntentStage.PgEpochUnixUs) * 10
            + new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, DateTimeKind.Utc);
        Assert.Equal(expectedTs, stored2.Ts);
    }
}
