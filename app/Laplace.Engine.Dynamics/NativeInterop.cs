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

    // Returns IntPtr (not string): C side returns .rodata string literal,
    // so `string` + StringMarshalling.Utf8 would have the source generator
    // call NativeMemory.Free() on the returned pointer post-copy and
    // crash with `free(): invalid pointer`.
    [LibraryImport(Library, EntryPoint = "laplace_dynamics_version")]
    private static partial IntPtr LaplaceDynamicsVersionPtr();

    public static string LaplaceDynamicsVersion() =>
        Marshal.PtrToStringUTF8(LaplaceDynamicsVersionPtr()) ?? string.Empty;

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
    //   (multi-pass lottery-ticket filter — distinct from streaming variants below)

    // === Streaming sparsity (Framework Epic #232 / Stories B.1 + B.2) ===
    // Single-pass per-tensor + per-row top-k variants used by WeightTensorETL
    // (ADR 0056). Deterministic per MKL_CBWR mode lock above. TBB-parallel
    // for large inputs (n >= 65536 per-tensor; row_count >= 4 per-row).

    [LibraryImport(Library, EntryPoint = "sparsity_per_tensor_topk_streaming")]
    internal static unsafe partial int SparsityPerTensorTopkStreaming(
        double* values, nuint n, double topkPct, byte* outMask);

    [LibraryImport(Library, EntryPoint = "sparsity_per_row_topk_streaming")]
    internal static unsafe partial int SparsityPerRowTopkStreaming(
        double* rows, nuint rowCount, nuint rowSize, nuint k, byte* outMasks);
}
