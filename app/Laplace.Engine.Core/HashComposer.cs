using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Managed wrapper over the engine <c>hash_composer_run</c>
/// (engine/core/include/laplace/core/hash_composer.h). Pure leaf-to-trunk
/// content-addressing primitive per ADR 0048.
///
/// The atom resolver is supplied by the caller as a static
/// <c>[UnmanagedCallersOnly]</c> method (typically for text decomposers:
/// a thin wrapper around the codepoint perfcache lookup; for tests:
/// a synthetic mapping). The function-pointer signature matches the
/// engine typedef <c>hash_composer_atom_resolver_fn</c>.
/// </summary>
public static unsafe class HashComposer
{
    // Signature compatible with engine's hash_composer_atom_resolver_fn:
    //   int (*)(uint32_t atom, void* user_data,
    //           hash128_t* out_id, double out_coord[4], hilbert128_t* out_hilbert)
    // Use a static [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    // method to obtain a function pointer of the right shape, then take its
    // address with &MethodName.

    /// <summary>Populate id/coord/hilbert for every node in
    /// <paramref name="tree"/> via a bottom-up walk. T0 leaves go through
    /// the caller-supplied <paramref name="resolver"/>; T≥1 interior
    /// nodes compose via Merkle + centroid + Hilbert encode.</summary>
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
