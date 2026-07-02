using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// The Rule #8 client-side consensus fold is only valid if
/// Glicko2.FoldUniformPeriod (the analytic uniform-period fold the server's
/// consensus_fold_engine calls) is BIT-EQUAL to expanding the same partial
/// into per-observation form and running glicko2_update_period — the
/// equivalence the whole byte-identical-fold verification chain rests on.
/// Both paths run in the same native laplace_core library in int64 fixed
/// point, so equality here is exact, not approximate.
/// </summary>
public class Glicko2FoldParityTests
{
    private static Glicko2State FoldPath(
        long r0, long rd0, long vol0,
        long opponentRating, long phi, long games, long sumScore, long tau)
    {
        var st = Glicko2.Init(r0, rd0, vol0);
        Glicko2.FoldUniformPeriod(ref st, opponentRating, phi, games, sumScore, tau, 0);
        return st;
    }

    private static void AssertStatesEqual(Glicko2State expected, Glicko2State actual)
    {
        Assert.Equal(expected.RatingFp1e9, actual.RatingFp1e9);
        Assert.Equal(expected.RdFp1e9, actual.RdFp1e9);
        Assert.Equal(expected.VolatilityFp1e9, actual.VolatilityFp1e9);
        Assert.Equal(expected.ObservationCount, actual.ObservationCount);
    }

    [Theory]
    // games=1 (rem-only edge), neutral prior
    [InlineData(1, Glicko2.ScoreWin)]
    [InlineData(1, Glicko2.ScoreLoss)]
    [InlineData(1, Glicko2.ScoreDraw)]
    // small counts, mixed sums (non-divisible => q/rem split exercised)
    [InlineData(3, 2_500_000_000L)]
    [InlineData(7, 3_141_592_653L)]
    [InlineData(12, 11_000_000_001L)]
    // larger counts typical of a real working set
    [InlineData(500, 271_828_182_845L)]
    [InlineData(4096, 2_048_000_000_000L)]
    // beyond the stackalloc threshold in AccumulateGames
    [InlineData(5000, 1_234_567_890_123L)]
    public void FoldUniformPeriod_BitEqualsObservationExpansion_NeutralPrior(
        long games, long sumScore)
    {
        long neutral = Glicko2.NeutralMuFp1e9();
        long phi = Glicko2.DefaultRdFp1e9;

        var viaObservations = Glicko2.AccumulateGames(
            neutral, Glicko2.DefaultRdFp1e9, Glicko2.DefaultVolatilityFp1e9,
            neutral, phi, games, sumScore);

        var viaFold = FoldPath(
            neutral, Glicko2.DefaultRdFp1e9, Glicko2.DefaultVolatilityFp1e9,
            neutral, phi, games, sumScore, Glicko2.DefaultTauFp1e9);

        AssertStatesEqual(viaObservations, viaFold);
    }

    [Theory]
    // non-neutral priors (a previously-folded consensus row being updated)
    [InlineData(1_650_000_000_000L, 120_000_000_000L, 55_000_000L, 42, 30_000_000_000L)]
    [InlineData(1_320_500_000_000L, 310_000_000_000L, 61_000_000L, 5, 4_500_000_000L)]
    [InlineData(1_500_000_000_000L, 40_000_000_000L, 60_000_000L, 999, 700_123_456_789L)]
    // non-default opponent phi (the accumulator's PhiFp1e9 varies per relation)
    [InlineData(1_500_000_000_000L, 350_000_000_000L, 60_000_000L, 17, 9_000_000_000L,
        200_000_000_000L)]
    [InlineData(1_777_000_000_000L, 88_000_000_000L, 59_000_000L, 260, 130_000_000_000L,
        50_000_000_000L)]
    public void FoldUniformPeriod_BitEqualsObservationExpansion_SeededPrior(
        long r0, long rd0, long vol0, long games, long sumScore,
        long phi = Glicko2.DefaultRdFp1e9)
    {
        long neutral = Glicko2.NeutralMuFp1e9();

        var viaObservations = Glicko2.AccumulateGames(
            r0, rd0, vol0, neutral, phi, games, sumScore);

        var viaFold = FoldPath(r0, rd0, vol0, neutral, phi, games, sumScore,
            Glicko2.DefaultTauFp1e9);

        AssertStatesEqual(viaObservations, viaFold);
    }

    [Fact]
    public void NeutralMu_MatchesServerConstant()
    {
        // CONSENSUS_FOLD_NEUTRAL_MU in consensus_fold_math.h — the opponent
        // rating every server-side fold uses. The client must feed the fold
        // from the native export, never a managed literal; this pins the
        // export to the documented value so drift in either direction fails.
        Assert.Equal(1_500_000_000_000L, Glicko2.NeutralMuFp1e9());
        Assert.Equal(Glicko2.DefaultRatingFp1e9, Glicko2.NeutralMuFp1e9());
    }

    [Fact]
    public void Init_MatchesManualStateConstruction()
    {
        var st = Glicko2.Init(1_600_000_000_000L, 200_000_000_000L, 58_000_000L);
        Assert.Equal(1_600_000_000_000L, st.RatingFp1e9);
        Assert.Equal(200_000_000_000L, st.RdFp1e9);
        Assert.Equal(58_000_000L, st.VolatilityFp1e9);
        Assert.Equal(0L, st.ObservationCount);
    }
}
