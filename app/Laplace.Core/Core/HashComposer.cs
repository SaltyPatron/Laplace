using Laplace.Native;

namespace Laplace.Engine.Core;

public static unsafe class HashComposer
{
    public static void Run(
        TierTree tree,
        delegate* unmanaged[Cdecl]<uint, IntPtr, Hash128*, double*, Hilbert128*, int> resolver,
        IntPtr resolverUserData = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (resolver == null) throw new ArgumentNullException(nameof(resolver));

        int count = tree.NodeCount;
        Span<double> coord = stackalloc double[4];
        for (uint i = 0; i < (uint)count; i++)
        {
            var node = tree.GetNode(i);
            if (node.Tier == 0)
            {
                Hash128 id = default;
                Hilbert128 hb = default;
                fixed (double* c = coord)
                {
                    int rc = resolver(node.Atom, resolverUserData, &id, c, &hb);
                    if (rc != 0)
                        throw new InvalidOperationException($"HashComposer leaf resolver failed for U+{node.Atom:X} (rc={rc}).");
                }
                tree.SetId(i, id);
                tree.SetCoord(i, coord);
                tree.SetHilbert(i, hb);
            }
            else if (node.ChildCount > 0)
            {
                int nc = checked((int)node.ChildCount);
                var childIds = new Hash128[nc];
                var childCoords = new double[nc * 4];
                for (int ci = 0; ci < nc; ci++)
                {
                    var child = tree.GetNode(node.FirstChildIdx + (uint)ci);
                    childIds[ci] = child.Id;
                    childCoords[ci * 4] = child.Coord[0];
                    childCoords[ci * 4 + 1] = child.Coord[1];
                    childCoords[ci * 4 + 2] = child.Coord[2];
                    childCoords[ci * 4 + 3] = child.Coord[3];
                }
                var (id, hb) = ComposeNode(node.Tier, childIds, childCoords, coord);
                tree.SetId(i, id);
                tree.SetCoord(i, coord);
                tree.SetHilbert(i, hb);
            }
        }
    }

    public static void RunWorkers(
        TierTree tree,
        delegate* unmanaged[Cdecl]<uint, IntPtr, Hash128*, double*, Hilbert128*, int> resolver,
        int workerCount,
        IntPtr resolverUserData = default)
    {
        if (workerCount <= 0) throw new ArgumentOutOfRangeException(nameof(workerCount));
        Run(tree, resolver, resolverUserData);
    }

    public static Hash128 ComputeMerkleId(byte tier, params ReadOnlySpan<Hash128> children)
        => Hash128.Merkle(tier, children);

    public static (Hash128 Id, Hilbert128 Hilbert) ComposeNode(
        byte tier,
        ReadOnlySpan<Hash128> childIds,
        ReadOnlySpan<double> childCoords,
        Span<double> outCoord)
    {
        if (childIds.Length == 0)
            throw new ArgumentException("childIds must be non-empty", nameof(childIds));
        if (childCoords.Length != childIds.Length * 4)
            throw new ArgumentException("childCoords length must be childIds.Length * 4", nameof(childCoords));
        if (outCoord.Length < 4)
            throw new ArgumentException("outCoord must have length >= 4", nameof(outCoord));

        for (int i = 0; i < childCoords.Length; i += 4)
        {
            double x = childCoords[i];
            double y = childCoords[i + 1];
            double z = childCoords[i + 2];
            double m = childCoords[i + 3];
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) || !double.IsFinite(m)
                || (x * x + y * y + z * z + m * m) > 1.000001)
                throw new ArgumentException("Child coordinates must be finite and within the Native coordinate domain.", nameof(childCoords));
        }

        Hash128 id = Hash128.Merkle(tier, childIds);
        double[] mean = Math4d.Centroid(childCoords);
        mean.AsSpan().CopyTo(outCoord);
        Hilbert128 hb = Hilbert128.Encode(mean);
        return (id, hb);
    }
}
