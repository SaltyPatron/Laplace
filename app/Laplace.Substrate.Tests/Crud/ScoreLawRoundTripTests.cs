using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
public class ScoreLawRoundTripTests
{
    private readonly LocalPgFixture _pg;
    private readonly ITestOutputHelper _out;

    public ScoreLawRoundTripTests(LocalPgFixture pg, ITestOutputHelper output)
    {
        _pg = pg;
        _out = output;
    }

    private const long PhiModel = 30_000_000_000L;
    private const long PhiWeak = 350_000_000_000L;
    private const double ArenaM = 0.02;

    private static long ForwardSumFp(double v, double m, long games)
        => games * ScoreLaw.ScoreFp(v, m);

    private async Task<long> FoldRatingAsync(long phi, long games, long sumFp)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT (laplace.laplace_glicko2_accumulate_games("
          + "1500000000000,350000000000,60000000,1500000000000,$1,$2,$3,500000000)).rating");
        cmd.Parameters.AddWithValue(phi);
        cmd.Parameters.AddWithValue(games);
        cmd.Parameters.AddWithValue(sumFp);
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    [Theory]
    [InlineData(PhiModel, 1L)]
    [InlineData(PhiModel, 22L)]
    [InlineData(PhiWeak, 1L)]
    [InlineData(PhiWeak, 22L)]
    public async Task FaithfulBand_RecoversWeightsWithinThreePercentOfM(long phi, long games)
    {
        var inverse = new CalibratedInverse(_pg.DataSource, phi);
        double[] overM = [-2.0, -1.0, -0.5, -0.1, 0.1, 0.5, 1.0, 2.0];
        foreach (double w in overM)
        {
            double v = w * ArenaM;
            long rating = await FoldRatingAsync(phi, games, ForwardSumFp(v, ArenaM, games));
            double recovered = inverse.Wom(rating, games) * ArenaM;
            double err = Math.Abs(recovered - v);
            _out.WriteLine($"phi={phi / 1e9} n={games} v/M={w,5:F2} v={v,9:F6} recovered={recovered,9:F6} err/M={err / ArenaM:F4}");
            Assert.True(err <= 0.03 * ArenaM,
                $"faithful band violated: v/M={w} err/M={err / ArenaM:F4} (phi={phi}, n={games})");
        }
    }

    [Theory]
    [InlineData(PhiModel, 1L)]
    [InlineData(PhiModel, 22L)]
    public async Task Recovery_IsMonotoneInWeight(long phi, long games)
    {
        var inverse = new CalibratedInverse(_pg.DataSource, phi);
        double prev = double.NegativeInfinity;
        for (double w = -5.0; w <= 5.0; w += 0.25)
        {
            long rating = await FoldRatingAsync(phi, games, ForwardSumFp(w * ArenaM, ArenaM, games));
            double recovered = inverse.Wom(rating, games);
            Assert.True(recovered >= prev - 1e-9,
                $"non-monotone recovery at w={w}: {recovered} < {prev}");
            prev = recovered;
        }
    }

    [Theory]
    [InlineData(PhiModel, 1L)]
    [InlineData(PhiModel, 22L)]
    public async Task OutlierRegime_SurvivesAndStaysDistinguishable(long phi, long games)
    {
        var inverse = new CalibratedInverse(_pg.DataSource, phi);
        double[] outliersOverM = [4.0, 6.0, 10.0, 30.0, 100.0];
        double prev = 0;
        foreach (double w in outliersOverM)
        {
            double v = w * ArenaM;
            long rating = await FoldRatingAsync(phi, games, ForwardSumFp(v, ArenaM, games));
            double recovered = inverse.Wom(rating, games) * ArenaM;
            double survival = recovered / v;
            _out.WriteLine($"OUTLIER phi={phi / 1e9} n={games} v/M={w,6:F1} recovered/M={recovered / ArenaM:F3} survival={survival:P1}");
            Assert.True(survival >= 0.80,
                $"outlier crushed: v/M={w} survival={survival:P1} (rational law must not saturate in-range)");
            Assert.True(recovered > prev + 1e-9,
                $"outliers not distinguishable: v/M={w} recovered/M={recovered / ArenaM:F3} <= prev {prev / ArenaM:F3}");
            prev = recovered;
        }
    }
}
