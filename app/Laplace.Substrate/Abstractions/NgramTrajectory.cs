using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class NgramTrajectory
{
    public readonly record struct Constituent(Hash128 Id, double X, double Y, double Z, double M);

    public static (Hash128 Id, EntityRow Entity, PhysicalityRow Physicality) Compose(
        IReadOnlyList<Constituent> constituents,
        byte tier, Hash128 typeId, Hash128 sourceId, long nowUs)
    {
        int n = constituents.Count;
        if (n == 0) throw new ArgumentException("n-gram has no constituents", nameof(constituents));

        var childIds = new Hash128[n];
        var coords = new double[(long)n * 4];
        for (int i = 0; i < n; i++)
        {
            var c = constituents[i];
            childIds[i] = c.Id;
            coords[i * 4 + 0] = c.X; coords[i * 4 + 1] = c.Y;
            coords[i * 4 + 2] = c.Z; coords[i * 4 + 3] = c.M;
        }

        Hash128 id = Hash128.Merkle(tier, childIds);
        // Karcher, not Centroid: the intrinsic mean lands ON S3 at norm 1. The
        // Euclidean centroid falls inside the 4-ball — measured min 0.003148
        // across 14.5M composed placements, 241,015 under 0.1 where the
        // normalised direction is float noise. Both are permutation-invariant
        // now; only this one is on the sphere. Changes every composed coord and
        // every hilbert_index derived from it — requires a reseed.
        double[] cen = Math4d.KarcherMean(coords);
        Hilbert128 hb = Hilbert128.Encode(cen);
        double[] traj = Trajectory.Build(childIds);
        Hash128 physId = PhysicalityId.Compute(id, PhysicalityType.Content);

        var entity = new EntityRow(id, tier, typeId, sourceId);
        var phys = new PhysicalityRow(
            Id: physId,
            EntityId: id,
            SourceId: sourceId,
            Type: PhysicalityType.Content,
            CoordX: cen[0], CoordY: cen[1], CoordZ: cen[2], CoordM: cen[3],
            HilbertIndex: hb,
            TrajectoryXyzm: traj,
            NConstituents: n,
            AlignmentResidual: null,
            SourceDim: null,
            ObservedAtUnixUs: nowUs);
        return (id, entity, phys);
    }
}
