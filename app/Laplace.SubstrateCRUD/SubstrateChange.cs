using System.Collections.Immutable;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public sealed record SubstrateChange(
    ImmutableArray<EntityRow>       Entities,
    ImmutableArray<PhysicalityRow>  Physicalities,
    ImmutableArray<AttestationRow>  Attestations,
    SubstrateChangeMetadata         Metadata);

public sealed record SubstrateChangeMetadata(
    Hash128         IntentId,
    Hash128         SourceId,
    string          SourceContentUnitName,
    DateTimeOffset  BuiltAt,
    Hash128?        ParentIntentId,
    /// <summary>Input records consumed by this intent (sentences, synsets, codepoints, …). 0 = not reported.</summary>
    long            InputUnitsConsumed = 0);

public sealed record EntityRow(
    Hash128  Id,
    byte     Tier,
    Hash128  TypeId,
    Hash128? FirstObservedBy);

public sealed record PhysicalityRow(
    Hash128         Id,
    Hash128         EntityId,
    Hash128         SourceId,
    PhysicalityType Type,
    double          CoordX,
    double          CoordY,
    double          CoordZ,
    double          CoordM,
    Hilbert128      HilbertIndex,
    double[]?       TrajectoryXyzm,
    int             NConstituents,
    double?         AlignmentResidual,
    int?            SourceDim,
    long            ObservedAtUnixUs);

public enum AttestationOutcome : short
{
    Refute  = 0,
    Draw    = 1,
    Confirm = 2,
}

public sealed record AttestationRow(
    Hash128            Id,
    Hash128            SubjectId,
    Hash128            TypeId,
    Hash128?           ObjectId,
    Hash128            SourceId,
    Hash128?           ContextId,
    AttestationOutcome Outcome,
    long               LastObservedAtUnixUs,
    long               ObservationCount,
    long               ScoreFp1e9,
    long               OpponentRdFp1e9,
    long?              SumScoreFp1e9 = null);
