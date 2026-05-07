namespace Laplace.Pipeline.Abstractions;

using Laplace.Core.Abstractions;

/// <summary>
/// Per-pipeline record types submitted to <see cref="IBatchSink"/> via the
/// per-channel emission services. One file per record kind would also be
/// acceptable; collected here because they are intimately tied to the sink
/// surface.
/// </summary>
public record EntityRecord(
    AtomId Hash,
    short Tier,
    AtomId ContentKindHash,
    byte[]? Content,
    Point4D Centroid);

public record EntityChildRecord(
    AtomId ParentHash,
    int Ordinal,
    int RleCount,
    AtomId ChildHash);

public record EdgeRecord(
    AtomId EdgeTypeHash,
    AtomId Hash);

public record EdgeMemberRecord(
    AtomId EdgeTypeHash,
    AtomId EdgeHash,
    AtomId RoleHash,
    int RolePosition,
    AtomId ParticipantHash);

public record PhysicalityRecord(
    AtomId PhysicalityTypeHash,
    AtomId EntityHash,
    AtomId? SourceHash,
    Point4D[] Geometry);

public record SequenceRecord(
    AtomId DocumentHash,
    long LeafPosition,
    AtomId LeafAtomHash);

public record EntityProvenanceRecord(
    AtomId EntityHash,
    AtomId SourceHash);

public record EdgeProvenanceRecord(
    AtomId EdgeTypeHash,
    AtomId EdgeHash,
    AtomId SourceHash);
