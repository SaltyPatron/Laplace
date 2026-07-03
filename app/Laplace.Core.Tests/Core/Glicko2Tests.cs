using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

public class Glicko2Tests
{
    [Fact]
    public void FpScale_Is1e9_PerAdr0004()
    {
        Assert.Equal(1_000_000_000L, Glicko2.FpScale);
    }

    [Fact]
    public void DefaultPrior_MatchesGlicko1Convention()
    {
        Assert.Equal(1_500L * Glicko2.FpScale, Glicko2.DefaultRatingFp1e9);
        Assert.Equal(350L * Glicko2.FpScale, Glicko2.DefaultRdFp1e9);
        Assert.Equal(60_000_000L, Glicko2.DefaultVolatilityFp1e9);
    }

    [Fact]
    public void ScoreConstants_MatchGlicko2Convention()
    {
        Assert.Equal(0L, Glicko2.ScoreLoss);
        Assert.Equal(500_000_000L, Glicko2.ScoreDraw);
        Assert.Equal(1_000_000_000L, Glicko2.ScoreWin);
    }

    [Fact]
    public void EffectiveMu_AtPrior_Is1500Minus2x350Equal800()
    {
        long effMu = Glicko2.EffectiveMuFp1e9(
            Glicko2.DefaultRatingFp1e9, Glicko2.DefaultRdFp1e9);
        Assert.Equal(800L * Glicko2.FpScale, effMu);
    }

    [Fact]
    public void EffectiveMu_HighRdGivesNegative()
    {
        long effMu = Glicko2.EffectiveMuFp1e9(
            1_000L * Glicko2.FpScale, 600L * Glicko2.FpScale);
        Assert.Equal(-200L * Glicko2.FpScale, effMu);
    }

    [Fact]
    public void UpdatePeriod_EmptyObservations_LeavesStateUnchanged()
    {
        var state = new Glicko2State
        {
            RatingFp1e9 = Glicko2.DefaultRatingFp1e9,
            RdFp1e9 = Glicko2.DefaultRdFp1e9,
            VolatilityFp1e9 = Glicko2.DefaultVolatilityFp1e9,
            LastObservedAtUnixNs = 0,
            ObservationCount = 0,
        };
        long origRating = state.RatingFp1e9;
        long origRd = state.RdFp1e9;
        long origVol = state.VolatilityFp1e9;

        Glicko2.UpdatePeriod(ref state,
            ReadOnlySpan<Glicko2Observation>.Empty,
            Glicko2.DefaultTauFp1e9, nowUnixNs: 0);

        Assert.Equal(origRating, state.RatingFp1e9);
        Assert.Equal(origRd, state.RdFp1e9);
        Assert.Equal(origVol, state.VolatilityFp1e9);
    }

    [Fact]
    public void UpdatePeriod_SingleWinShiftsRatingUp_LowersRd()
    {
        var state = new Glicko2State
        {
            RatingFp1e9 = Glicko2.DefaultRatingFp1e9,
            RdFp1e9 = Glicko2.DefaultRdFp1e9,
            VolatilityFp1e9 = Glicko2.DefaultVolatilityFp1e9,
            LastObservedAtUnixNs = 0,
            ObservationCount = 0,
        };
        var obs = new[] {
            new Glicko2Observation {
                OpponentRatingFp1e9 = Glicko2.DefaultRatingFp1e9,
                OpponentRdFp1e9     = Glicko2.DefaultRdFp1e9,
                ScoreFp1e9          = Glicko2.ScoreWin,
            }
        };

        Glicko2.UpdatePeriod(ref state, obs,
            Glicko2.DefaultTauFp1e9, nowUnixNs: 1_000_000_000L);

        Assert.True(state.RatingFp1e9 > Glicko2.DefaultRatingFp1e9,
            $"win should bump rating > prior; got {state.RatingFp1e9}");
        Assert.True(state.RdFp1e9 < Glicko2.DefaultRdFp1e9,
            $"win should tighten RD < prior; got {state.RdFp1e9}");
    }
}
