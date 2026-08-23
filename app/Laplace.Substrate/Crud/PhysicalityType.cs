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

    // An ORDERED structural encoding: a flat vertex list carrying sentinels and
    // (role, value) pairs rather than a content sequence. Distinct from Content for
    // exactly the reason Set is -- the partial indexes that read text trajectories are
    // all WHERE type = 1, and a structure whose vertices are head refs, deprels,
    // annotation keys and end markers is not a sequence anyone means to walk.
    //
    // Measured 2026-08-23, before this existed: 2,132,050 of 46,542,360 type=1
    // physicalities were UD parse structures, so generation.trajectory_continuations
    // returned annotation entities as continuations of words -- hot -> ud/misc-key/...
    // at weight 544, outranking hot -> water at 502, and New -> substrate/pos/X/v1 at
    // 1475 outranking New -> Zealand at 1336.
    ParseStructure = 8,

    // Sparse, ordinal-aligned chess source annotations. These are parallel sequences on
    // the PLAYING, not per-ply testimony rows and not part of move/position identity.
    ChessComment = 6,
    ChessAnnotation = 7,
}
