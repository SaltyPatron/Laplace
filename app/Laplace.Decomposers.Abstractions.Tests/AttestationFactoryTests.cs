using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// AttestationFactory: ID hashing + Glicko-2 prior derivation (ADR 0044).
/// Per ADR 0016: every per-source decomposer routes through this factory;
/// these tests fix the contract.
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
        // Per the canonical 5-tuple encoding: null slot writes Hash128.Zero.
        var withNulls = AttestationFactory.ComputeId(
            H("a"), H("b"), null, H("d"), null);
        var withZeros = AttestationFactory.ComputeId(
            H("a"), H("b"), Hash128.Zero, H("d"), Hash128.Zero);
        Assert.Equal(withZeros, withNulls);
    }

    [Fact]
    public void ComputeId_MatchesCreateRowId()
    {
        // The factory's Create() and the exposed ComputeId() must produce the
        // same ID for the same canonical tuple (Create internally calls the
        // same hashing primitive).
        var subj = H("s"); var kind = H("k"); var obj = H("o");
        var src  = H("src"); var ctx = H("ctx");

        var standalone = AttestationFactory.ComputeId(subj, kind, obj, src, ctx);
        var fromCreate = AttestationFactory.Create(
            subj, kind, obj, src, ctx,
            KindValueTier.T9, TrustClass.AiModelProbeTier7).Id;
        Assert.Equal(standalone, fromCreate);
    }

    [Fact]
    public void Prior_T9_AiModelProbe_HasExpectedTier9TimesTrust7Rating()
    {
        // ADR 0044 T9 Tensor-Calculation prior: (mu=1400, RD=300, vol=0.06).
        // TrustClass.AiModelProbeTier7 weight: 0.50.
        // Expected rating = 1400 * 0.50 = 700, RD = 300, vol = 0.06.
        var prior = AttestationFactory.Prior(
            KindValueTier.T9, TrustClass.AiModelProbeTier7);

        Assert.Equal(700L * Glicko2.FpScale,  prior.RatingFp1e9);
        Assert.Equal(300L * Glicko2.FpScale,  prior.RdFp1e9);
        Assert.Equal(60_000_000L,             prior.VolatilityFp1e9);
        Assert.Equal(0L,                      prior.LastObservedAtUnixNs);
        Assert.Equal(0L,                      prior.ObservationCount);
    }

    [Fact]
    public void Prior_T1_SubstrateMandate_GetsFullTrustWeight()
    {
        // ADR 0044 T1 Mandate prior: (2500, 30, 0.001).
        // TrustClass.SubstrateMandateTier1 weight: 1.00.
        // Expected rating = 2500.
        var prior = AttestationFactory.Prior(
            KindValueTier.T1, TrustClass.SubstrateMandateTier1);
        Assert.Equal(2500L * Glicko2.FpScale, prior.RatingFp1e9);
        Assert.Equal(30L   * Glicko2.FpScale, prior.RdFp1e9);
    }

    [Fact]
    public void Prior_AdversarialTier10_ZerosTheRating()
    {
        // ADR 0044 TrustClass.AdversarialTier10 weight: 0.00 — effectively
        // excludes the source from any arena.
        var prior = AttestationFactory.Prior(
            KindValueTier.T9, TrustClass.AdversarialTier10);
        Assert.Equal(0L, prior.RatingFp1e9);
    }
}
