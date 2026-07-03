using System.Runtime.InteropServices;

namespace Laplace.Engine.Synthesis;

public static partial class NativeInterop
{
    private const string Library = "laplace_synthesis";

    [LibraryImport(Library, EntryPoint = "laplace_synthesis_version")]
    private static partial IntPtr LaplaceSynthesisVersionPtr();

    public static string LaplaceSynthesisVersion() =>
        Marshal.PtrToStringUTF8(LaplaceSynthesisVersionPtr()) ?? string.Empty;

    [LibraryImport(Library, EntryPoint = "laplace_synthesis_init")]
    public static partial int LaplaceSynthesisInit();

    static NativeInterop()
    {
        _ = LaplaceSynthesisInit();
    }

    [LibraryImport(Library, EntryPoint = "laplace_bf16_decode")]
    public static unsafe partial int Bf16Decode(void* rawBytes, nuint nElements, double* outValues);

    [LibraryImport(Library, EntryPoint = "laplace_f32_gather_to_f64")]
    public static unsafe partial int F32GatherToF64(
        float* src, int* rowMap, nuint nRows, nuint d, double* outValues);

    [LibraryImport(Library, EntryPoint = "recipe_parse")]
    public static unsafe partial IntPtr RecipeParse(byte* jsonText, nuint len);

    [LibraryImport(Library, EntryPoint = "recipe_get_field",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial IntPtr RecipeGetField(IntPtr recipe, string fieldName);

    [LibraryImport(Library, EntryPoint = "recipe_free")]
    public static partial void RecipeFree(IntPtr recipe);

    [LibraryImport(Library, EntryPoint = "arch_template_load",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr ArchTemplateLoad(string templateName);

    [LibraryImport(Library, EntryPoint = "arch_template_required_tensors")]
    public static unsafe partial int ArchTemplateRequiredTensors(
        IntPtr tmpl, IntPtr recipe, TensorSpec* outSpecs, nuint cap);

    [LibraryImport(Library, EntryPoint = "arch_template_free")]
    public static partial void ArchTemplateFree(IntPtr tmpl);

    [LibraryImport(Library, EntryPoint = "arch_template_materialize_tensor")]
    public static unsafe partial int ArchTemplateMaterializeTensor(
        IntPtr tmpl, TensorSpec* spec, SubstrateView* view, void* outValues);

    [LibraryImport(Library, EntryPoint = "tensor_svd_truncate")]
    public static unsafe partial int TensorSvdTruncate(
        float* A, nuint m, nuint n,
        double relErrTol,
        nuint* outRank,
        float* U, float* S, float* Vt, nuint kmax);

    [LibraryImport(Library, EntryPoint = "compute_qk_pairs_above_threshold")]
    public static unsafe partial long ComputeQkPairsAboveThreshold(
        float* eF32, nuint vocab, nuint dModel,
        float* wqHead, float* wkHead, nuint headDim,
        double noiseFloor, nuint q0, nuint q1,
        QkPairF64* outPairs, nuint outCap, int* overflow);

    [LibraryImport(Library, EntryPoint = "compute_qk_pairs_above_threshold_pruned")]
    public static unsafe partial long ComputeQkPairsAboveThresholdPruned(
        float* eF32, nuint vocab, nuint dModel,
        float* wqHead, float* wkHead, nuint headDim,
        double noiseFloor, nuint q0, nuint q1,
        QkPairF64* outPairs, nuint outCap, int* overflow);

    [LibraryImport(Library, EntryPoint = "project_qk_layer")]
    public static unsafe partial int ProjectQkLayer(
        float* eF32, nuint vocab, nuint dModel,
        float* wq, nuint nHeads,
        float* wk, nuint nKv,
        nuint headDim,
        double* qCacheOut, double* kCacheOut);

    [LibraryImport(Library, EntryPoint = "score_qk_head_cached")]
    public static unsafe partial long ScoreQkHeadCached(
        double* qCache, nuint nHeads,
        double* kCache, nuint nKv,
        nuint vocab, nuint headDim,
        nuint head, nuint kvHead,
        double floor, nuint q0, nuint q1,
        QkPairF64* outPairs, nuint outCap, int* overflow);

    [LibraryImport(Library, EntryPoint = "compute_substrate_gram")]
    public static unsafe partial int ComputeSubstrateGram(
        double* tokenBasis, double* perToken, nuint vocab, nuint basisDim,
        int* qkRows, int* qkCols, double* qkVals, nuint nnz,
        double* unaryGram, double* binaryGram);

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


    [LibraryImport(Library, EntryPoint = "feature_extractor_load",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr FeatureExtractorLoad(string extractorName);

    [LibraryImport(Library, EntryPoint = "feature_extractor_output_dim")]
    public static partial nuint FeatureExtractorOutputDim(IntPtr fe);

    [LibraryImport(Library, EntryPoint = "feature_extractor_extract")]
    public static unsafe partial int FeatureExtractorExtract(
        IntPtr fe, byte* entityHash, double* outFeatures, nuint outDim);

    [LibraryImport(Library, EntryPoint = "feature_extractor_free")]
    public static partial void FeatureExtractorFree(IntPtr fe);
}

[StructLayout(LayoutKind.Sequential)]
public struct QkPairF64
{
    public uint QueryIdx;
    public uint KeyIdx;
    public double Score;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TensorSpec
{
    public byte* Name;
    public ulong Rank;
    public fixed ulong Shape[8];
    public int Dtype;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SubstrateView
{
    public double* PerTokenConsensus;
    public nuint Vocab;
    public int* PerPairRows;
    public int* PerPairCols;
    public double* PerPairVals;
    public nuint PerPairNnz;
    public double NormAggregate;
    public double* TokenBasis;
    public nuint BasisDim;
    public double* UnaryGram;
    public double* BinaryGram;
}
