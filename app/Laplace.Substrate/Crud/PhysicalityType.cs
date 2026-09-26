namespace Laplace.SubstrateCRUD;

public enum PhysicalityType : short
{

    Content = 1,


    BuildingBlock = 2,


    Projection = 3,


    ProjectionOutput = 4,

    // An unordered set of member ids packed into a trajectory. Kept apart from Content so
    // the partial indexes over content trajectories (physicalities_constituents_gin,
    // physicalities_traj_first_id_btree, physicalities_traj_probe, all WHERE type = 1)
    // never read a vertex order that carries no sequence meaning. Members are sorted
    // ascending by id before packing, so equal sets share one Merkle id.
    Set = 5,

    // An ordered structural encoding: a flat vertex list of sentinels and (role, value)
    // pairs rather than a content sequence. Kept apart from Content for the same reason
    // as Set: its vertices (head refs, relation labels, annotation keys, end markers)
    // must not surface as continuations in trajectory reads over type = 1.
    ParseStructure = 8,

    // Exact canonical descriptor manifest with the native literal-identifier
    // retention projection. This does not assert selected Content geometry for
    // referenced entities; a selected view has its own recipe and receipt.
    DescriptorRetention = 9,

    // Closed contiguous interval over an ordered canonical domain. The trajectory carries
    // only [first,last]; membership is derived from the domain's admitted ordinal law rather
    // than expanded into one vertex/edge per member. Distinct from Content so range manifests
    // never enter text-continuation indexes.
    Range = 10,

    // Sparse, ordinal-aligned source annotations (comments, glyphs) carried as parallel
    // sequences on a game trajectory. They are not testimony rows and not part of the
    // identity of the moves or positions they annotate.
    ChessComment = 6,
    ChessAnnotation = 7,
}
