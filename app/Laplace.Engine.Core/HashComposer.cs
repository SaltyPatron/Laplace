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
}
