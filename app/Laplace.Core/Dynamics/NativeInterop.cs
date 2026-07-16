using System.Runtime.InteropServices;

namespace Laplace.Engine.Dynamics;

public static partial class NativeInterop
{
    private const string Library = "laplace_dynamics";

    [LibraryImport(Library, EntryPoint = "laplace_dynamics_init")]
    public static partial int LaplaceDynamicsInit();

    [LibraryImport(Library, EntryPoint = "laplace_dynamics_version")]
    private static partial IntPtr LaplaceDynamicsVersionPtr();

    public static string LaplaceDynamicsVersion() =>
        Marshal.PtrToStringUTF8(LaplaceDynamicsVersionPtr()) ?? string.Empty;

    static NativeInterop()
    {
        _ = LaplaceDynamicsInit();
    }

    [LibraryImport(Library, EntryPoint = "laplacian_eigenmaps")]
    public static unsafe partial int LaplacianEigenmaps(
        double* highDimPts, nuint n, nuint highDim,
        nuint kNeighbors, nuint targetDim,
        double* lowDimOut);

    [LibraryImport(Library, EntryPoint = "laplacian_eigenmaps_from_sparse_graph")]
    public static unsafe partial int LaplacianEigenmapsFromSparseGraph(
        int* cooRows, int* cooCols, double* cooWeights,
        nuint nnz, nuint n, nuint targetDim,
        double* lowDimOut);

    [LibraryImport(Library, EntryPoint = "bilinear_edges_tile")]
    public static unsafe partial int BilinearEdgesTile(
        double* left, nuint rowBegin, nuint rowEnd,
        double* right, nuint nRight,
        nuint r, double theta,
        int* outRows, int* outCols, double* outVals, long* outScores,
        nuint cap, nuint* outCount, int* overflow);

    [LibraryImport(Library, EntryPoint = "project_embedding")]
    public static unsafe partial int ProjectEmbedding(
        float* pts, nuint n, nuint d, float* w, nuint r, double* outp);

    [LibraryImport(Library, EntryPoint = "project_embedding_d")]
    public static unsafe partial int ProjectEmbeddingD(
        double* pts, nuint n, nuint d, float* w, nuint r, double* outp);

    [LibraryImport(Library, EntryPoint = "norm_rows_d")]
    public static unsafe partial int NormRowsD(double* data, nuint n, nuint dim);

    [LibraryImport(Library, EntryPoint = "expand_kv_heads_d")]
    public static unsafe partial int ExpandKvHeadsD(
        double* kv, nuint n, nuint nHeads, nuint nKv, nuint headDim, double* outp);

    [LibraryImport(Library, EntryPoint = "center_columns_f")]
    public static unsafe partial int CenterColumnsF(float* m, nuint n, nuint d);

    [LibraryImport(Library, EntryPoint = "ffn_write_vectors_d")]
    public static unsafe partial int FfnWriteVectorsD(
        double* x, nuint n, nuint d, float* up, float* upBias, float* gate, nuint interm,
        float* down, nuint dOut, int act, double* outp);

    [LibraryImport(Library, EntryPoint = "layer_norm_rows_d")]
    public static unsafe partial int LayerNormRowsD(
        double* m, nuint n, nuint d, float* gamma, float* beta, double eps);

    [LibraryImport(Library, EntryPoint = "add_row_vector_d")]
    public static unsafe partial int AddRowVectorD(double* m, nuint n, nuint d, float* v);

    [LibraryImport(Library, EntryPoint = "hypot_rows_d")]
    public static unsafe partial int HypotRowsD(double* a, double* b, nuint n, double* outp);

    [LibraryImport(Library, EntryPoint = "center_columns_d")]
    public static unsafe partial int CenterColumnsD(double* m, nuint n, nuint d);

    [LibraryImport(Library, EntryPoint = "scale_cols_f")]
    public static unsafe partial int ScaleColsF(float* m, nuint rows, nuint d, float* g);

    [LibraryImport(Library, EntryPoint = "scale_cols_d")]
    public static unsafe partial int ScaleColsD(double* m, nuint rows, nuint d, float* g);

    [LibraryImport(Library, EntryPoint = "slice_head_d")]
    public static unsafe partial int SliceHeadD(
        double* full, double* head, nuint n, nuint fullDim, nuint h, nuint hd);

    [LibraryImport(Library, EntryPoint = "row_norms_out_d")]
    public static unsafe partial int RowNormsOutD(double* m, nuint n, nuint d, double* outNorms);

    [LibraryImport(Library, EntryPoint = "f32_to_f64")]
    public static unsafe partial int F32ToF64(float* src, nuint count, double* dst);

    [LibraryImport(Library, EntryPoint = "ffn_activation_norms")]
    public static unsafe partial int FfnActivationNorms(
        double* x, nuint n, nuint d, float* up, float* gate, nuint interm, double* outNorms);

    [LibraryImport(Library, EntryPoint = "ffn_token_pairs_tile")]
    public static unsafe partial int FfnTokenPairsTile(
        double* emb, nuint n, nuint d,
        double* unemb,
        double* gate, double* up, double* down, nuint interm,
        nuint rowBegin, nuint rowEnd,
        double theta,
        int* outRows, int* outCols, double* outVals, long* outScores,
        nuint cap, nuint* outCount, int* overflow);

    [LibraryImport(Library, EntryPoint = "gram_schmidt_orthonormalize")]
    public static unsafe partial int GramSchmidtOrthonormalize(
        double* vectors, nuint nVecs, nuint dim);

    [LibraryImport(Library, EntryPoint = "procrustes_fit")]
    public static unsafe partial IntPtr ProcrustesFit(
        double* sourcePts, nuint n, nuint sourceDim,
        double* targetPts);

    [LibraryImport(Library, EntryPoint = "procrustes_apply")]
    public static unsafe partial void ProcrustesApply(
        IntPtr transform,
        double* sourceVec, nuint sourceDim,
        double* out4);

    [LibraryImport(Library, EntryPoint = "procrustes_residual")]
    public static partial double ProcrustesResidual(IntPtr transform);

    [LibraryImport(Library, EntryPoint = "procrustes_free")]
    public static partial void ProcrustesFree(IntPtr transform);

}
