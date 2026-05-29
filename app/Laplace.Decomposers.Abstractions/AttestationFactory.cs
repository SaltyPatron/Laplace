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
            RatingFp1e9:          (long)(mu  * trustWeight * Glicko2.FpScale),
            RdFp1e9:              (long)(rd  * Glicko2.FpScale),
            VolatilityFp1e9:      (long)(vol * Glicko2.FpScale),
            LastObservedAtUnixUs: nowUs,
            ObservationCount:     observationCount);
    }

    /// <summary>
    /// Like <see cref="Create"/>, but seeds the Glicko-2 μ from an OBSERVED
    /// magnitude (|q·k| for a Q_PROJECTS edge, per-token L2 for a unary kind)
    /// instead of the flat tier prior — "how hard the strand tugs." The tier×trust
    /// prior sets the μ ceiling + RD/vol; the observation scales μ within the band.
    /// RD stays wide (one witness) so later corroboration (more heads / models)
    /// moves it via Glicko accumulation. strength = |mag| / (|mag| + floor) ∈ (0,1):
    /// monotonic in magnitude, 0.5 at the noise floor, →1 for strong observations;
    /// no global stats (streaming-safe), deterministic. Interim magnitude→μ mapping
    /// (ADR 0044/0036 open item #1) — calibrate per-kind once query results inform it.
    /// </summary>
    public static AttestationRow CreateWeighted(
        Hash128        subject,
        Hash128        kindId,
        Hash128?       obj,
        Hash128        sourceId,
        Hash128?       contextId,
        KindValueTier  tier,
        TrustClass     trust,
        double         magnitude,
        double         floor,
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
        double trustWeight = TrustWeight(trust);
        double m        = Math.Abs(magnitude);
        double strength = (floor > 0 && m > 0) ? m / (m + floor) : 0.0;
        double muSeed   = mu * strength;
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

        return new AttestationRow(
            Id:                   id,
            SubjectId:            subject,
            KindId:               kindId,
            ObjectId:             obj,
            SourceId:             sourceId,
            ContextId:            contextId,
            RatingFp1e9:          (long)(muSeed * trustWeight * Glicko2.FpScale),
            RdFp1e9:              (long)(rd  * Glicko2.FpScale),
            VolatilityFp1e9:      (long)(vol * Glicko2.FpScale),
            LastObservedAtUnixUs: nowUs,
            ObservationCount:     observationCount);
    }

    /// <summary>
    /// Content-addressed attestation ID — BLAKE3 of the canonical 5-tuple
    /// (subject, kind, object, source, context). Same hashing used internally
    /// by <see cref="Create"/>; exposed so per-instance paths (Glicko-2
    /// matchup-driven attestation builders) can compute IDs without going
    /// through the factory's prior-derivation arithmetic.
    /// </summary>
    public static Hash128 ComputeId(
        Hash128 subject, Hash128 kindId, Hash128? obj,
        Hash128 sourceId, Hash128? contextId)
    {
        Span<byte> buf = stackalloc byte[16 * 5];
        subject.WriteBytes(buf.Slice(0, 16));
        kindId.WriteBytes(buf.Slice(16, 16));
        (obj ?? Hash128.Zero).WriteBytes(buf.Slice(32, 16));
        sourceId.WriteBytes(buf.Slice(48, 16));
        (contextId ?? Hash128.Zero).WriteBytes(buf.Slice(64, 16));
        return Hash128.Blake3(buf);
    }

    /// <summary>
    /// ADR 0044 (Table A × Table B) Glicko-2 prior for a fresh attestation.
    /// Returns the state shape consumed by <see cref="Glicko2.UpdatePeriod"/>
    /// — per-instance evidence applies matchups against this prior. Cleanly
    /// separates "what's the kind-tier baseline" from "how do observations
    /// move it."
    /// </summary>
    public static Glicko2State Prior(KindValueTier tier, TrustClass trust)
    {
        var (mu, rd, vol) = TierPrior(tier);
        double trustWeight = TrustWeight(trust);
        return new Glicko2State
        {
            RatingFp1e9          = (long)(mu  * trustWeight * Glicko2.FpScale),
            RdFp1e9              = (long)(rd  * Glicko2.FpScale),
            VolatilityFp1e9      = (long)(vol * Glicko2.FpScale),
            LastObservedAtUnixNs = 0,
            ObservationCount     = 0,
        };
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
