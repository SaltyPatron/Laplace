using System.Runtime.InteropServices;

namespace Laplace.Engine.Synthesis;

/// <summary>
/// P/Invoke bindings to liblaplace_synthesis.
///
/// Recipe parsing, architecture-template materialization, BF16 decoding,
/// the exact QK pair/projection kernels, export-only SVD factoring, and the
/// GGUF/format writers live here. Math in C/C++, orchestration in C#.
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

    /// <summary>
    /// Distribute substrate consensus values into one tensor slot per the
 /// architecture template's recipe layout.
    /// NOT a pseudoinverse — broadcast across the recipe's per-(layer, head, dim)
    /// shape. See engine/synthesis/include/laplace/synthesis/arch_template.h for
    /// the SubstrateView fields the template consumes.
    /// Returns 0 success, -1 null input, -2 shape/template mismatch, -3 substrate
    /// view incompatibility.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "arch_template_materialize_tensor")]
    public static unsafe partial int ArchTemplateMaterializeTensor(
        IntPtr tmpl, TensorSpec* spec, SubstrateView* view, void* outValues);

    // === Tensor decomposition (EXPORT-ONLY: re-export = SVD-factor consensus circuits
    //     into the target mold at the recipe rank; never used at ingest) ===

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
    /// Exact, deterministic, streaming, threshold-based QK token-relation scorer for one
    /// head: emits every (query,key) pair with |q_t·k_s| &gt; <paramref name="noiseFloor"/>
    /// (no top-k). f64 Neumaier-compensated, fixed order → bit-identical regardless of
    /// thread count or windowing. Processes query rows [<paramref name="q0"/>,
    /// <paramref name="q1"/>) into <paramref name="outPairs"/> (cap <paramref name="outCap"/>);
    /// sets <paramref name="overflow"/>=1 and keeps the largest whole-row prefix that fits
    /// (caller retries a smaller window). Returns pairs written, -1 bad args.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "compute_qk_pairs_above_threshold")]
    public static unsafe partial long ComputeQkPairsAboveThreshold(
        float* eF32, nuint vocab, nuint dModel,
        float* wqHead, float* wkHead, nuint headDim,
        double noiseFloor, nuint q0, nuint q1,
        QkPairF64* outPairs, nuint outCap, int* overflow);

    /// <summary>
    /// Sub-quadratic exact variant of <see cref="ComputeQkPairsAboveThreshold"/> — same
    /// params/result, but Cauchy-Schwarz norm-pruned (|q·k| ≤ ‖q‖·‖k‖) so the vast
    /// majority of pairs are provably skipped without scoring. Output is bit-identical to
    /// the all-pairs kernel (verified by ctest parity). Use this for ingestion.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "compute_qk_pairs_above_threshold_pruned")]
    public static unsafe partial long ComputeQkPairsAboveThresholdPruned(
        float* eF32, nuint vocab, nuint dModel,
        float* wqHead, float* wkHead, nuint headDim,
        double noiseFloor, nuint q0, nuint q1,
        QkPairF64* outPairs, nuint outCap, int* overflow);

    /// <summary>
    /// Project a layer's Q and K through the embedding ONCE for ALL heads (streams E a
    /// single time). q_cache layout [vocab][nHeads][headDim]; k_cache [vocab][nKv][headDim],
    /// row-major f64. Wq is [nHeads*headDim × dModel], Wk is [nKv*headDim × dModel] f32
    /// (HF output×input). The per-element compensated (Neumaier) projection in fixed order
    /// m=0..dModel-1 is identical to <see cref="ComputeQkPairsAboveThresholdPruned"/>, so a
    /// head scored from this cache is bit-identical to the pruned kernel. TBB-parallel across
    /// tokens; thread-count independent. Returns 0 ok, -1 bad args.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "project_qk_layer")]
    public static unsafe partial int ProjectQkLayer(
        float* eF32, nuint vocab, nuint dModel,
        float* wq, nuint nHeads,
        float* wk, nuint nKv,
        nuint headDim,
        double* qCacheOut, double* kCacheOut);

    /// <summary>
    /// Score ONE head purely from the caches built by <see cref="ProjectQkLayer"/> — no E
    /// re-streaming. Reads qCache[token][head] and kCache[token][kvHead], runs the IDENTICAL
    /// Cauchy-Schwarz norm-pruned scoring as <see cref="ComputeQkPairsAboveThresholdPruned"/>
    /// (key-norm sort / binary-search cutoff / ascending-key emit / whole-row overflow prefix).
    /// For a given (head, kvHead, floor, q0, q1) the emitted pairs, order, f64 score bits,
    /// count, and overflow are bit-identical to the pruned kernel for that head (verified by
    /// ctest + C# parity). Processes query rows [q0, q1) into outPairs (cap outCap); sets
    /// overflow=1 keeping the largest whole-row prefix that fits. Returns pairs written, -1 bad args.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "score_qk_head_cached")]
    public static unsafe partial long ScoreQkHeadCached(
        double* qCache, nuint nHeads,
        double* kCache, nuint nKv,
        nuint vocab, nuint headDim,
        nuint head, nuint kvHead,
        double floor, nuint q0, nuint q1,
        QkPairF64* outPairs, nuint outCap, int* overflow);

    // === Gram matrix precomputation ===

    /// <summary>
    /// Compute unary_gram = E^T·diag(perToken)·E and binary_gram = E^T·S_qk·E.
    /// Both outputs caller-allocated [basisDim × basisDim doubles].
    /// Requires MKL. Returns 0 ok, -1 null input, -2 no MKL.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "compute_substrate_gram")]
    public static unsafe partial int ComputeSubstrateGram(
        double* tokenBasis, double* perToken, nuint vocab, nuint basisDim,
        int* qkRows, int* qkCols, double* qkVals, nuint nnz,
        double* unaryGram, double* binaryGram);

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
/// Sparse (query, key, score) triplet with an f64 score from
/// <see cref="NativeInterop.ComputeQkPairsAboveThreshold"/>. Must match the C struct
/// qk_pair_f64_t layout (uint32, uint32, double — 16 bytes).
/// </summary>
[StructLayout(LayoutTypeId.Sequential)]
public struct QkPairF64
{
    public uint   QueryIdx;
    public uint   KeyIdx;
    public double Score;
}

/// <summary>
/// Tensor specification from <see cref="NativeInterop.ArchTemplateRequiredTensors"/>.
/// Must match the C struct tensor_spec_t layout.
/// </summary>
[StructLayout(LayoutTypeId.Sequential)]
public unsafe struct TensorSpec
{
    public byte*  Name;       /* points into arch_template_t internal storage */
    public ulong  Rank;
    public fixed ulong Shape[8];
    public int    Dtype;      /* 0=f32, 1=f16, 2=bf16 */
}

/// <summary>
/// Substrate consensus bundle consumed by
/// <see cref="NativeInterop.ArchTemplateMaterializeTensor"/>. Must match the
/// C struct substrate_view_t layout exactly.
/// the architecture template distributes these values across the recipe's
/// per-(layer, head, dim) layout — broadcast, NOT pseudoinverse.
/// </summary>
[StructLayout(LayoutTypeId.Sequential)]
public unsafe struct SubstrateView
{
    public double* PerTokenConsensus;
    public nuint   Vocab;
    public int*    PerPairRows;
    public int*    PerPairCols;
    public double* PerPairVals;
    public nuint   PerPairNnz;
    public double  NormAggregate;
    public double* TokenBasis;
    public nuint   BasisDim;
    public double* UnaryGram;    /* [basis_dim × basis_dim] E^T·diag(perToken)·E, or null */
    public double* BinaryGram;  /* [basis_dim × basis_dim] E^T·S_qk·E, or null */
}
