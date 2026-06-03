using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// Smoke + algebra tests for the Glicko-2 C-bridge (Glicko2.cs + glicko2.h/.c).
/// Per CLAUDE.md "one source of math truth": C# never reimplements the
/// formulas; these tests verify the P/Invoke layer correctly delegates.
/// </summary>
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
        // Glickman's Glicko-1 paper defaults: mu=1500, RD=350, vol=0.06.
        Assert.Equal(1_500L * Glicko2.FpScale, Glicko2.DefaultRatingFp1e9);
        Assert.Equal(350L * Glicko2.FpScale,   Glicko2.DefaultRdFp1e9);
        Assert.Equal(60_000_000L,              Glicko2.DefaultVolatilityFp1e9);
    }

    [Fact]
    public void ScoreConstants_MatchGlicko2Convention()
    {
        // Glicko-2 matchup outcome: 0=loss, 0.5=draw, 1=win (scaled fp1e9).
        Assert.Equal(0L,                       Glicko2.ScoreLoss);
        Assert.Equal(500_000_000L,             Glicko2.ScoreDraw);
        Assert.Equal(1_000_000_000L,           Glicko2.ScoreWin);
    }

    [Fact]
    public void EffectiveMu_AtPrior_Is1500Minus2x350Equal800()
    {
 // effective_mu = rating - 2 * rd (95% lower bound).
        // At the default prior (1500, 350), effective_mu = 800.
        long effMu = Glicko2.EffectiveMuFp1e9(
            Glicko2.DefaultRatingFp1e9, Glicko2.DefaultRdFp1e9);
        Assert.Equal(800L * Glicko2.FpScale, effMu);
    }

    [Fact]
    public void EffectiveMu_HighRdGivesNegative()
    {
        // rating=1000, rd=600 → 1000 - 1200 = -200. The C primitive does
        // NOT clamp; the cascade's effective-score combiner applies any
        // policy clamps arena semantics. Verify the raw
        // value passes through.
        long effMu = Glicko2.EffectiveMuFp1e9(
            1_000L * Glicko2.FpScale, 600L * Glicko2.FpScale);
        Assert.Equal(-200L * Glicko2.FpScale, effMu);
    }

    [Fact]
    public void UpdatePeriod_EmptyObservations_LeavesStateUnchanged()
    {
        // Per glicko2.h: empty observation period is a no-op (the C primitive
        // returns early without touching state). C# wrapper short-circuits
        // on observations.IsEmpty so the P/Invoke isn't even called.
        var state = new Glicko2State
        {
            RatingFp1e9          = Glicko2.DefaultRatingFp1e9,
            RdFp1e9              = Glicko2.DefaultRdFp1e9,
            VolatilityFp1e9      = Glicko2.DefaultVolatilityFp1e9,
            LastObservedAtUnixNs = 0,
            ObservationCount     = 0,
        };
        long origRating = state.RatingFp1e9;
        long origRd     = state.RdFp1e9;
        long origVol    = state.VolatilityFp1e9;

        Glicko2.UpdatePeriod(ref state,
            ReadOnlySpan<Glicko2Observation>.Empty,
            Glicko2.DefaultTauFp1e9, nowUnixNs: 0);

        Assert.Equal(origRating, state.RatingFp1e9);
        Assert.Equal(origRd,     state.RdFp1e9);
        Assert.Equal(origVol,    state.VolatilityFp1e9);
    }

    [Fact]
    public void UpdatePeriod_SingleWinShiftsRatingUp_LowersRd()
    {
        // Sanity check: one decisive win against an equal-rated opponent
        // should bump rating upward + tighten RD. Numerical values come from
        // the C primitive — this test verifies direction, not magnitude.
        var state = new Glicko2State
        {
            RatingFp1e9          = Glicko2.DefaultRatingFp1e9,
            RdFp1e9              = Glicko2.DefaultRdFp1e9,
            VolatilityFp1e9      = Glicko2.DefaultVolatilityFp1e9,
            LastObservedAtUnixNs = 0,
            ObservationCount     = 0,
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
