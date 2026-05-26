using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Centralised factory for <see cref="AttestationRow"/> construction.
/// Computes the 5-tuple content-addressed ID and applies the ADR 0044
/// kind-value-tier × source-trust-class prior matrix for initial Glicko-2
/// state. Every per-source decomposer MUST route attestation construction
/// through here — per ADR 0016 (no per-source reinvention).
/// </summary>
public static class AttestationFactory
{
    /// <summary>
    /// Create one <see cref="AttestationRow"/> with ID derived from the
    /// canonical 5-tuple <c>(subject, kind, object, source, context)</c> and
    /// initial Glicko-2 state from the ADR 0044 tier × trust matrix.
    /// </summary>
    public static AttestationRow Create(
        Hash128        subject,
        Hash128        kindId,
        Hash128?       obj,
        Hash128        sourceId,
        Hash128?       contextId,
        KindValueTier  tier,
        TrustClass     trust,
        long           observationCount = 1)
    {
        Span<byte> buf = stackalloc byte[16 * 5];
        subject.WriteBytes(buf.Slice(0, 16));
        kindId.WriteBytes(buf.Slice(16, 16));
        (obj ?? Hash128.Zero).WriteBytes(buf.Slice(32, 16));
        sourceId.WriteBytes(buf.Slice(48, 16));
        (contextId ?? Hash128.Zero).WriteBytes(buf.Slice(64, 16));
        var id = Hash128.Blake3(buf);

        var (mu, rd, vol) = TierPrior(tier);
        double trustWeight  = TrustWeight(trust);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

        return new AttestationRow(
            Id:                   id,
            SubjectId:            subject,
            KindId:               kindId,
            ObjectId:             obj,
            SourceId:             sourceId,
            ContextId:            contextId,
            RatingFp1e9:          (long)(mu  * trustWeight * 1_000_000_000.0),
            RdFp1e9:              (long)(rd  * 1_000_000_000.0),
            VolatilityFp1e9:      (long)(vol * 1_000_000_000.0),
            LastObservedAtUnixUs: nowUs,
            ObservationCount:     observationCount);
    }

    // ADR 0044 Table A: kind value tier priors (mu, RD, volatility)
    private static (double Mu, double Rd, double Vol) TierPrior(KindValueTier tier) => tier switch
    {
        KindValueTier.T1  => (2500, 30,  0.001),
        KindValueTier.T2  => (2300, 60,  0.005),
        KindValueTier.T3  => (1900, 150, 0.03),
        KindValueTier.T4  => (1800, 170, 0.04),
        KindValueTier.T5  => (1700, 200, 0.05),
        KindValueTier.T6  => (1600, 220, 0.05),
        KindValueTier.T7  => (1550, 240, 0.05),
        KindValueTier.T8  => (1500, 280, 0.06),
        KindValueTier.T9  => (1400, 300, 0.06),
        KindValueTier.T10 => (1500, 280, 0.06), // scalar-valued; use T8 defaults
        KindValueTier.T11 => (1300, 350, 0.06),
        _                 => (1500, 350, 0.06),
    };

    // ADR 0044 Table B: source trust class prior weights
    private static double TrustWeight(TrustClass trust) => trust switch
    {
        TrustClass.SubstrateMandateTier1        => 1.00,
        TrustClass.StandardsDerivedTier2        => 0.95,
        TrustClass.AcademicCuratedTier3         => 0.85,
        TrustClass.AcademicCuratedUserInputTier4 => 0.78,
        TrustClass.StructuredCorpusTier5        => 0.70,
        TrustClass.UserCuratedResourceTier6     => 0.60,
        TrustClass.AiModelProbeTier7            => 0.50,
        TrustClass.AppDerivedTier8              => 0.40,
        TrustClass.UserPromptTier9              => 0.30,
        TrustClass.AdversarialTier10            => 0.00,
        _                                       => 0.30,
    };
}

/// <summary>ADR 0044 attestation-kind value tiers T1–T11.</summary>
public enum KindValueTier
{
    T1  = 1,  // Substrate-asserted invariant
    T2  = 2,  // Standards-derived structural
    T3  = 3,  // Taxonomic
    T4  = 4,  // Partitive / compositional
    T5  = 5,  // Causal / implicational
    T6  = 6,  // Equivalence / translation
    T7  = 7,  // Oppositional / constraining
    T8  = 8,  // Associative / co-occurrence
    T9  = 9,  // Tensor-calculation (model-derived)
    T10 = 10, // Scalar-valued numeric attribute
    T11 = 11, // Probationary / user-supplied
}

/// <summary>ADR 0044 source trust class tiers TC1–TC10.</summary>
public enum TrustClass
{
    SubstrateMandateTier1         = 1,
    StandardsDerivedTier2         = 2,
    AcademicCuratedTier3          = 3,
    AcademicCuratedUserInputTier4 = 4,
    StructuredCorpusTier5         = 5,
    UserCuratedResourceTier6      = 6,
    AiModelProbeTier7             = 7,
    AppDerivedTier8               = 8,
    UserPromptTier9               = 9,
    AdversarialTier10             = 10,
}
