using System.Collections.Immutable;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public sealed record SubstrateChange(
    ImmutableArray<EntityRow>       Entities,
    ImmutableArray<PhysicalityRow>  Physicalities,
    ImmutableArray<AttestationRow>  Attestations,
    SubstrateChangeMetadata         Metadata,
    ImmutableArray<IntentStage>     IntentStages = default,
    ImmutableArray<TestimonyWalkRow> TestimonyWalks = default);

/// <summary>
/// THE TRAJECTORY JOURNAL: a subject's thresholded table read at one
/// (plane, layer) is a WALK — vertices packed under the 212-bit trajectory law
/// (object reference, games in run_length, zigzagged fp1e9 score in flags;
/// Engine.Core TestimonyWalk.Pack). φ derives ONCE per witness weight. Walks
/// exist only on the consensus-only deposit path; the writer journals them to
/// walk staging and the terminal fold gathers per subject — no global sort,
/// ONE Glicko period per relation (the period rule).
/// </summary>
public sealed record TestimonyWalkRow(
    Hash128   Subject,
    Hash128   TypeId,
    Hash128?  ContextId,
    long      PhiFp1e9,
    byte[]    PackedVertices,
    int       Count,
    long      GamesTotal,
    long      ObservedAtUnixUs);

public sealed record SubstrateChangeMetadata(
    Hash128         IntentId,
    Hash128         SourceId,
    string          SourceContentUnitName,
    DateTimeOffset  BuiltAt,
    Hash128?        ParentIntentId,
    /// <summary>Input records consumed by this intent (sentences, synsets, codepoints, …). 0 = not reported.</summary>
    long            InputUnitsConsumed = 0,
    /// <summary>
    /// Commit barrier group. Ingest may parallelize commits within one epoch but never across epochs.
    /// Bump when later intents reference entities committed only in earlier phases.
    /// </summary>
    int             CommitEpoch = 0);

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
