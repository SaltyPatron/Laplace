namespace Laplace.SubstrateCRUD;

public enum PhysicalityType : short
{

    Content = 1,


    BuildingBlock = 2,


    Projection = 3,


    ProjectionOutput = 4,

    // An UNORDERED set of member ids packed into a trajectory. Distinct from Content so the
    // three partial indexes that read text trajectories -- physicalities_constituents_gin,
    // physicalities_traj_first_id_btree, physicalities_traj_probe, all WHERE type = 1 -- are
    // not silently widened by a shape whose vertex order carries no sequence meaning.
    // Constituents are sorted ascending by id before packing, which is what makes the merkle
    // id of a set well-defined and therefore deduplicating (docs/specs/38).
    Set = 5,

    // Sparse, ordinal-aligned chess source annotations. These are parallel sequences on
    // the PLAYING, not per-ply testimony rows and not part of move/position identity.
    ChessComment = 6,
    ChessAnnotation = 7,
}
