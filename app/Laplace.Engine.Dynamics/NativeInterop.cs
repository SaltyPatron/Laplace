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

    // TODO Chunk 6.10-6.12: sparsity_per_tensor_topk / sparsity_per_row_topk / sparsity_probe_validate
    //   (multi-pass lottery-ticket filter — distinct from streaming variants below)

    // === Streaming sparsity (Framework Epic #232 / Stories B.1 + B.2) ===
    // Single-pass per-tensor + per-row top-k variants used by WeightTensorETL
    // (ADR 0056). Deterministic per MKL_CBWR mode lock above. TBB-parallel
    // for large inputs (n >= 65536 per-tensor; row_count >= 4 per-row).

    [LibraryImport(Library, EntryPoint = "sparsity_per_tensor_topk_streaming")]
    public static unsafe partial int SparsityPerTensorTopkStreaming(
        double* values, nuint n, double topkPct, byte* outMask);

    [LibraryImport(Library, EntryPoint = "sparsity_per_row_topk_streaming")]
    public static unsafe partial int SparsityPerRowTopkStreaming(
        double* rows, nuint rowCount, nuint rowSize, nuint k, byte* outMasks);

    // === Circuit extraction (O(params) address-book read; TBB-parallel, exact) ===
    // The hot loop of model ingestion, moved off the managed scalar path into the engine.

    /// <summary>One surviving (unit, token, signed value) cell from resolve_matrix.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CircuitCell { public uint Unit; public int Token; public float Value; }

    /// <summary>Model inspector: detect a viable noise floor for a weight tensor from its own
    /// magnitude distribution so kept cells retain targetEnergy of Σw² (the lottery-ticket
    /// subnetwork). Model-agnostic, TBB-histogrammed. targetEnergy 1.0 ⇒ 0 (keep all).</summary>
    [LibraryImport(Library, EntryPoint = "detect_energy_floor")]
    public static unsafe partial double DetectEnergyFloor(float* w, nuint n, double targetEnergy);

    /// <summary>addr[m] = argmax_{t : valid[t]} |E[t,m]| over vocab (or -1). E is [vocab × d_model]
    /// row-major. valid may be null. TBB-parallel over dims. 0 ok, -1 bad args.</summary>
    [LibraryImport(Library, EntryPoint = "build_address_book")]
    public static unsafe partial int BuildAddressBook(
        float* e, nuint vocab, nuint dModel, byte* valid, int* addrOut);

    /// <summary>Resolve rows [u0,u1) of a row-major [n_units × d_model] matrix against addr,
    /// emitting every |value|&gt;floor (addr&gt;=0) cell as (unit, token, signed value). TBB-parallel,
    /// dense+deterministic. *overflow=1 (and nothing emitted) if the window exceeds cap — shrink +
    /// retry. 0 ok, negative bad args.</summary>
    [LibraryImport(Library, EntryPoint = "resolve_matrix")]
    public static unsafe partial int ResolveMatrix(
        float* w, nuint nUnits, nuint dModel, int* addr, double floor,
        nuint u0, nuint u1, CircuitCell* outBuf, nuint cap, nuint* outCount, int* overflow);
}
