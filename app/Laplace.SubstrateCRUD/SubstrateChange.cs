using System.Collections.Immutable;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Canonical unified intent type carrying entities + physicalities +
/// attestations + metadata from a per-source decomposer to the shared
/// SubstrateCRUD write surface — per ADR 0049.
///
/// Serializable for checkpoint / resume on multi-hour ingest runs.
/// FK-dependency ordering invariant: entities must precede physicalities
/// which must precede attestations (enforced at apply time, not at
/// construction). Race-tolerant via content-addressing + ON CONFLICT DO
/// NOTHING at the DDL level (per RULES R5).
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
/// the intent for the checkpoint journal + per-intent observability emission.
/// </summary>
public sealed record SubstrateChangeMetadata(
    Hash128         IntentId,
    Hash128         SourceId,
    string          SourceContentUnitName,
    DateTimeOffset  BuiltAt,
    Hash128?        ParentIntentId);

/// <summary>
/// One row of the <c>laplace.entities</c> table (identity layer per ADR 0039).
/// All fields populated by the decomposer.
/// </summary>
public sealed record EntityRow(
    Hash128  Id,
    byte     Tier,           // 0..255; written as smallint
    Hash128  TypeId,
    Hash128? FirstObservedBy);

/// <summary>
/// One row of the <c>laplace.physicalities</c> table (per-source per-kind
/// view per ADR 0039).
///
/// <para>
/// Trajectory is a mantissa-packed LINESTRINGZM per ADR 0012 when populated;
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

/// <summary>
/// One row of the <c>laplace.attestations</c> table (typed knowledge edge
/// with Glicko-2 current state per RULES R5 + ADR 0036 + ADR 0044).
///
/// <para>
/// Glicko-2 rating/rd/volatility are int64 fixed-point ×1e9 per the substrate
/// datatype standards. Initial values come from the kind-value-tier × source-
/// trust-class prior matrix per ADR 0044, not from a global default.
/// </para>
/// </summary>
public sealed record AttestationRow(
    Hash128  Id,
    Hash128  SubjectId,
    Hash128  KindId,
    Hash128? ObjectId,
    Hash128  SourceId,
    Hash128? ContextId,
    long     RatingFp1e9,
    long     RdFp1e9,
    long     VolatilityFp1e9,
    long     LastObservedAtUnixUs,
    long     ObservationCount);
