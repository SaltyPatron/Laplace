using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Centralised factory for <see cref="AttestationRow"/> (the EVIDENCE layer).
/// Every per-source decomposer routes attestation construction through here
/// (no per-source reinvention).
///
/// <para>
/// An attestation is ONE Glicko-2 OBSERVATION — a match the relation (the
/// player) plays against a neutral baseline opponent (ARCHITECTURE.md §10),
/// DERIVED, not tuned:
/// <list type="bullet">
///   <item><c>score   = ½(1 + tanh(signed_m / M))</c> ∈ (0,1) — the SIGNED
///   magnitude's outcome (+ win/confirm, − loss/refute, 0 draw).</item>
///   <item><c>opponent_rd = φ(weight)</c> — the witness weight
///   (kind_rank × source_trust × tenant_trust) mapped to opponent precision φ.
///   Glicko's own g(φ) does the weighting; never a μ multiplier.</item>
///   <item><c>arena_m = M</c> — the per-arena magnitude scale used (audit).</item>
/// </list>
/// The accumulated rating/rd/volatility live on the consensus table, NOT here.
/// No tiers and no trust classes in evidence — kind significance and source
/// trust enter ONLY as the numeric witness weight folded into opponent φ
/// (see <see cref="KindRank"/> / <see cref="SourceTrust"/>).
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
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double signedMagnitude, double arenaScale, double witnessWeight, long observationCount = 1)
        => Build(subject, typeId, obj, sourceId, contextId,
                 Score(signedMagnitude, arenaScale), witnessWeight, arenaScale, observationCount);

    /// <summary>One CATEGORICAL observation: a confirm is a win (score=1), a
    /// refute is a loss (score=0). For structural / lexical assertions (is_a,
    /// synonym = confirm; antonym = refute). No magnitude arena (arena_m = 0).</summary>
    public static AttestationRow CreateCategorical(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        bool confirm, double witnessWeight, long observationCount = 1)
        => Build(subject, typeId, obj, sourceId, contextId,
                 confirm ? 1.0 : 0.0, witnessWeight, 0.0, observationCount);

    /// <summary>Observation from an already-computed score ∈ [0,1].</summary>
    public static AttestationRow CreateScored(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double score, double witnessWeight, double arenaScale = 0.0, long observationCount = 1)
        => Build(subject, typeId, obj, sourceId, contextId, score, witnessWeight, arenaScale, observationCount);

    // ── Categorical / magnitude-weighted builders for the seed decomposers.
    //    Significance is a numeric kind_rank (KindRank) × source_trust (SourceTrust),
    //    folded into the Glicko opponent φ — no tier on the kind, no trust class, no
    //    μ prior, nothing tier/trust stored in the evidence (truth #5).

    /// <summary>Categorical confirm weighted by a kind-rank × source-trust pair
    /// (see <see cref="KindRank"/> / <see cref="SourceTrust"/>).</summary>
    public static AttestationRow Create(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double kindRank, double sourceTrust, long observationCount = 1)
        => CreateCategorical(subject, typeId, obj, sourceId, contextId,
                             confirm: true, witnessWeight: kindRank * sourceTrust,
                             observationCount: observationCount);

    /// <summary>Magnitude-weighted observation. The magnitude is the arena's SIGNED
    /// strength; <paramref name="arenaScale"/> is the per-arena scale M (tanh(m/M))
    /// — a scale, never a value-dropping floor; the witness weight is
    /// kind_rank × source_trust (× tenant_trust = 1 until S5).</summary>
    public static AttestationRow CreateWeighted(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double kindRank, double sourceTrust, double magnitude, double arenaScale, long observationCount = 1)
        => CreateObservation(subject, typeId, obj, sourceId, contextId,
                             signedMagnitude: magnitude, arenaScale: arenaScale,
                             witnessWeight: kindRank * sourceTrust, observationCount: observationCount);

    /// <summary>
    /// One PRE-AGGREGATED evidence row: <paramref name="games"/> observations of
    /// the SAME relation by the SAME source whose individual fixed-point scores
    /// summed EXACTLY to <paramref name="sumScoreFp1e9"/> — positions of a
    /// logical table (model layers, norm slots) aggregating on ONE row
    /// (relation identity and evidence identity EXCLUDE position; per-position
    /// attribution is recipe content). All integer math — no double round-trip,
    /// the consensus accumulation consumes the exact (n, Σs). Outcome = the NET
    /// class: Σs vs n·½ exactly.
    /// </summary>
    public static AttestationRow CreateAggregated(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        long games, long sumScoreFp1e9, double witnessWeight)
    {
        if (games <= 0) throw new ArgumentOutOfRangeException(nameof(games));
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        const long half = 500_000_000L;          // ½ in fixed-point ×1e9
        long netHalf = checked(games * half);
        return new AttestationRow(
            Id:                   ComputeId(subject, typeId, obj, sourceId, contextId),
            SubjectId:            subject,
            TypeId:               typeId,
            ObjectId:             obj,
            SourceId:             sourceId,
            ContextId:            contextId,
            Outcome:              sumScoreFp1e9 > netHalf ? AttestationOutcome.Confirm
                                : sumScoreFp1e9 < netHalf ? AttestationOutcome.Refute
                                                          : AttestationOutcome.Draw,
            LastObservedAtUnixUs: nowUs,
            ObservationCount:     games,
            ScoreFp1e9:           sumScoreFp1e9 / games,   // display/fallback; the exact sum rides below
            OpponentRdFp1e9:      (long)(WitnessPhi(witnessWeight) * Glicko2.FpScale),
            SumScoreFp1e9:        sumScoreFp1e9);
    }

    private static AttestationRow Build(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double score, double witnessWeight, double arenaScale, long observationCount)
    {
        _ = arenaScale;   // consumed into the score upstream; never persisted (values are testimony)
        double s = score < 0.0 ? 0.0 : (score > 1.0 ? 1.0 : score);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        long scoreFp = (long)(s * Glicko2.FpScale);
        const long half = 500_000_000L;   // ½ in fixed-point ×1e9
        return new AttestationRow(
            Id:                   ComputeId(subject, typeId, obj, sourceId, contextId),
            SubjectId:            subject,
            TypeId:               typeId,
            ObjectId:             obj,
            SourceId:             sourceId,
            ContextId:            contextId,
            // The persisted dissent record — a CLASS from the testimony's
            // direction, never its magnitude (sign decides; magnitude consumed).
            Outcome:              scoreFp > half ? AttestationOutcome.Confirm
                                : scoreFp < half ? AttestationOutcome.Refute
                                                 : AttestationOutcome.Draw,
            LastObservedAtUnixUs: nowUs,
            ObservationCount:     observationCount,
            // In-flight testimony — consumed by the consensus accumulation at
            // ingest; the writer's COPY layout never persists these.
            ScoreFp1e9:           scoreFp,
            OpponentRdFp1e9:      (long)(WitnessPhi(witnessWeight) * Glicko2.FpScale));
    }

    /// <summary>
    /// Content-addressed attestation ID — BLAKE3 of the canonical 5-tuple
    /// (subject, type, object, source, context).
    /// </summary>
    public static Hash128 ComputeId(
        Hash128 subject, Hash128 typeId, Hash128? obj,
        Hash128 sourceId, Hash128? contextId)
    {
        Span<byte> buf = stackalloc byte[16 * 5];
        subject.WriteBytes(buf.Slice(0, 16));
        typeId.WriteBytes(buf.Slice(16, 16));
        (obj ?? Hash128.Zero).WriteBytes(buf.Slice(32, 16));
        sourceId.WriteBytes(buf.Slice(48, 16));
        (contextId ?? Hash128.Zero).WriteBytes(buf.Slice(64, 16));
        return Hash128.Blake3(buf);
    }
}

/// <summary>
/// Kind significance RANK ∈ (0,1] — a real significance scale (is_a ≫ acronym_of;
/// synonym ≠ hyponym ≠ meronym ≠ antonym), NOT a tier ladder, NOT a μ prior, NOT an
/// entity. Feeds the witness weight = kind_rank × source_trust × tenant_trust →
/// Glicko opponent φ. (Replaces the corrupt KindValueTier tier-on-kinds.)
/// </summary>
public static class KindRank
{
    public const double Mandate             = 1.00; // substrate-asserted invariant
    public const double StandardsStructural = 0.91; // standards-derived structural
    public const double Taxonomic           = 0.82; // is_a / hypernymy / instance
    public const double Partitive           = 0.73; // part / member / substance
    public const double Causal              = 0.64; // causal / implicational
    public const double Equivalence         = 0.55; // synonymy / translation
    public const double Oppositional        = 0.45; // antonymy / constraint
    public const double Associative         = 0.36; // co-occurrence / relatedness
    public const double TensorCalculation   = 0.27; // model-derived circuit
    public const double ScalarValued        = 0.18; // numeric attribute
    public const double Probationary        = 0.09; // user-supplied / unvetted
}

/// <summary>
/// Source TRUST ∈ [0,1] — a Glicko input, NEVER a class/tier ladder on trust.
/// Feeds the witness weight = kind_rank × source_trust × tenant_trust → opponent φ.
/// (Replaces the corrupt TrustClass tier ladder.)
/// </summary>
public static class SourceTrust
{
    public const double SubstrateMandate         = 1.00;
    public const double StandardsDerived         = 0.95;
    public const double AcademicCurated          = 0.85;
    public const double AcademicCuratedUserInput = 0.78;
    public const double StructuredCorpus         = 0.70;
    public const double UserCuratedResource      = 0.60;
    public const double AiModelProbe             = 0.50;
    public const double AppDerived               = 0.40;
    public const double UserPrompt               = 0.30;
    public const double Adversarial              = 0.00;
}
