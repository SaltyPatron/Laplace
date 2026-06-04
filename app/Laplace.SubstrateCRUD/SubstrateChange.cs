using System.Collections.Immutable;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Canonical unified intent type carrying entities + physicalities +
/// attestations + metadata from a per-source decomposer to the shared
/// SubstrateCRUD write surface.
///
/// FK-dependency ordering invariant: entities must precede physicalities
/// which must precede attestations (enforced at apply time, not at
/// construction). Race-tolerant via content-addressing + ON CONFLICT DO
/// NOTHING at the DDL level.
///
/// All IDs are computed client-side via <see cref="Hash128.Blake3"/> /
/// <see cref="Hash128.Merkle"/> in the decomposer; SubstrateCRUD never
/// computes hashes itself. Per-row timestamps (created_at, observed_at,
/// last_observed_at) are NOT in the intent — they're set by SubstrateCRUD
/// at insert time using the writer's logical clock.
/// </summary>
public sealed record SubstrateChange(
    ImmutableArray<EntityRow>       Entities,
    ImmutableArray<PhysicalityRow>  Physicalities,
    ImmutableArray<AttestationRow>  Attestations,
    SubstrateChangeMetadata         Metadata);

/// <summary>
/// Header that travels with every <see cref="SubstrateChange"/> — identifies
/// the intent for per-intent observability emission.
/// </summary>
public sealed record SubstrateChangeMetadata(
    Hash128         IntentId,
    Hash128         SourceId,
    string          SourceContentUnitName,
    DateTimeOffset  BuiltAt,
    Hash128?        ParentIntentId);

/// <summary>
/// One row of the <c>laplace.entities</c> table (identity layer).
/// All fields populated by the decomposer.
/// </summary>
public sealed record EntityRow(
    Hash128  Id,
    byte     Tier,           // 0..255; written as smallint
    Hash128  TypeId,
    Hash128? FirstObservedBy);

/// <summary>
/// One row of the <c>laplace.physicalities</c> table (per-source per-kind
/// view).
///
/// <para>
/// Trajectory is a mantissa-packed LINESTRINGZM when populated;
/// pass <c>null</c> + 0 vertices for T0 atoms or any case where no trajectory
/// applies.
/// </para>
/// </summary>
public sealed record PhysicalityRow(
    Hash128         Id,
    Hash128         EntityId,
    Hash128         SourceId,
    PhysicalityKind Kind,
    double          CoordX,
    double          CoordY,
    double          CoordZ,
    double          CoordM,
    Hilbert128      HilbertIndex,
    double[]?       TrajectoryXyzm,        // length = 4 * n_vertices; null/empty = SQL NULL
    int             NConstituents,
    double?         AlignmentResidual,
    int?            SourceDim,
    long            ObservedAtUnixUs);

/// <summary>The dissent record on an evidence row — a CLASS, never a magnitude.</summary>
public enum AttestationOutcome : short
{
    Refute  = 0,
    Draw    = 1,
    Confirm = 2,
}

/// <summary>
/// One row of the <c>laplace.attestations</c> table — the EVIDENCE layer.
/// EVIDENCE IS PROVENANCE: who witnessed which relation (source; model
/// layer/head in context), when, how many games, and whether it confirmed or
/// refuted (<see cref="Outcome"/> — a class, never a magnitude).
///
/// <para>
/// <c>ScoreFp1e9</c> / <c>OpponentRdFp1e9</c> are the witness's TESTIMONY IN
/// FLIGHT — the ½(1+tanh(signed_m/M)) match outcome and the trust→φ opponent
/// precision the consensus accumulation consumes AT INGEST. They are NEVER
/// persisted (a stored per-witness score is invertible to the weight —
/// recording raw weights). The persisted columns are exactly: id, subject,
/// kind, object, source, context, outcome, last_observed_at,
/// observation_count. The accumulated rating/rd/volatility live on the
/// consensus table — NOT here. No tiers, no trust classes in evidence.
/// </para>
/// </summary>
public sealed record AttestationRow(
    Hash128            Id,
    Hash128            SubjectId,
    Hash128            KindId,
    Hash128?           ObjectId,
    Hash128            SourceId,
    Hash128?           ContextId,
    AttestationOutcome Outcome,
    long               LastObservedAtUnixUs,
    long               ObservationCount,
    long               ScoreFp1e9,        // in-flight testimony — consumed at ingest, never persisted
    long               OpponentRdFp1e9);  // in-flight trust→φ   — consumed at ingest, never persisted
