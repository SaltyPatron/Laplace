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
    internal static unsafe partial IntPtr RecipeParse(byte* jsonText, nuint len);

    [LibraryImport(Library, EntryPoint = "recipe_get_field",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial IntPtr RecipeGetField(IntPtr recipe, string fieldName);

    [LibraryImport(Library, EntryPoint = "recipe_free")]
    internal static partial void RecipeFree(IntPtr recipe);

    // === Architecture template ===

    [LibraryImport(Library, EntryPoint = "arch_template_load",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr ArchTemplateLoad(string templateName);

    [LibraryImport(Library, EntryPoint = "arch_template_required_tensors")]
    internal static unsafe partial int ArchTemplateRequiredTensors(
        IntPtr tmpl, IntPtr recipe, TensorSpec* outSpecs, nuint cap);

    [LibraryImport(Library, EntryPoint = "arch_template_free")]
    internal static partial void ArchTemplateFree(IntPtr tmpl);

    // === Static QK attention scorer ===

    /// <summary>
    /// Compute per-row top-k token-to-token static QK scores for one attention head.
    /// Wq and Wk are [head_dim × d_model] row-major (HuggingFace output×input convention).
    /// outPairs: caller-allocated array of at least n_vocab*topkPerRow entries.
    /// Returns number of pairs written, or -1 on error.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "compute_static_qk_scores")]
    public static unsafe partial int ComputeStaticQkScores(
        double* E, nuint nVocab, nuint dModel,
        double* Wq, double* Wk, nuint headDim,
        nuint topkPerRow,
        QkPair* outPairs, nuint outCap);

    // === GGUF writer ===

    [LibraryImport(Library, EntryPoint = "gguf_writer_create",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GgufWriterCreate(string outputPath);

    [LibraryImport(Library, EntryPoint = "gguf_writer_add_metadata_str",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int GgufWriterAddMetadataStr(IntPtr w, string key, string value);

    [LibraryImport(Library, EntryPoint = "gguf_writer_add_metadata_u32",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int GgufWriterAddMetadataU32(IntPtr w, string key, uint value);

    [LibraryImport(Library, EntryPoint = "gguf_writer_add_tensor",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int GgufWriterAddTensor(
        IntPtr w, string name, int dtype,
        nuint* shape, nuint rank, void* data);

    [LibraryImport(Library, EntryPoint = "gguf_writer_finalize")]
    internal static partial int GgufWriterFinalize(IntPtr w);

    [LibraryImport(Library, EntryPoint = "gguf_writer_free")]
    internal static partial void GgufWriterFree(IntPtr w);

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
