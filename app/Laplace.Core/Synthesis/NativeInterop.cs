using System.Runtime.InteropServices;

namespace Laplace.Engine.Synthesis;

public static partial class NativeInterop
{
    private const string Library = "laplace_synthesis";
    [StructLayout(LayoutKind.Sequential)]
    public struct BasisReceipt {
        public nuint Rank,Anchors;
        public double OrthogonalityError,SpanRelativeError,FrameOrthogonalityError,AnchorErrorBefore,AnchorErrorAfter;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct SpectralReceipt {
        public nuint ActiveVertices,Components,RequestedModes,RetainedModes,CombinedRank;
        public double EigenResidualMax,MassOrthogonalityError,DiscardedSvdEnergy;
    }
    [LibraryImport(Library,EntryPoint="laplace_basis_refine")]
    public static unsafe partial int BasisRefine(double* embedding,nuint rows,nuint width,nuint rank,double* coordinates,int* input,BasisReceipt* receipt);
    [LibraryImport(Library,EntryPoint="laplace_spectral_embed")]
    public static unsafe partial int SpectralEmbed(nuint rows,nuint width,int* input,double* coordinates,int* rowsIndex,int* colsIndex,double* weights,nuint count,double* embedding,nuint* features,SpectralReceipt* receipt);
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SourceObservation {public fixed byte SourceId[16];public fixed byte ClaimId[16];public long Games,Rating,Rd,OpponentRating,OpponentRd;}
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SourceShare {public fixed byte SourceId[16];public ulong ClaimsTouched,Games;public double FisherWeight;public uint ShareBp;}
    [LibraryImport(Library,EntryPoint="laplace_source_attribution")]
    public static unsafe partial int SourceAttribution(SourceObservation* observations,nuint count,SourceShare* shares,nuint capacity,nuint* written);
    [LibraryImport(Library, EntryPoint="laplace_program_create")]
    public static unsafe partial IntPtr ProgramCreate(nuint vocab,nuint width,double* embedding,int bos,int eos,double epsilon,double ropeBase,double ropeScale,double attentionGain,double residualGain,nuint layers);
    [LibraryImport(Library, EntryPoint="laplace_program_tie_embeddings")]
    public static partial int ProgramTieEmbeddings(IntPtr program,int tied);
    [LibraryImport(Library,EntryPoint="laplace_program_output_floor")]
    public static partial int ProgramOutputFloor(IntPtr program,double bound,int equalCandidates);
    [LibraryImport(Library,EntryPoint="laplace_program_observation_readout")]
    public static partial int ProgramObservationReadout(IntPtr program,int enabled);
    [LibraryImport(Library, EntryPoint="laplace_program_plane")]
    public static unsafe partial int ProgramPlane(IntPtr program,int* rows,int* cols,double* weights,nuint count);
    [LibraryImport(Library, EntryPoint="laplace_program_layer")]
    public static unsafe partial int ProgramLayer(IntPtr program,nuint heads,nuint kv,nuint intermediate,int* headOperators,int ffnOperator,int* expertOperators,nuint experts,nuint used);
    [LibraryImport(Library, EntryPoint="laplace_program_sequence")]
    public static unsafe partial int ProgramSequence(IntPtr program,int* tokens,nuint count,double weight);
    [LibraryImport(Library, EntryPoint="laplace_program_compile")]
    public static unsafe partial int ProgramCompile(IntPtr program,int readout,int* input,int* emit,
        delegate* unmanaged[Cdecl]<IntPtr,byte*,byte*,nuint*,nuint,float*,nuint,int> sink,
        delegate* unmanaged[Cdecl]<IntPtr,nuint,byte*,nuint,double,double,void> trace,IntPtr state);
    [LibraryImport(Library, EntryPoint="laplace_program_error")]
    public static partial IntPtr ProgramError(IntPtr program);
    [LibraryImport(Library, EntryPoint="laplace_program_states")]
    public static unsafe partial float* ProgramStates(IntPtr program,nuint* rows,nuint* width);
    [LibraryImport(Library, EntryPoint="laplace_program_nodes")]
    public static unsafe partial nuint ProgramNodes(IntPtr program,int** tokens,int** parents,int** positions);
    [LibraryImport(Library, EntryPoint="laplace_program_targets")]
    public static unsafe partial nuint ProgramTargets(IntPtr program,nuint** nodes,double** floors,nuint** offsets,int** columns,double** deltas);
    [LibraryImport(Library, EntryPoint="laplace_program_free")]
    public static partial void ProgramFree(IntPtr program);

    [LibraryImport(Library, EntryPoint="laplace_readout_fit")]
    public static unsafe partial int ReadoutFit(float* states,nuint vocab,nuint width,int* input,int* emit,int* rows,int* cols,double* weights,nuint nnz,float* head,nuint* rank,double* relativeError,double* accuracy);
    [LibraryImport(Library, EntryPoint="laplace_readout_embed")]
    public static unsafe partial int ReadoutEmbed(nuint vocab,nuint width,int* input,int* emit,int* rows,int* cols,double* weights,nuint nnz,double* coordinates,double* embedding,nuint* features);
    [LibraryImport(Library, EntryPoint="laplace_readout_embed_policy")]
    public static unsafe partial int ReadoutEmbedPolicy(nuint vocab,nuint width,int* input,int* emit,int* rows,int* cols,double* weights,nuint nnz,double* coordinates,double* embedding,nuint* features,double bound,int equalCandidates);
    [LibraryImport(Library, EntryPoint="laplace_graph_basis")]
    public static unsafe partial int GraphBasis(int* rows,int* cols,double* weights,nuint nnz,nuint vocab,nuint requested,double* output,nuint* rank);
    [LibraryImport(Library, EntryPoint="laplace_operator_apply")]
    public static unsafe partial int OperatorApply(double* basis,nuint vocab,nuint width,double* value,double scale,double* accumulator);
    [LibraryImport(Library, EntryPoint="laplace_operator_compile")]
    public static unsafe partial int OperatorCompile(double* basis,nuint vocab,nuint width,
        int* rows,int* cols,double* weights,nuint nnz,double* scores,double* values,
        nuint* rank,double* scoreError,double* valueError);

    [LibraryImport(Library, EntryPoint = "laplace_lookup_compile")]
    public static unsafe partial int LookupCompile(nuint vocab,nuint dim,int eos,double epsilon,
        int* inputOperand,int* operandOutput,int* rows,int* cols,double* weights,nuint nnz,double* embedding,double* output);


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

    // Unified tensor dtype codec — one native entry for every safetensors numeric/bool
    // dtype. Unknown names (block-quant containers) resolve to -1 so the caller refuses
    // rather than ingesting zeros.
    [LibraryImport(Library, EntryPoint = "laplace_tensor_dtype_from_name",
                   StringMarshalling = StringMarshalling.Utf8)]
    public static partial int TensorDtypeFromName(string name);

    [LibraryImport(Library, EntryPoint = "laplace_tensor_dtype_size")]
    public static partial nuint TensorDtypeSize(int dtype);

    [LibraryImport(Library, EntryPoint = "laplace_tensor_decode_f32")]
    public static unsafe partial int TensorDecodeF32(
        void* rawBytes, nuint nElements, int dtype, float* outValues);

    [LibraryImport(Library, EntryPoint = "recipe_parse")]
    public static unsafe partial IntPtr RecipeParse(byte* jsonText, nuint len);

    [LibraryImport(Library, EntryPoint = "recipe_get_field",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial IntPtr RecipeGetField(IntPtr recipe, string fieldName);

    // Typed reads over the same parsed recipe — callers must not re-parse config.json
    // in managed code to get a number out of it. Status: 0 ok, -1 null, -2 missing,
    // -3 present-but-not-that-type (never a silent default).
    [LibraryImport(Library, EntryPoint = "recipe_get_int",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int RecipeGetInt(IntPtr recipe, string fieldName, long* outValue);

    [LibraryImport(Library, EntryPoint = "recipe_get_double",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int RecipeGetDouble(IntPtr recipe, string fieldName, double* outValue);

    [LibraryImport(Library, EntryPoint = "recipe_free")]
    public static partial void RecipeFree(IntPtr recipe);

    // safetensors container header — [u64 LE json length][json]. Entries come back in
    // data-offset (on-disk) order. NULL on any malformed header: a file that does not
    // describe its own bytes must be refused, not half-read.
    [LibraryImport(Library, EntryPoint = "safetensors_parse_header")]
    public static unsafe partial IntPtr SafetensorsParseHeader(void* bytes, nuint len);

    [LibraryImport(Library, EntryPoint = "safetensors_header_bytes")]
    public static partial long SafetensorsHeaderBytes(IntPtr header);

    [LibraryImport(Library, EntryPoint = "safetensors_tensor_count")]
    public static partial int SafetensorsTensorCount(IntPtr header);

    [LibraryImport(Library, EntryPoint = "safetensors_has_metadata")]
    public static partial int SafetensorsHasMetadata(IntPtr header);

    [LibraryImport(Library, EntryPoint = "safetensors_tensor_name")]
    public static partial IntPtr SafetensorsTensorName(IntPtr header, int index);

    [LibraryImport(Library, EntryPoint = "safetensors_tensor_dtype")]
    public static partial IntPtr SafetensorsTensorDtype(IntPtr header, int index);

    [LibraryImport(Library, EntryPoint = "safetensors_tensor_rank")]
    public static partial int SafetensorsTensorRank(IntPtr header, int index);

    [LibraryImport(Library, EntryPoint = "safetensors_tensor_dim")]
    public static partial long SafetensorsTensorDim(IntPtr header, int index, int axis);

    [LibraryImport(Library, EntryPoint = "safetensors_tensor_data_start")]
    public static partial long SafetensorsTensorDataStart(IntPtr header, int index);

    [LibraryImport(Library, EntryPoint = "safetensors_tensor_data_end")]
    public static partial long SafetensorsTensorDataEnd(IntPtr header, int index);

    [LibraryImport(Library, EntryPoint = "safetensors_header_free")]
    public static partial void SafetensorsHeaderFree(IntPtr header);

    // SentencePiece ModelProto reader/writer. Piece bytes are carried verbatim — the
    // vocabulary is full of U+2581 and other multi-byte forms, and a lossy re-encode
    // would change token identity.
    [LibraryImport(Library, EntryPoint = "sp_model_parse")]
    public static unsafe partial IntPtr SpModelParse(void* bytes, nuint len);

    [LibraryImport(Library, EntryPoint = "sp_model_piece_count")]
    public static partial int SpModelPieceCount(IntPtr model);

    [LibraryImport(Library, EntryPoint = "sp_model_piece")]
    public static unsafe partial IntPtr SpModelPiece(IntPtr model, int index, nuint* outLen);

    [LibraryImport(Library, EntryPoint = "sp_model_score")]
    public static partial float SpModelScore(IntPtr model, int index);

    [LibraryImport(Library, EntryPoint = "sp_model_type")]
    public static partial int SpModelType(IntPtr model, int index);

    [LibraryImport(Library, EntryPoint = "sp_model_free")]
    public static partial void SpModelFree(IntPtr model);

    [LibraryImport(Library, EntryPoint = "sp_model_write")]
    public static unsafe partial int SpModelWrite(
        byte** pieces, nuint* pieceLens, float* scores, int* types, int count,
        byte** outBuf, nuint* outLen);

    [LibraryImport(Library, EntryPoint = "sp_model_buffer_free")]
    public static unsafe partial void SpModelBufferFree(byte* buf);

    // HF -> GGML tensor naming. Lives with the writer because it is format grammar:
    // a GGUF carrying HuggingFace names is a file no llama.cpp build will load.
    [LibraryImport(Library, EntryPoint = "gguf_tensor_name_hf_to_ggml",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int GgufTensorNameHfToGgml(string hfName, byte* outBuf, nuint outCap);

    [LibraryImport(Library, EntryPoint = "arch_template_load",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr ArchTemplateLoad(string templateName);

    [LibraryImport(Library, EntryPoint = "arch_template_required_tensors")]
    private static unsafe partial int ArchTemplateRequiredTensorsNative(
        IntPtr tmpl, IntPtr recipe, TensorSpec* outSpecs, nuint cap);

    /// <summary>
    /// Bounded ABI form. The C ABI reports the required count when <paramref name="cap"/>
    /// is too small and does not populate <paramref name="outSpecs"/> in that case, so a
    /// capacity shortfall is returned as -2 rather than as a positive count over unfilled specs.
    /// </summary>
    public static unsafe int ArchTemplateRequiredTensors(
        IntPtr tmpl, IntPtr recipe, TensorSpec* outSpecs, nuint cap)
    {
        int count = ArchTemplateRequiredTensorsNative(tmpl, recipe, outSpecs, cap);
        return count > 0 && (nuint)count > cap ? -2 : count;
    }

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
    public static partial IntPtr FormatWriterCreate(string format, string outputDirPath);

    [LibraryImport(Library, EntryPoint = "format_writer_add_tensor",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int FormatWriterAddTensor(
        IntPtr w, string name, int dtype,
        nuint* shape, nuint rank, void* data, nuint dataLen);

    [LibraryImport(Library, EntryPoint = "format_writer_set_config")]
    public static unsafe partial int FormatWriterSetConfig(IntPtr w, byte* configJson, nuint len);

    [LibraryImport(Library, EntryPoint = "format_writer_set_tokenizer")]
    public static unsafe partial int FormatWriterSetTokenizer(IntPtr w, byte* tokJson, nuint len);

    [LibraryImport(Library, EntryPoint = "format_writer_finalize")]
    public static partial int FormatWriterFinalize(IntPtr w);

    [LibraryImport(Library, EntryPoint = "format_writer_free")]
    public static partial void FormatWriterFree(IntPtr w);


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
