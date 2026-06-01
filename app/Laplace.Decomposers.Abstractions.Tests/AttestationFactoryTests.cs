using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// AttestationFactory: ID hashing + the §10 signed-observation mechanism.
/// Every per-source decomposer routes through this factory (ADR 0016); these
/// tests fix the contract: signed magnitude → Glicko score, trust → opponent φ
/// (NOT a μ multiplier), no tiers in evidence.
/// </summary>
public class AttestationFactoryTests
{
    private static Hash128 H(string s) => Hash128.OfCanonical(s);

    [Fact]
    public void ComputeId_DeterministicOnSameTuple()
    {
        var subj = H("subject/v1");
        var kind = H("kind/v1");
        var obj  = H("object/v1");
        var src  = H("source/v1");
        var ctx  = H("context/v1");

        var id1 = AttestationFactory.ComputeId(subj, kind, obj, src, ctx);
        var id2 = AttestationFactory.ComputeId(subj, kind, obj, src, ctx);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ComputeId_ChangesOnAnyTupleSlot()
    {
        var baseId = AttestationFactory.ComputeId(
            H("a"), H("b"), H("c"), H("d"), H("e"));
        Assert.NotEqual(baseId, AttestationFactory.ComputeId(H("X"), H("b"), H("c"), H("d"), H("e")));
        Assert.NotEqual(baseId, AttestationFactory.ComputeId(H("a"), H("X"), H("c"), H("d"), H("e")));
        Assert.NotEqual(baseId, AttestationFactory.ComputeId(H("a"), H("b"), H("X"), H("d"), H("e")));
        Assert.NotEqual(baseId, AttestationFactory.ComputeId(H("a"), H("b"), H("c"), H("X"), H("e")));
        Assert.NotEqual(baseId, AttestationFactory.ComputeId(H("a"), H("b"), H("c"), H("d"), H("X")));
    }

    [Fact]
    public void ComputeId_NullObjAndNullCtx_TreatedAsZeroHash()
    {
        var withNulls = AttestationFactory.ComputeId(H("a"), H("b"), null, H("d"), null);
        var withZeros = AttestationFactory.ComputeId(H("a"), H("b"), Hash128.Zero, H("d"), Hash128.Zero);
        Assert.Equal(withZeros, withNulls);
    }

    [Fact]
    public void ComputeId_MatchesObservationRowId()
    {
        var subj = H("s"); var kind = H("k"); var obj = H("o");
        var src  = H("src"); var ctx = H("ctx");

        var standalone = AttestationFactory.ComputeId(subj, kind, obj, src, ctx);
        var fromObs = AttestationFactory.CreateObservation(
            subj, kind, obj, src, ctx, signedMagnitude: 0.5, arenaScale: 0.1, witnessWeight: 0.5).Id;
        Assert.Equal(standalone, fromObs);
    }

    // ── §10: signed magnitude → score = ½(1 + tanh(m/M)) ───────────────────

    [Fact]
    public void Score_PositiveMagnitude_AboveHalf()
        => Assert.True(AttestationFactory.Score(1.0, 1.0) > 0.5);

    [Fact]
    public void Score_NegativeMagnitude_BelowHalf()
        => Assert.True(AttestationFactory.Score(-1.0, 1.0) < 0.5);

    [Fact]
    public void Score_ZeroMagnitude_IsExactlyHalf()
        => Assert.Equal(0.5, AttestationFactory.Score(0.0, 1.0), 12);

    [Fact]
    public void Score_IsSymmetricAroundHalf()
    {
        // tanh is odd ⇒ s(+m) + s(−m) = 1: a refute is the mirror of a confirm.
        double up = AttestationFactory.Score(0.7, 0.3);
        double dn = AttestationFactory.Score(-0.7, 0.3);
        Assert.Equal(1.0, up + dn, 12);
    }

    [Fact]
    public void Score_StrongerMagnitude_HigherScore()
        => Assert.True(AttestationFactory.Score(2.0, 1.0) > AttestationFactory.Score(0.5, 1.0));

    [Fact]
    public void CreateObservation_PositiveMagnitude_ScoreAboveHalf()
    {
        var r = AttestationFactory.CreateObservation(
            H("s"), H("k"), H("o"), H("src"), null, signedMagnitude: 1.5, arenaScale: 1.0, witnessWeight: 0.5);
        Assert.True(r.ScoreFp1e9 > Glicko2.FpScale / 2);
    }

    [Fact]
    public void CreateObservation_NegativeMagnitude_ScoreBelowHalf()
    {
        // A refute/repel witness (negative coupling) must land below neutral — not abs'd to a win.
        var r = AttestationFactory.CreateObservation(
            H("s"), H("k"), H("o"), H("src"), null, signedMagnitude: -1.5, arenaScale: 1.0, witnessWeight: 0.5);
        Assert.True(r.ScoreFp1e9 < Glicko2.FpScale / 2);
    }

    // ── §10: trust → opponent φ (opponent_rd), NEVER the score ──────────────

    [Fact]
    public void WitnessPhi_TrustedTighterThanCrank()
        => Assert.True(AttestationFactory.WitnessPhi(1.0) < AttestationFactory.WitnessPhi(0.1));

    [Fact]
    public void Trust_ChangesOpponentRd_NotScore()
    {
        // Same magnitude + arena, different witness trust: identical SCORE,
        // different OPPONENT φ. Trust weights via g(φ), never by moving the outcome.
        var trusted = AttestationFactory.CreateObservation(
            H("s"), H("k"), H("o"), H("src"), null, signedMagnitude: 1.0, arenaScale: 1.0, witnessWeight: 1.0);
        var crank = AttestationFactory.CreateObservation(
            H("s"), H("k"), H("o"), H("src"), null, signedMagnitude: 1.0, arenaScale: 1.0, witnessWeight: 0.05);

        Assert.Equal(trusted.ScoreFp1e9, crank.ScoreFp1e9);          // outcome unchanged by trust
        Assert.True(trusted.OpponentRdFp1e9 < crank.OpponentRdFp1e9); // trusted → tighter opponent
    }

    [Fact]
    public void CreateCategorical_ConfirmIsWin_RefuteIsLoss()
    {
        var confirm = AttestationFactory.CreateCategorical(
            H("s"), H("k"), H("o"), H("src"), null, confirm: true, witnessWeight: 1.0);
        var refute = AttestationFactory.CreateCategorical(
            H("s"), H("k"), H("o2"), H("src"), null, confirm: false, witnessWeight: 1.0);

        Assert.Equal(Glicko2.FpScale, confirm.ScoreFp1e9);   // 1.0
        Assert.Equal(0L,              refute.ScoreFp1e9);     // 0.0
    }

    [Fact]
    public void Evidence_CarriesNoAccumulatedState()
    {
        // The evidence row is an observation: score/opponent_rd/arena_m only. No
        // rating/rd/volatility field exists to bake a tier prior into (truth #5).
        var r = AttestationFactory.CreateObservation(
            H("s"), H("k"), H("o"), H("src"), null, signedMagnitude: 1.0, arenaScale: 1.0, witnessWeight: 0.5);
        Assert.True(r.ScoreFp1e9 is >= 0 and <= Glicko2.FpScale);
        Assert.True(r.OpponentRdFp1e9 > 0);
    }
}
