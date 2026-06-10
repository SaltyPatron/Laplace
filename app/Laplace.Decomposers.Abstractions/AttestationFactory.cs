using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class AttestationFactory
{
    private const double PhiTrusted = 30.0;
    private const double PhiCrank   = 350.0;

    public static double WitnessPhi(double weight)
    {
        double w = weight < 0.0 ? 0.0 : (weight > 1.0 ? 1.0 : weight);
        return PhiCrank + (PhiTrusted - PhiCrank) * w;
    }

    public static double Score(double signedMagnitude, double arenaScale)
        => arenaScale > 0.0 ? ScoreLaw.ScoreFp(signedMagnitude, arenaScale) / (double)ScoreLaw.FpScale : 0.5;

    public static AttestationRow CreateObservation(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double signedMagnitude, double arenaScale, double witnessWeight, long observationCount = 1)
        => Build(subject, typeId, obj, sourceId, contextId,
                 Score(signedMagnitude, arenaScale), witnessWeight, arenaScale, observationCount);

    public static AttestationRow CreateCategorical(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        bool confirm, double witnessWeight, long observationCount = 1)
        => Build(subject, typeId, obj, sourceId, contextId,
                 confirm ? 1.0 : 0.0, witnessWeight, 0.0, observationCount);

    public static AttestationRow CreateScored(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double score, double witnessWeight, double arenaScale = 0.0, long observationCount = 1)
        => Build(subject, typeId, obj, sourceId, contextId, score, witnessWeight, arenaScale, observationCount);

    public static AttestationRow Create(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double typeRank, double sourceTrust, long observationCount = 1)
        => CreateCategorical(subject, typeId, obj, sourceId, contextId,
                             confirm: true, witnessWeight: typeRank * sourceTrust,
                             observationCount: observationCount);

    public static AttestationRow CreateWeighted(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double typeRank, double sourceTrust, double magnitude, double arenaScale, long observationCount = 1)
        => CreateObservation(subject, typeId, obj, sourceId, contextId,
                             signedMagnitude: magnitude, arenaScale: arenaScale,
                             witnessWeight: typeRank * sourceTrust, observationCount: observationCount);

    public static AttestationRow CreateAggregated(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        long games, long sumScoreFp1e9, double witnessWeight)
    {
        if (games <= 0) throw new ArgumentOutOfRangeException(nameof(games));
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        const long half = 500_000_000L;
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
            ScoreFp1e9:           sumScoreFp1e9 / games,
            OpponentRdFp1e9:      (long)(WitnessPhi(witnessWeight) * Glicko2.FpScale),
            SumScoreFp1e9:        sumScoreFp1e9);
    }

    private static AttestationRow Build(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        double score, double witnessWeight, double arenaScale, long observationCount)
    {
        _ = arenaScale;
        double s = score < 0.0 ? 0.0 : (score > 1.0 ? 1.0 : score);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        long scoreFp = (long)(s * Glicko2.FpScale);
        const long half = 500_000_000L;
        return new AttestationRow(
            Id:                   ComputeId(subject, typeId, obj, sourceId, contextId),
            SubjectId:            subject,
            TypeId:               typeId,
            ObjectId:             obj,
            SourceId:             sourceId,
            ContextId:            contextId,
            Outcome:              scoreFp > half ? AttestationOutcome.Confirm
                                : scoreFp < half ? AttestationOutcome.Refute
                                                 : AttestationOutcome.Draw,
            LastObservedAtUnixUs: nowUs,
            ObservationCount:     observationCount,
            ScoreFp1e9:           scoreFp,
            OpponentRdFp1e9:      (long)(WitnessPhi(witnessWeight) * Glicko2.FpScale));
    }

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

public static class RelationTypeRank
{
    public const double Mandate             = 1.00;
    public const double StandardsStructural = 0.91;
    public const double Taxonomic           = 0.82;
    public const double Partitive           = 0.73;
    public const double Causal              = 0.64;
    public const double Equivalence         = 0.55;
    public const double Oppositional        = 0.45;
    public const double Associative         = 0.36;
    public const double TensorCalculation   = 0.27;
    public const double ScalarValued        = 0.18;
    public const double Probationary        = 0.09;
}

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
