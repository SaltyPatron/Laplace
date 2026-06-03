using System.Runtime.InteropServices;

namespace Laplace.Engine.Dynamics;

/// <summary>
/// P/Invoke bindings to liblaplace_dynamics.
///
/// The static constructor calls LaplaceDynamicsInit() which locks MKL's
/// threading layer to TBB and sets MKL_CBWR for substrate determinism.
/// MKL/TBB integration is conditional on the lib being built with
/// LAPLACE_REQUIRE_MKL=ON (or auto-detected at configure time).
///
/// Bindings: Procrustes alignment, Laplacian eigenmaps, Gram-Schmidt,
/// and the faithful contracted-operator circuit kernels
/// (project_embedding + bilinear_edges_tile).
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
        //: init MKL once at C# binding load. Return code !=0
        // means MKL_CBWR couldn't be locked — caller (test harness or app
        // startup) is responsible for surfacing if substrate determinism
        // is required. In the Eigen-only fallback build, returns 0.
        _ = LaplaceDynamicsInit();
    }

    // === Laplacian eigenmaps ===

    /// <summary>
    /// Project high_dim_pts (n × high_dim, row-major) to low_dim_out (n × target_dim, row-major)
    /// via Laplacian eigenmaps over a k-NN graph. Returns 0 on success, negative on error.
    /// NOTE: k-NN construction is O(n² × high_dim) — use on representative anchors, not full 32K vocab.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "laplacian_eigenmaps")]
    public static unsafe partial int LaplacianEigenmaps(
        double* highDimPts, nuint n, nuint highDim,
        nuint kNeighbors, nuint targetDim,
        double* lowDimOut);

    /// <summary>
    /// Laplacian eigenmaps with a precomputed sparse graph in COO triples
    /// (substrate's typed-edge attestation set; weights via Glicko-2
    /// effective μ). Skips the k-NN construction step. Drops non-positive
    /// weights (substrate noise floor). Symmetrizes internally so directed
    /// edges (e.g. Q_PROJECTS) yield a symmetric Laplacian. Returns 0 on
    /// success, -1 null, -2 invalid args, -3 eigensolver failure, -4
    /// degenerate input.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "laplacian_eigenmaps_from_sparse_graph")]
    public static unsafe partial int LaplacianEigenmapsFromSparseGraph(
        int* cooRows, int* cooCols, double* cooWeights,
        nuint nnz, nuint n, nuint targetDim,
        double* lowDimOut);

    // === Faithful contracted-operator edges (model evidence core) ===

    /// <summary>
    /// Materialize the contracted bilinear operator M = Left·Rightᵀ for the row
    /// range [rowBegin, rowEnd) and emit every (i, j) whose SIGNED value exceeds
    /// the coherence threshold <paramref name="theta"/>. Left/Right are the
    /// projected embeddings (E·Wq / E_U·Wk etc.); the only cut is theta — never
    /// argmax, never top-k, never an a-priori floor. f64 dgemm, deterministic.
    /// Caller tiles row ranges (M is never dense over the full vocab) and drains
    /// outRows/outCols/outVals (length ≥ cap) each call; overflow → 1 if cap hit.
    /// Returns 0 ok, -1 bad args, -2 no MKL.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "bilinear_edges_tile")]
    public static unsafe partial int BilinearEdgesTile(
        double* left, nuint rowBegin, nuint rowEnd,
        double* right, nuint nRight,
        nuint r, double theta,
        int* outRows, int* outCols, double* outVals,
        nuint cap, nuint* outCount, int* overflow);

    /// <summary>
    /// Project an embedding through a circuit weight: out [n × r] = pts [n × d] · Wᵀ
    /// (W is [r × d] row-major, safetensors out×in). Forms the Left/Right operands
    /// for <see cref="BilinearEdgesTile"/>. f32 in → exact f64 dgemm → f64 out.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "project_embedding")]
    public static unsafe partial int ProjectEmbedding(
        float* pts, nuint n, nuint d, float* w, nuint r, double* outp);

    // === Gram-Schmidt ===

    /// <summary>
    /// In-place orthonormalization of vectors (n_vecs × dim, row-major) via Eigen HouseholderQR.
    /// Returns 0 on success, nonzero if rank-deficient.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "gram_schmidt_orthonormalize")]
    public static unsafe partial int GramSchmidtOrthonormalize(
        double* vectors, nuint nVecs, nuint dim);

    // === Procrustes alignment ===

    /// <summary>
    /// Fit a Procrustes transform aligning source_pts (n × source_dim) to target_pts (n × 4).
    /// Returns opaque handle; caller must free with ProcrustesFree.
    /// Returns IntPtr.Zero on failure.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "procrustes_fit")]
    public static unsafe partial IntPtr ProcrustesFit(
        double* sourcePts, nuint n, nuint sourceDim,
        double* targetPts);   // n × 4 doubles

    /// <summary>
    /// Apply the Procrustes transform to a single source vector → 4D output.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "procrustes_apply")]
    public static unsafe partial void ProcrustesApply(
        IntPtr transform,
        double* sourceVec, nuint sourceDim,
        double* out4);   // 4 doubles

    /// <summary>Frobenius residual of the Procrustes fit.</summary>
    [LibraryImport(Library, EntryPoint = "procrustes_residual")]
    public static partial double ProcrustesResidual(IntPtr transform);

    [LibraryImport(Library, EntryPoint = "procrustes_free")]
    public static partial void ProcrustesFree(IntPtr transform);

}
