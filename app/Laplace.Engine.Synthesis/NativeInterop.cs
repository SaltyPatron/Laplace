using System.Runtime.InteropServices;

namespace Laplace.Engine.Synthesis;

/// <summary>
/// P/Invoke bindings to liblaplace_synthesis (per ADR 0024 + 0026).
///
/// Recipe parsing, architecture-template materialization, BF16 decoding,
/// static QK attention scoring, native package emission, and GGUF proof
/// export live here. Per ADR 0027: math in C/C++, orchestration in C#.
/// </summary>
public static partial class NativeInterop
{
    private const string Library = "laplace_synthesis";

    // Returns IntPtr (not string): C side returns .rodata string literal,
    // so `string` + StringMarshalling.Utf8 would have the source generator
    // call NativeMemory.Free() on the returned pointer post-copy and
    // crash with `free(): invalid pointer`.
    [LibraryImport(Library, EntryPoint = "laplace_synthesis_version")]
    private static partial IntPtr LaplaceSynthesisVersionPtr();

    public static string LaplaceSynthesisVersion() =>
        Marshal.PtrToStringUTF8(LaplaceSynthesisVersionPtr()) ?? string.Empty;

    // === BF16 decoder ===

    /// <summary>
    /// Convert n_elements packed BF16 values (2 bytes each) to double-precision.
    /// AVX2-vectorized on x86-64; scalar fallback otherwise.
    /// Returns 0 on success, -1 on null pointer.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "laplace_bf16_decode")]
    public static unsafe partial int Bf16Decode(void* rawBytes, nuint nElements, double* outValues);

    // === Recipe ===

    [LibraryImport(Library, EntryPoint = "recipe_parse")]
    public static unsafe partial IntPtr RecipeParse(byte* jsonText, nuint len);

    [LibraryImport(Library, EntryPoint = "recipe_get_field",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial IntPtr RecipeGetField(IntPtr recipe, string fieldName);

    [LibraryImport(Library, EntryPoint = "recipe_free")]
    public static partial void RecipeFree(IntPtr recipe);

    // === Architecture template ===

    [LibraryImport(Library, EntryPoint = "arch_template_load",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr ArchTemplateLoad(string templateName);

    [LibraryImport(Library, EntryPoint = "arch_template_required_tensors")]
    public static unsafe partial int ArchTemplateRequiredTensors(
        IntPtr tmpl, IntPtr recipe, TensorSpec* outSpecs, nuint cap);

    [LibraryImport(Library, EntryPoint = "arch_template_free")]
    public static partial void ArchTemplateFree(IntPtr tmpl);

    // === Interior-tensor reconstruction (substrate-native codec) ===

    /// <summary>
    /// Symmetric factorization: recover ONE W [out_dim × N] such that
    /// S ≈ E·Wᵀ·W·Eᵀ for the kind-specific sparse adjacency S. Used for
    /// V_PROJECTS / O_PROJECTS / GATES / UP_PROJECTS / DOWN_PROJECTS.
    /// Returns 0 / -1 null / -2 invalid args / -3 eigensolver failure /
    /// -4 degenerate. Internal: Eigen LDLT + SelfAdjointEigenSolver.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "reconstruct_w_from_token_pair_attestations")]
    public static unsafe partial int ReconstructWFromTokenPairAttestations(
        double* E, nuint vocab, nuint N,
        int* sRows, int* sCols, double* sWeights, nuint sNnz,
        nuint outDim, double lambda,
        float* WOut);

    /// <summary>
    /// Asymmetric (joint-bilinear) factorization: recover BOTH Wq
    /// [out_dim_q × N] AND Wk [out_dim_k × N] such that S ≈ E·Wqᵀ·Wk·Eᵀ.
    /// Used for Q_PROJECTS — TinyLlama GQA has Wq=[2048×2048] and
    /// Wk=[256×2048] (different shapes); symmetric collapse would destroy
    /// behavioral fidelity. Internal: Eigen LDLT + JacobiSVD.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "reconstruct_qk_from_token_pair_attestations")]
    public static unsafe partial int ReconstructQkFromTokenPairAttestations(
        double* E, nuint vocab, nuint N,
        int* sRows, int* sCols, double* sWeights, nuint sNnz,
        nuint outDimQ, nuint outDimK, double lambda,
        float* WqOut, float* WkOut);

    // === Static QK attention scorer ===

    /// <summary>
    /// SVD-based per-row top-k token-to-token static QK scores for one attention head.
    /// E_bf16: [n_vocab × d_model] in BF16; Wq/Wk: [head_dim × d_model] in f32.
    /// outPairs: caller-allocated; must hold at least nVocab*topkPerRow entries.
    /// Returns number of pairs written, or -1 on error.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "compute_static_qk_scores")]
    public static unsafe partial int ComputeStaticQkScores(
        ushort* E_bf16, nuint nVocab, nuint dModel,
        float* Wq, float* Wk, nuint headDim,
        nuint topkPerRow,
        QkPair* outPairs, nuint outCap);

    /// <summary>
    /// Batch SVD-based QK scorer: all attention heads for one layer, TBB-parallel.
    /// WqAll: [n_heads × head_dim × d_model] f32 (all query heads stacked).
    /// WkAll: [n_kv_heads × head_dim × d_model] f32 (all KV heads stacked).
    /// outPairs: flat [n_heads × outCapPerHead] — head h at outPairs + h*outCapPerHead.
    /// outCounts: [n_heads] — number of pairs written per head.
    /// outCapPerHead must be ≥ nVocab * topkPerRow.
    /// Returns 0 on success, -1 on error.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "compute_static_qk_scores_batch")]
    public static unsafe partial int ComputeStaticQkScoresBatch(
        ushort* E_bf16, nuint nVocab, nuint dModel,
        float* WqAll, float* WkAll,
        nuint nHeads, nuint nKvHeads, nuint headDim,
        nuint queriesPerKv, nuint topkPerRow,
        QkPair* outPairs, int* outCounts, nuint outCapPerHead);

    // === Tensor decomposition ===

    /// <summary>
    /// Energy-truncated thin SVD of a row-major f32 matrix A [m × n].
    /// Keeps the minimal rank r such that ‖A − Aᵣ‖_F ≤ relErrTol·‖A‖_F
    /// (Eckart-Young). relErrTol=0 ⇒ full rank. The substrate's no-flat-threshold
    /// significance selector: retained rank adapts per tensor to its own spectrum.
    /// U: [m × kmax] row-major (first r cols), S: [kmax] (first r), Vt: [kmax × n]
    /// (first r rows). kmax must be ≥ min(m,n). Returns 0 ok, -1 bad args,
    /// -2 if LAPACK/MKL unavailable, or a positive LAPACK info on failure.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "tensor_svd_truncate")]
    public static unsafe partial int TensorSvdTruncate(
        float* A, nuint m, nuint n,
        double relErrTol,
        nuint* outRank,
        float* U, float* S, float* Vt, nuint kmax);

    /// <summary>
    /// E·Wᵀ token→feature projection scorer (ADR 0056 interior-role math_function:
    /// V/O/GATES/UP/DOWN). For each token, top-k feature dims by |（E·Wᵀ)[token,dim]|.
    /// E_bf16: [nVocab × dModel] BF16; W: [nOut × dModel] f32 (output×input).
    /// outPairs: QueryIdx=token, KeyIdx=feature dim, Score=projection value.
    /// Returns pairs written, -1 bad args, -2 if MKL unavailable.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "compute_static_projection_scores")]
    public static unsafe partial int ComputeStaticProjectionScores(
        ushort* E_bf16, nuint nVocab, nuint dModel,
        float* W, nuint nOut,
        nuint topkPerRow,
        QkPair* outPairs, nuint outCap);

    // === GGUF writer ===

    [LibraryImport(Library, EntryPoint = "gguf_writer_create",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GgufWriterCreate(string outputPath);

    [LibraryImport(Library, EntryPoint = "gguf_writer_add_metadata_str",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GgufWriterAddMetadataStr(IntPtr w, string key, string value);

    [LibraryImport(Library, EntryPoint = "gguf_writer_add_metadata_u32",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GgufWriterAddMetadataU32(IntPtr w, string key, uint value);

    [LibraryImport(Library, EntryPoint = "gguf_writer_add_metadata_f32",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GgufWriterAddMetadataF32(IntPtr w, string key, float value);

    [LibraryImport(Library, EntryPoint = "gguf_writer_add_metadata_bool",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GgufWriterAddMetadataBool(IntPtr w, string key, int value);

    [LibraryImport(Library, EntryPoint = "gguf_writer_add_metadata_str_array_packed",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int GgufWriterAddMetadataStrArrayPacked(
        IntPtr w, string key, byte* packedData, nuint totalBytes, nuint count);

    [LibraryImport(Library, EntryPoint = "gguf_writer_add_metadata_f32_array",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int GgufWriterAddMetadataF32Array(
        IntPtr w, string key, float* values, nuint count);

    [LibraryImport(Library, EntryPoint = "gguf_writer_add_metadata_i32_array",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int GgufWriterAddMetadataI32Array(
        IntPtr w, string key, int* values, nuint count);

    [LibraryImport(Library, EntryPoint = "gguf_writer_add_tensor",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int GgufWriterAddTensor(
        IntPtr w, string name, int dtype,
        nuint* shape, nuint rank, void* data);

    [LibraryImport(Library, EntryPoint = "gguf_writer_finalize")]
    public static partial int GgufWriterFinalize(IntPtr w);

    [LibraryImport(Library, EntryPoint = "gguf_writer_free")]
    public static partial void GgufWriterFree(IntPtr w);

    // === Format writer ===

    [LibraryImport(Library, EntryPoint = "format_writer_create",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr FormatWriterCreate(string format, string outputDirPath);

    [LibraryImport(Library, EntryPoint = "format_writer_add_tensor",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int FormatWriterAddTensor(
        IntPtr w, string name, int dtype,
        nuint* shape, nuint rank, void* data, nuint dataLen);

    [LibraryImport(Library, EntryPoint = "format_writer_set_config")]
    internal static unsafe partial int FormatWriterSetConfig(IntPtr w, byte* configJson, nuint len);

    [LibraryImport(Library, EntryPoint = "format_writer_set_tokenizer")]
    internal static unsafe partial int FormatWriterSetTokenizer(IntPtr w, byte* tokJson, nuint len);

    [LibraryImport(Library, EntryPoint = "format_writer_finalize")]
    internal static partial int FormatWriterFinalize(IntPtr w);

    [LibraryImport(Library, EntryPoint = "format_writer_free")]
    internal static partial void FormatWriterFree(IntPtr w);
}

/// <summary>
/// Sparse (query, key, score) triplet from <see cref="NativeInterop.ComputeStaticQkScores"/>.
/// Must match the C struct qk_pair_t layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct QkPair
{
    public uint  QueryIdx;
    public uint  KeyIdx;
    public float Score;
}

/// <summary>
/// Tensor specification from <see cref="NativeInterop.ArchTemplateRequiredTensors"/>.
/// Must match the C struct tensor_spec_t layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TensorSpec
{
    public byte*  Name;       /* points into arch_template_t internal storage */
    public ulong  Rank;
    public fixed ulong Shape[8];
    public int    Dtype;      /* 0=f32, 1=f16, 2=bf16 */
}
