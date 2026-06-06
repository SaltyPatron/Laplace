using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Composes an n-gram **trajectory entity** — a path of constituent entities
/// (`[the, capital, of]`) made into ONE content-addressed entity, a tier above its
/// constituents, exactly as a word is one entity above its graphemes.
///
/// This is the same composition <c>hash_composer</c> performs for text tiers
/// (engine/core/include/laplace/core/hash_composer.h), applied to an arbitrary ordered
/// sequence of already-content-addressed entities (tokens, or lower-tier n-grams):
/// <list type="bullet">
///   <item><c>id      = Hash128.Merkle(tier, [child id …])</c></item>
///   <item><c>coord   = Math4d.Centroid([child coord …])</c></item>
///   <item><c>hilbert = Hilbert128.Encode(coord)</c></item>
///   <item>the Content physicality stores the ordered constituents as the trajectory
/// (<c>Trajectory.Build</c>,) — the path itself, not just the centroid.</item>
/// </list>
///
/// Because the id is the Merkle of the constituents, the SAME n-gram minted by any source
/// — this model, another model, a corpus sentence — is ONE entity; its onward completion
/// attestations accumulate into consensus. Tokens, n-grams, and the completions between
/// them are all the same kind of record at different tiers; this is the building block.
/// </summary>
public static class NgramTrajectory
{
    /// <summary>One constituent of an n-gram: its content-addressed id and S³ coord.</summary>
    public readonly record struct Constituent(Hash128 Id, double X, double Y, double Z, double M);

    /// <summary>
    /// Compose the n-gram trajectory entity from its ordered <paramref name="constituents"/>
    /// and append the entity + Content-physicality rows to <paramref name="b"/>. Returns the
    /// content-addressed entity id (so the caller can hang completion attestations off it).
    /// </summary>
    /// <param name="tier">The n-gram's tier — strictly above its constituents' tier.</param>
    /// <param name="typeId">The substrate type entity for this n-gram tier.</param>
    /// <returns>The content-addressed entity id + its EntityRow + Content PhysicalityRow. The
    /// caller dedups by id and folds — Compose does NOT touch a builder, so identical n-grams
    /// from different witnesses collapse to one entity client-side (no duplicate writes).</returns>
    public static (Hash128 Id, EntityRow Entity, PhysicalityRow Physicality) Compose(
        IReadOnlyList<Constituent> constituents,
        byte tier, Hash128 typeId, Hash128 sourceId, long nowUs)
    {
        int n = constituents.Count;
        if (n == 0) throw new ArgumentException("n-gram has no constituents", nameof(constituents));

        var childIds = new Hash128[n];
        var coords   = new double[(long)n * 4];
        for (int i = 0; i < n; i++)
        {
            var c = constituents[i];
            childIds[i] = c.Id;
            coords[i * 4 + 0] = c.X; coords[i * 4 + 1] = c.Y;
            coords[i * 4 + 2] = c.Z; coords[i * 4 + 3] = c.M;
        }

        Hash128    id    = Hash128.Merkle(tier, childIds);
        double[]   cen   = Math4d.Centroid(coords);                // canonical composite coord
        Hilbert128 hb    = Hilbert128.Encode(cen);
        double[]   traj  = Trajectory.Build(childIds);             // the path = ordered constituents
        Hash128    physId = PhysicalityId.Compute(
            id, sourceId, PhysicalityType.Content, cen[0], cen[1], cen[2], cen[3], traj);

        var entity = new EntityRow(id, tier, typeId, sourceId);
        var phys = new PhysicalityRow(
            Id:                physId,
            EntityId:          id,
            SourceId:          sourceId,
            Type:              PhysicalityType.Content,
            CoordX:            cen[0], CoordY: cen[1], CoordZ: cen[2], CoordM: cen[3],
            HilbertIndex:      hb,
            TrajectoryXyzm:    traj,
            NConstituents:     n,
            AlignmentResidual: null,
            SourceDim:         null,
            ObservedAtUnixUs:  nowUs);
        return (id, entity, phys);
    }
}
