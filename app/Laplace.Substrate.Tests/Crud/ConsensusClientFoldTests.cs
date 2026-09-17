using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// The inline fold (consensus_upsert at apply time) must be numerically exact
/// against the native glicko-2 scalar it dispatches to. Parity is checked the
/// strongest way available: apply a batch through the writer, then recompute
/// every edge with laplace_glicko2_accumulate_period() directly and demand
/// exact int64 equality on rating/rd/volatility, plus witness accumulation and
/// last_observed_at semantics across separately admitted durable evidence.
/// A storage batch does not create another semantic rating period.
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
            "SELECT (r).rating, (r).rd, (r).volatility FROM (SELECT laplace.laplace_glicko2_accumulate_period("
            + "$1, $2, $3, ARRAY[consensus.glicko2_neutral_mu()]::bigint[], ARRAY[$4]::bigint[], "
            + "ARRAY[$5]::bigint[], ARRAY[$6]::bigint[], consensus.glicko2_tau()) AS r) s");
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
            "SELECT consensus.glicko2_neutral_mu(), consensus.glicko2_initial_rd(), consensus.glicko2_initial_volatility()");
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync());
        return (rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2));
    }

    [Fact]
    public async Task ClientFold_MatchesCanonicalServerMath_AcrossStorageBatches()
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

        // The second storage batch extends the same durable evidence period.
        var cid11 = ConsensusKeys.EdgeId(H("s1"), H("rel"), H("o1"));
        var prior = await StoredAsync(cid11);
        await using (var writer2 = new ConsensusAccumulatingWriter(inner, _pg.DataSource))
        {
            var change2 = new SubstrateChangeBuilder(H("source"), "fold-p2")
                .AddAttestation(Att("s1", "o1", 4, 750_000_000L, phi, ts2))
                .Build();
            await writer2.ApplyAsync(change2);
        }

        var stored2 = await StoredAsync(cid11);
        await using var canonical = _pg.DataSource.CreateCommand("""
            WITH folded AS MATERIALIZED (
                SELECT laplace.consensus_fold(false,NULL,NULL,NULL,
                    opponent_rating_fp1e9,opponent_rd_fp1e9,GREATEST(observation_count,1),
                    sum_score_fp1e9,consensus.glicko2_tau()
                    ORDER BY last_observed_at,id) AS result,
                    count(*) AS evidence_rows
                FROM laplace.attestations
                WHERE type_id=$1 AND subject_id=$2 AND object_id=$3
            )
            SELECT (result).rating,(result).rd,(result).volatility,
                   (result).witness_count,evidence_rows FROM folded
            """);
        canonical.Parameters.AddWithValue(H("rel").ToBytes());
        canonical.Parameters.AddWithValue(H("s1").ToBytes());
        canonical.Parameters.AddWithValue(H("o1").ToBytes());
        await using var expected = await canonical.ExecuteReaderAsync();
        Assert.True(await expected.ReadAsync());
        var expect2 = (Rating:expected.GetInt64(0), Rd:expected.GetInt64(1), Vol:expected.GetInt64(2));
        Assert.Equal(7L,expected.GetInt64(3));
        Assert.Equal(2L,expected.GetInt64(4));
        Assert.Equal(expect2.Rating, stored2.Rating);
        Assert.Equal(expect2.Rd, stored2.Rd);
        Assert.Equal(expect2.Vol, stored2.Vol);
        Assert.Equal(prior.Witnesses + 4, stored2.Witnesses);

        // last_observed_at = GREATEST(prior, batch max ts) — µs exact.
        var expectedTs = new DateTime(
            (ts2 - IntentStage.PgEpochUnixUs) * 10
            + new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, DateTimeKind.Utc);
        Assert.Equal(expectedTs, stored2.Ts);
    }
}
