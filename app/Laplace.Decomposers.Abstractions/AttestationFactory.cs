using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Centralised factory for <see cref="AttestationRow"/> (the EVIDENCE layer).
/// Every per-source decomposer routes attestation construction through here
/// (ADR 0016 — no per-source reinvention).
///
/// <para>
/// An attestation is ONE Glicko-2 OBSERVATION — a match the relation (the
/// player) plays against a neutral baseline opponent (ARCHITECTURE.md §10),
/// DERIVED, not tuned:
/// <list type="bullet">
///   <item><c>score   = ½(1 + tanh(signed_m / M))</c> ∈ (0,1) — the SIGNED
///   magnitude's outcome: + → win (confirm/attract), − → loss (refute/repel),
///   0 → ½ (draw). Sign is preserved; dissent is a weighted loss, never abs'd away.</item>
///   <item><c>opponent_rd = φ(weight)</c> — the witness weight
///   (kind_rank × source_trust × tenant_trust) mapped to opponent precision.
///   Trusted → low φ (g(φ)≈1, full weight); crank → high φ (g(φ)≈0, discounted).
///   Glicko's own g(φ) does the weighting — never a μ multiplier.</item>
///   <item><c>arena_m = M</c> — the per-arena magnitude scale used (audit).</item>
/// </list>
/// The accumulated rating/rd/volatility live on the consensus table, NOT here.
/// There are no tiers and no trust classes in evidence — kind significance and
/// source trust enter ONLY as the numeric witness weight folded into φ.
/// </para>
/// </summary>
public static class AttestationFactory
{
    // ── The trust→φ shape — the ONE legitimate calibration besides M and φ₀
    //    (ARCHITECTURE.md §10). weight ∈ [0,1]: trusted (→1) ⇒ tight opponent
    //    (low φ ⇒ g(φ)≈1, full weight); crank (→0) ⇒ wide opponent (high φ ⇒
    //    g(φ)≈0, discounted). One trusted proof out-votes N cranks natively.
    private const double PhiTrusted = 30.0;    // weight = 1 → precise opponent
    private const double PhiCrank   = 350.0;   // weight = 0 → maximally imprecise

    /// <summary>Witness weight ∈ [0,1] → opponent rating deviation φ (Glicko-1 scale).</summary>
    public static double WitnessPhi(double weight)
    {
        double w = weight < 0.0 ? 0.0 : (weight > 1.0 ? 1.0 : weight);
        return PhiCrank + (PhiTrusted - PhiCrank) * w;   // w=1 → 30, w=0 → 350
    }

    /// <summary>Glicko-2 match outcome from a SIGNED magnitude and a per-arena
    /// scale M &gt; 0: <c>s = ½(1 + tanh(m/M)) ∈ (0,1)</c>. + → win, − → loss,
    /// 0 → ½. M is the measured arena scale, never a hand-set knob; M ≤ 0 ⇒
    /// no scale ⇒ neutral draw.</summary>
    public static double Score(double signedMagnitude, double arenaScale)
        => arenaScale > 0.0 ? 0.5 * (1.0 + Math.Tanh(signedMagnitude / arenaScale)) : 0.5;

    /// <summary>One witness OBSERVATION from a SIGNED magnitude + measured arena
    /// scale M + witness weight. Score via tanh, opponent φ via the trust shape.</summary>
    public static AttestationRow CreateObservation(
        Hash128 subject, Hash128 kindId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double signedMagnitude, double arenaScale, double witnessWeight, long observationCount = 1)
        => Build(subject, kindId, obj, sourceId, contextId,
                 Score(signedMagnitude, arenaScale), witnessWeight, arenaScale, observationCount);

    /// <summary>One CATEGORICAL observation: a confirm is a win (score=1), a
    /// refute is a loss (score=0). For structural / lexical assertions (is_a,
    /// synonym = confirm; antonym = refute). No magnitude arena (arena_m = 0).</summary>
    public static AttestationRow CreateCategorical(
        Hash128 subject, Hash128 kindId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        bool confirm, double witnessWeight, long observationCount = 1)
        => Build(subject, kindId, obj, sourceId, contextId,
                 confirm ? 1.0 : 0.0, witnessWeight, 0.0, observationCount);

    /// <summary>Observation from an already-computed score ∈ [0,1].</summary>
    public static AttestationRow CreateScored(
        Hash128 subject, Hash128 kindId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double score, double witnessWeight, double arenaScale = 0.0, long observationCount = 1)
        => Build(subject, kindId, obj, sourceId, contextId, score, witnessWeight, arenaScale, observationCount);

    // ── Compatibility shims for the seed decomposers (categorical + weighted).
    //    Kind significance + source trust still arrive as the (KindValueTier,
    //    TrustClass) pair, but they are now PURELY a numeric lookup into
    //    kind_rank × source_trust → witness weight → opponent φ. NO tier prior
    //    on μ, NO tier/trust stored in the evidence. (The enum NAMES are a
    //    pending rename to KindRank/SourceTrust; the behaviour is already the
    //    rank/trust the law specifies.)

    /// <summary>Categorical confirm from a kind-rank × source-trust pair.</summary>
    public static AttestationRow Create(
        Hash128 subject, Hash128 kindId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        KindValueTier tier, TrustClass trust, long observationCount = 1)
        => CreateCategorical(subject, kindId, obj, sourceId, contextId,
                             confirm: true, witnessWeight: Weight(tier, trust),
                             observationCount: observationCount);

    /// <summary>Magnitude-weighted observation from a kind-rank × source-trust
    /// pair. The magnitude is the arena's SIGNED strength; <paramref name="floor"/>
    /// is the per-arena scale M (tanh(m/M)).</summary>
    public static AttestationRow CreateWeighted(
        Hash128 subject, Hash128 kindId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        KindValueTier tier, TrustClass trust, double magnitude, double floor, long observationCount = 1)
        => CreateObservation(subject, kindId, obj, sourceId, contextId,
                             signedMagnitude: magnitude, arenaScale: floor,
                             witnessWeight: Weight(tier, trust), observationCount: observationCount);

    /// <summary>Witness weight = kind_rank × source_trust (× tenant_trust = 1 until S5).</summary>
    public static double Weight(KindValueTier tier, TrustClass trust)
        => KindRank(tier) * SourceTrust(trust);

    private static AttestationRow Build(
        Hash128 subject, Hash128 kindId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double score, double witnessWeight, double arenaScale, long observationCount)
    {
        double s = score < 0.0 ? 0.0 : (score > 1.0 ? 1.0 : score);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        return new AttestationRow(
            Id:                   ComputeId(subject, kindId, obj, sourceId, contextId),
            SubjectId:            subject,
            KindId:               kindId,
            ObjectId:             obj,
            SourceId:             sourceId,
            ContextId:            contextId,
            ScoreFp1e9:           (long)(s * Glicko2.FpScale),
            OpponentRdFp1e9:      (long)(WitnessPhi(witnessWeight) * Glicko2.FpScale),
            ArenaMFp1e9:          (long)(Math.Max(0.0, arenaScale) * Glicko2.FpScale),
            LastObservedAtUnixUs: nowUs,
            ObservationCount:     observationCount);
    }

    /// <summary>
    /// Content-addressed attestation ID — BLAKE3 of the canonical 5-tuple
    /// (subject, kind, object, source, context).
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

    // ── Numeric kind RANK ∈ (0,1] — significance scale (is_a ≫ acronym_of),
    //    NOT a tier ladder, NOT a μ prior. Derived from the kind's significance
    //    ordinal: rank = (12 − ordinal) / 11, so the most significant kind = 1.0.
    private static double KindRank(KindValueTier tier) => (12.0 - (int)tier) / 11.0;

    // ── Numeric source TRUST ∈ [0,1] — a Glicko input, never a class/tier.
    private static double SourceTrust(TrustClass trust) => trust switch
    {
        TrustClass.SubstrateMandateTier1         => 1.00,
        TrustClass.StandardsDerivedTier2         => 0.95,
        TrustClass.AcademicCuratedTier3          => 0.85,
        TrustClass.AcademicCuratedUserInputTier4 => 0.78,
        TrustClass.StructuredCorpusTier5         => 0.70,
        TrustClass.UserCuratedResourceTier6      => 0.60,
        TrustClass.AiModelProbeTier7             => 0.50,
        TrustClass.AppDerivedTier8               => 0.40,
        TrustClass.UserPromptTier9               => 0.30,
        TrustClass.AdversarialTier10             => 0.00,
        _                                        => 0.30,
    };
}

/// <summary>
/// Kind significance ordinal — feeds the numeric kind_rank ∈ (0,1] used to
/// weight a witness (NOT a tier on the kind, NOT a μ prior, NOT an entity).
/// Pending rename to a KindRank significance scale; the ordinal is the rank.
/// </summary>
public enum KindValueTier
{
    T1  = 1,  // Substrate-asserted invariant     (rank 1.00)
    T2  = 2,  // Standards-derived structural
    T3  = 3,  // Taxonomic
    T4  = 4,  // Partitive / compositional
    T5  = 5,  // Causal / implicational
    T6  = 6,  // Equivalence / translation
    T7  = 7,  // Oppositional / constraining
    T8  = 8,  // Associative / co-occurrence
    T9  = 9,  // Tensor-calculation (model-derived)
    T10 = 10, // Scalar-valued numeric attribute
    T11 = 11, // Probationary / user-supplied      (rank 0.09)
}

/// <summary>
/// Source trust ordinal — feeds the numeric source_trust ∈ [0,1] used to weight
/// a witness (a Glicko input, NEVER a class/tier ladder on trust). Pending
/// rename to a SourceTrust scale.
/// </summary>
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
