using System.Runtime.InteropServices;

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

        bool added = false;
        try
        {
            tree.DangerousAddRef(ref added);
            int rc = NativeInterop.HashComposerRun(tree.DangerousNativeHandle,
                                                    resolver, resolverUserData);
            if (rc != 0)
                throw new InvalidOperationException($"hash_composer_run returned {rc}");
        }
        finally
        {
            if (added) tree.DangerousRelease();
        }
    }









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

        Hash128 id;
        Hilbert128 hb;
        fixed (Hash128* cids = childIds)
        fixed (double* ccoords = childCoords)
        fixed (double* oc = outCoord)
        {
            NativeInterop.HashComposerComposeNode(
                tier, cids, ccoords, (nuint)childIds.Length, &id, oc, &hb);
        }
        return (id, hb);
    }
}
