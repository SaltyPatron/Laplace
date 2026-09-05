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

    /// <summary>
    /// Optional retained source-input lifetime and transaction-bound verifier.
    /// The ingest runner owns this lease after the change is emitted.
    /// </summary>
    public SubstrateApplyEnvelope? ApplyEnvelope { get; init; }
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

/// <summary>
/// A continuous score consumed by the canonical consensus fold in the same
/// transaction as its categorical receipt.  Scores are process-local only and
/// deliberately absent from COPY, evidence, and replay digests.  The receipt
/// id and calculation receipt make a retry identity-sensitive without turning
/// the score into durable witness data.
/// </summary>
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

    // Physical artifact identity produced by the source-native composer. This is
    // distinct from an execution resume fingerprint and is carried to the file journal.
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
    long ObservedAtUnixUs);

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
    // The opponent's RATING, the half that never existed (GH #1321). Defaulted so
    // every existing construction site still compiles. Zero is reserved for evidence
    // written by the broken pre-GH-1321 managed staging boundary and is repaired to the
    // witnessed opponent rating (or neutral when the source did not publish one).
    long OpponentRatingFp1e9 = 1_500_000_000_000,
    long? SumScoreFp1e9 = null,
    Mask256 HighwayMask = default,
    bool FoldReplayable = true);
