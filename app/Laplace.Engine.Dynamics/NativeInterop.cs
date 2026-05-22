using System.Runtime.InteropServices;

namespace Laplace.Engine.Dynamics;

/// <summary>
/// P/Invoke bindings to liblaplace_dynamics (per ADR 0024 + 0026 + 0030).
///
/// The static constructor calls LaplaceDynamicsInit() which locks MKL's
/// threading layer to TBB and sets MKL_CBWR for substrate determinism
/// (per RULES.md R7). Per ADR 0030 + the dynamics build CMakeLists,
/// MKL/TBB integration is conditional on the lib being built with
/// LAPLACE_REQUIRE_MKL=ON (or auto-detected at configure time).
///
/// Real bindings (procrustes_fit, laplacian_eigenmaps,
/// gram_schmidt_orthonormalize, sparsity filters) land Chunk 6.
/// </summary>
public static partial class NativeInterop
{
    private const string Library = "laplace_dynamics";

    /// <summary>One-shot init — locks MKL threading + CBWR. Idempotent.</summary>
    [LibraryImport(Library, EntryPoint = "laplace_dynamics_init")]
    public static partial int LaplaceDynamicsInit();

    [LibraryImport(Library, EntryPoint = "laplace_dynamics_version", StringMarshalling = StringMarshalling.Utf8)]
    public static partial string LaplaceDynamicsVersion();

    static NativeInterop()
    {
        // Per ADR 0030: init MKL once at C# binding load. Return code !=0
        // means MKL_CBWR couldn't be locked — caller (test harness or app
        // startup) is responsible for surfacing if substrate determinism
        // is required. In the Eigen-only fallback build, returns 0.
        _ = LaplaceDynamicsInit();
    }

    // TODO Chunk 6.8: procrustes_fit / procrustes_apply / procrustes_residual / procrustes_free
    // TODO Chunk 6.6: laplacian_eigenmaps
    // TODO Chunk 6.7: gram_schmidt_orthonormalize
    // TODO Chunk 6.10-6.12: sparsity_per_tensor_topk / sparsity_per_row_topk / sparsity_probe_validate
}
