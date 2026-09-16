using System.Collections.Immutable;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public sealed record SubstrateChange(
    ImmutableArray<EntityRow> Entities,
    ImmutableArray<PhysicalityRow> Physicalities,
    ImmutableArray<AttestationRow> Attestations,
    SubstrateChangeMetadata Metadata,
    ImmutableArray<IntentStage> IntentStages = default,
    ImmutableArray<TestimonyWalkRow> TestimonyWalks = default,
    ImmutableArray<string> CanonicalNames = default,
    ImmutableArray<EphemeralFoldInput> EphemeralFoldInputs = default)
{
    public bool CountsAsUnit { get; init; } = true;
    public SubstrateApplyEnvelope? ApplyEnvelope { get; init; }
    /// <summary>
    /// Source physicality bodies before current-placement selection. The native
    /// descriptor owner identifies exact forms and retains their observation scope.
    /// Default means a caller supplied only <see cref="Physicalities"/>.
    /// </summary>
    public ImmutableArray<PhysicalityRow> PhysicalityObservations { get; init; }
    /// <summary>Explicit priors for the source owners of this unit's raw physicality
    /// observations. This is producer metadata, not a trust inferred from entity
    /// identity, relation weight, or the first selected placement.</summary>
    public ImmutableDictionary<Hash128, double> PhysicalitySourcePriors { get; init; } =
        ImmutableDictionary<Hash128, double>.Empty;

    public SubstrateChange WithSourcePrior(Hash128 sourceId, double sourceTrust)
    {
        ValidateSourcePrior(sourceTrust);
        if (PhysicalitySourcePriors.TryGetValue(sourceId, out double prior))
        {
            if (BitConverter.DoubleToInt64Bits(prior) != BitConverter.DoubleToInt64Bits(sourceTrust))
                throw new InvalidOperationException($"source {sourceId} has conflicting priors in one physicality observation unit");
            return this;
        }
        return this with { PhysicalitySourcePriors = PhysicalitySourcePriors.Add(sourceId, sourceTrust) };
    }

    public double RequireSourcePrior(Hash128 sourceId)
    {
        if (!PhysicalitySourcePriors.TryGetValue(sourceId, out double prior))
            throw new InvalidOperationException($"physicality source {sourceId} has no declared prior in intent {Metadata.IntentId}");
        ValidateSourcePrior(prior);
        return prior;
    }

    internal static void ValidateSourcePrior(double sourceTrust)
    {
        if (!double.IsFinite(sourceTrust) || sourceTrust < 0 || sourceTrust > 1)
            throw new ArgumentOutOfRangeException(nameof(sourceTrust), "source prior must be finite and in [0,1]");
    }
}

public sealed record TestimonyWalkRow(
    Hash128 Subject,
    Hash128 TypeId,
    Hash128? ContextId,
    long PhiFp1e9,
    byte[] PackedVertices,
    int Count,
    long GamesTotal,
    long ObservedAtUnixUs);

public sealed record EphemeralFoldInput(
    Hash128 AttestationId,
    Hash128 CalculationReceiptId,
    long ScoreFp1e9);

public sealed record SubstrateChangeMetadata(
    Hash128 IntentId,
    Hash128 SourceId,
    string SourceContentUnitName,
    DateTimeOffset BuiltAt,
    Hash128? ParentIntentId,
    long InputUnitsConsumed = 0,
    int CommitEpoch = 0,
    Hash128? FileId = null);

public sealed record EntityRow(
    Hash128 Id,
    byte Tier,
    Hash128 TypeId,
    Hash128? FirstObservedBy);

public sealed record PhysicalityRow(
    Hash128 Id,
    Hash128 EntityId,
    Hash128 SourceId,
    PhysicalityType Type,
    double CoordX,
    double CoordY,
    double CoordZ,
    double CoordM,
    Hilbert128 HilbertIndex,
    double[]? TrajectoryXyzm,
    int NConstituents,
    double? AlignmentResidual,
    int? SourceDim,
    long ObservedAtUnixUs)
{
    private readonly bool _identityValidated = ValidateIdentity(
        Id, EntityId, Type, TrajectoryXyzm, NConstituents);

    private static bool ValidateIdentity(
        Hash128 id, Hash128 entityId, PhysicalityType type,
        double[]? trajectoryXyzm, int nConstituents)
    {
        if (nConstituents < 0)
            throw new InvalidOperationException("physicality constituent count cannot be negative");

        Hash128 expectedPhysicalityId = PhysicalityId.Compute(entityId, type);
        if (id != expectedPhysicalityId)
            throw new InvalidOperationException(
                $"physicality identity mismatch: entity={entityId} type={(short)type} "
                + $"declared={id} recomputed={expectedPhysicalityId}");

        if (trajectoryXyzm is null || trajectoryXyzm.Length == 0)
        {
            if (nConstituents != 0)
                throw new InvalidOperationException(
                    $"physicality declares {nConstituents} constituents without a trajectory");
            return true;
        }
        if (trajectoryXyzm.Length % 4 != 0)
            throw new InvalidOperationException("physicality trajectory is not an XYZM vertex sequence");

        // Every trajectory type owes an exact logical constituent count. Only Content
        // additionally owns the identity of that ordered manifest.
        Hash128 manifestId = Trajectory.ContentIdentity(trajectoryXyzm, out int logicalCount);
        if (logicalCount != nConstituents)
            throw new InvalidOperationException(
                $"physicality trajectory count mismatch: entity={entityId} type={(short)type} "
                + $"declared={nConstituents} decoded={logicalCount}");

        if (type == PhysicalityType.Content && manifestId != entityId)
            throw new InvalidOperationException(
                $"content trajectory identity mismatch: entity={entityId} "
                + $"recomputed={manifestId} constituents={logicalCount}");
        return true;
    }
}

public enum AttestationOutcome : short
{
    Refute = 0,
    Draw = 1,
    Confirm = 2,
}

public sealed record AttestationRow(
    Hash128 Id,
    Hash128 SubjectId,
    Hash128 TypeId,
    Hash128? ObjectId,
    Hash128 SourceId,
    Hash128? ContextId,
    AttestationOutcome Outcome,
    long LastObservedAtUnixUs,
    long ObservationCount,
    long ScoreFp1e9,
    long OpponentRdFp1e9,
    long OpponentRatingFp1e9 = 1_500_000_000_000,
    long? SumScoreFp1e9 = null,
    Mask256 HighwayMask = default,
    bool FoldReplayable = true);
