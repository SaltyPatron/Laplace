using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;    // QkPair, TensorSpec
using Laplace.SubstrateCRUD;
using DynInterop   = Laplace.Engine.Dynamics.NativeInterop;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Streams BF16 weight tensors from a safetensors file, computes token-to-token
/// attestations via the C engine (MKL-backed DGEMM), applies lottery-ticket
/// sparsity, and yields SubstrateChange batches for each tensor role.
///
/// EMBEDS: uses compute_static_qk_scores with a rank-64 identity projection
/// (E[:,:64]·E[:,:64]^T) — token proximity in the leading 64 embedding dims.
/// O(n² × 64) via MKL DGEMM, ~1-2s for 32K vocab.
///
/// Q_PROJECTS: static QK scores E·Wq·Wk^T·E^T aggregated across all layers+heads.
///
/// All other roles (V, O, GATES, UP, DOWN, NORMALIZES, OUTPUT): sparsity applied
/// to the raw weight matrix. Token-to-token composition with E deferred (Chunk 7+).
/// </summary>
public sealed class LlamaWeightExtractor
{
    /* Lottery-ticket parameters per plan defaults. */
    private const double TopkPct    = 0.05;
    private const int    TopkPerRow = 32;

    /* Rank of the identity projection used for EMBEDS proximity. */
    private const int EmbedProjectDim = 64;

    /* Glicko-2 initial state for T9 model-weight attestation tier per STANDARDS. */
    private const long kRatingFp1e9     = 1_400_000_000_000L;
    private const long kRdFp1e9         =   350_000_000_000L;
    private const long kVolatilityFp1e9 =        60_000_000L;

    private readonly string _safetensorsPath;
    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<SafetensorsContainerParser.TensorReference> _refs;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly KindIds _kinds;
    private readonly Hash128 _sourceId;

    public sealed class KindIds
    {
        public required Hash128 Embeds         { get; init; }
        public required Hash128 QProjects      { get; init; }
        public required Hash128 KProjects      { get; init; }
        public required Hash128 VProjects      { get; init; }
        public required Hash128 OProjects      { get; init; }
        public required Hash128 Gates          { get; init; }
        public required Hash128 UpProjects     { get; init; }
        public required Hash128 DownProjects   { get; init; }
        public required Hash128 Normalizes     { get; init; }
        public required Hash128 OutputProjects { get; init; }
    }

    public LlamaWeightExtractor(
        string modelDir,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId,
        KindIds kinds)
    {
        _recipe          = recipe;
        _tokens          = tokens;
        _sourceId        = sourceId;
        _kinds           = kinds;
        _safetensorsPath = Path.Combine(modelDir, "model.safetensors");
        _refs            = SafetensorsContainerParser.ParseHeader(_safetensorsPath);
    }

    /// <summary>
    /// Yield SubstrateChange batches for all weight attestations.
    /// Order: embed_tokens → Q_PROJECTS (all layers aggregated) → remaining roles.
    /// </summary>
    public async IAsyncEnumerable<SubstrateChange> ExtractAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
            _refs.Count, StringComparer.Ordinal);
        foreach (var r in _refs) refMap[r.Name] = r;

        int vocabSize = _recipe.VocabSize;
        int dModel    = _recipe.HiddenSize;
        int nHeads    = _recipe.NumHeads;
        int nKvHeads  = _recipe.NumKvHeads;
        int headDim   = dModel / nHeads;

        /* --- Load embedding matrix E [vocabSize × dModel] as raw BF16 (2 bytes/element).
         * Never decoded to f64 — the C scorer decodes to f32 on demand per head.
         * This keeps peak memory at n_vocab × dModel × 2 bytes (256 MB for TinyLlama)
         * instead of × 8 bytes (1 GB f64). For a 200K-vocab model: 3.2 GB vs 13 GB. */
        ushort[] E_bf16 = LoadRawBF16(refMap, "model.embed_tokens.weight",
                                       (long)vocabSize * dModel);

        /* --- EMBEDS: token proximity via rank-EmbedProjectDim identity projection ---
         * Wq = Wk = I[:projDim, :dModel] (f32); treated as one synthetic head. */
        int projDim = Math.Min(EmbedProjectDim, dModel);
        float[] wqIdentityF32 = new float[(long)projDim * dModel];
        for (int h = 0; h < projDim; h++) wqIdentityF32[(long)h * dModel + h] = 1.0f;

        var qkAccumEmbed = new Dictionary<(uint q, uint k), (double sum, int count)>(1 << 20);
        AccumulateQkScoresBatch(E_bf16, wqIdentityF32, wqIdentityF32,
                                vocabSize, dModel, 1, 1, projDim, qkAccumEmbed);
        yield return BuildQkAttestations(qkAccumEmbed, vocabSize, _kinds.Embeds, "embed_tokens");
        await Task.Yield();

        /* --- Q_PROJECTS: aggregate QK scores across all layers, all heads --- */
        var qkAccum = new Dictionary<(uint q, uint k), (double sum, int count)>(1 << 20);

        for (int layer = 0; layer < _recipe.NumLayers; layer++)
        {
            ct.ThrowIfCancellationRequested();
            string qName = $"model.layers.{layer}.self_attn.q_proj.weight";
            string kName = $"model.layers.{layer}.self_attn.k_proj.weight";
            if (!refMap.ContainsKey(qName) || !refMap.ContainsKey(kName)) continue;

            /* Load all Q heads ([nHeads × headDim × dModel]) and all KV heads stacked.
             * The batch C function handles GQA grouping internally via queriesPerKv. */
            float[] qWeightF32 = LoadRawBF16AsF32(refMap, qName, (long)nHeads * headDim * dModel);
            float[] kWeightF32 = LoadRawBF16AsF32(refMap, kName, (long)nKvHeads * headDim * dModel);
            AccumulateQkScoresBatch(E_bf16, qWeightF32, kWeightF32,
                                    vocabSize, dModel, nHeads, nKvHeads, headDim, qkAccum);
            await Task.Yield();
        }

        yield return BuildQkAttestations(qkAccum, vocabSize, _kinds.QProjects, "q_projects_aggregated");

        /* --- Remaining roles per layer --- */
        for (int layer = 0; layer < _recipe.NumLayers; layer++)
        {
            ct.ThrowIfCancellationRequested();
            yield return ExtractLayerRoles(refMap, layer, vocabSize, dModel);
            await Task.Yield();
        }

        /* OUTPUT_PROJECTS (lm_head) */
        if (refMap.ContainsKey("lm_head.weight"))
        {
            double[] lmHead = LoadAndDecodeF64(refMap, "lm_head.weight", (long)vocabSize * dModel);
            yield return BuildRoleAttestations(lmHead, vocabSize, dModel, _kinds.OutputProjects, "lm_head");
        }
    }

    /* ------------------------------------------------------------------ */

    /* Batch QK score accumulator.  Calls the TBB-parallel C batch function,
     * which processes all nHeads heads simultaneously with one BF16→f32 decode
     * of E.  All heads share the same E_f32 buffer read-only inside C.
     *
     * For the EMBEDS case, nHeads=1, nKvHeads=1, and wqAll/wkAll each hold
     * a single [headDim × dModel] identity projection block. */
    private void AccumulateQkScoresBatch(
        ushort[] E_bf16, float[] wqAll, float[] wkAll,
        int vocabSize, int dModel, int nHeads, int nKvHeads, int headDim,
        Dictionary<(uint q, uint k), (double sum, int count)> accum)
    {
        int queriesPerKv = nHeads / nKvHeads;

        /* Pre-allocate output: [nHeads × vocabSize × TopkPerRow] QkPairs.
         * Each head writes at offset h * capPerHead into the flat array. */
        long capPerHead = (long)vocabSize * TopkPerRow;
        var  pairsFlat  = new QkPair[nHeads * capPerHead];
        var  counts     = new int[nHeads];

        GCHandle eHandle     = GCHandle.Alloc(E_bf16, GCHandleType.Pinned);
        GCHandle wqHandle    = GCHandle.Alloc(wqAll,  GCHandleType.Pinned);
        GCHandle wkHandle    = GCHandle.Alloc(wkAll,  GCHandleType.Pinned);
        GCHandle pairsHandle = GCHandle.Alloc(pairsFlat, GCHandleType.Pinned);
        GCHandle cntHandle   = GCHandle.Alloc(counts,    GCHandleType.Pinned);
        try
        {
            unsafe
            {
                ushort* ePtr    = (ushort*)eHandle.AddrOfPinnedObject();
                float*  wqPtr   = (float*) wqHandle.AddrOfPinnedObject();
                float*  wkPtr   = (float*) wkHandle.AddrOfPinnedObject();
                QkPair* pairsPtr = (QkPair*)pairsHandle.AddrOfPinnedObject();
                int*    cntPtr   = (int*)   cntHandle.AddrOfPinnedObject();

                int rc = SynthInterop.ComputeStaticQkScoresBatch(
                    ePtr, (nuint)vocabSize, (nuint)dModel,
                    wqPtr, wkPtr,
                    (nuint)nHeads, (nuint)nKvHeads, (nuint)headDim,
                    (nuint)queriesPerKv, (nuint)TopkPerRow,
                    pairsPtr, cntPtr, (nuint)capPerHead);

                if (rc != 0)
                    throw new InvalidOperationException($"compute_static_qk_scores_batch returned {rc}");

                /* Merge all heads' results into accum dictionary. */
                for (int h = 0; h < nHeads; h++)
                {
                    long offset = (long)h * capPerHead;
                    int  n      = counts[h];
                    for (int pi = 0; pi < n; pi++)
                    {
                        var p   = pairsFlat[offset + pi];
                        var key = (p.QueryIdx, p.KeyIdx);
                        if (accum.TryGetValue(key, out var existing))
                            accum[key] = (existing.sum + p.Score, existing.count + 1);
                        else
                            accum[key] = (p.Score, 1);
                    }
                }
            }
        }
        finally
        {
            eHandle.Free(); wqHandle.Free(); wkHandle.Free();
            pairsHandle.Free(); cntHandle.Free();
        }
    }

    private SubstrateChange BuildQkAttestations(
        Dictionary<(uint q, uint k), (double sum, int count)> accum,
        int vocabSize, Hash128 kindId, string unitName)
    {
        var means = new (uint q, uint k, double mean)[accum.Count];
        int idx = 0;
        foreach (var (key, (sum, count)) in accum)
            means[idx++] = (key.q, key.k, sum / count);

        /* Per-tensor top-k% */
        int keepN = Math.Max(1, (int)(means.Length * TopkPct));
        if (keepN < means.Length)
        {
            Array.Sort(means, (a, b) => Math.Abs(b.mean).CompareTo(Math.Abs(a.mean)));
            means = means.AsSpan(0, keepN).ToArray();
        }

        /* Per-row top-k */
        var byRow = new Dictionary<uint, List<(uint k, double mean)>>(vocabSize);
        foreach (var (q, k, mean) in means)
        {
            if (!byRow.TryGetValue(q, out var list)) { list = []; byRow[q] = list; }
            list.Add((k, mean));
        }

        var survivors = new List<(uint q, uint k, float score)>(byRow.Count * TopkPerRow);
        foreach (var (q, list) in byRow)
        {
            list.Sort((a, b) => Math.Abs(b.mean).CompareTo(Math.Abs(a.mean)));
            int take = Math.Min(TopkPerRow, list.Count);
            for (int i = 0; i < take; i++)
                survivors.Add((q, list[i].k, (float)list[i].mean));
        }

        return BuildPairAttestations(survivors, kindId, unitName);
    }

    private SubstrateChange ExtractLayerRoles(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        int layer, int vocabSize, int dModel)
    {
        var b = new SubstrateChangeBuilder(_sourceId, $"layer/{layer}/roles",
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 256);

        int nKvDim = _recipe.NumKvHeads * (dModel / _recipe.NumHeads);
        int intrm  = _recipe.IntermediateSize;

        void AddRole(string name, Hash128 kind, int rows, int cols)
        {
            if (!refMap.ContainsKey(name)) return;
            double[] w = LoadAndDecodeF64(refMap, name, (long)rows * cols);
            AddWeightAttestations(b, w, rows, cols, kind);
        }

        AddRole($"model.layers.{layer}.self_attn.v_proj.weight", _kinds.VProjects,    nKvDim, dModel);
        AddRole($"model.layers.{layer}.self_attn.o_proj.weight", _kinds.OProjects,    dModel, dModel);
        AddRole($"model.layers.{layer}.mlp.gate_proj.weight",    _kinds.Gates,        intrm,  dModel);
        AddRole($"model.layers.{layer}.mlp.up_proj.weight",      _kinds.UpProjects,   intrm,  dModel);
        AddRole($"model.layers.{layer}.mlp.down_proj.weight",    _kinds.DownProjects, dModel, intrm);

        AddNormRole(b, refMap, $"model.layers.{layer}.input_layernorm.weight",          dModel);
        AddNormRole(b, refMap, $"model.layers.{layer}.post_attention_layernorm.weight", dModel);

        return b.Build();
    }

    private void AddNormRole(
        SubstrateChangeBuilder b,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string name, int size)
    {
        if (!refMap.ContainsKey(name)) return;
        double[] w = LoadAndDecodeF64(refMap, name, size);

        unsafe
        {
            byte[] mask = new byte[size];
            fixed (double* wPtr = w)
            fixed (byte*   mPtr = mask)
                DynInterop.SparsityPerTensorTopkStreaming(wPtr, (nuint)size, TopkPct, mPtr);

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            for (int i = 0; i < size && i < _tokens.Count; i++)
            {
                if (mask[i] == 0) continue;
                var subj  = _tokens[i].EntityId;
                var attId = LlamaRecipeExtractor.ComputeAttestationId(subj, _kinds.Normalizes, subj, _sourceId);
                b.AddAttestation(new AttestationRow(
                    Id: attId, SubjectId: subj, KindId: _kinds.Normalizes, ObjectId: subj,
                    SourceId: _sourceId, ContextId: null,
                    RatingFp1e9: ScaleToRating(w[i]),
                    RdFp1e9: kRdFp1e9, VolatilityFp1e9: kVolatilityFp1e9,
                    LastObservedAtUnixUs: now, ObservationCount: 1));
            }
        }
    }

    private SubstrateChange BuildRoleAttestations(
        double[] weight, int rows, int cols, Hash128 kindId, string unitName)
    {
        var b = new SubstrateChangeBuilder(_sourceId, $"weight/{unitName}",
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 1024);
        AddWeightAttestations(b, weight, rows, cols, kindId);
        return b.Build();
    }

    private void AddWeightAttestations(
        SubstrateChangeBuilder b, double[] weight, int rows, int cols, Hash128 kindId)
    {
        int safeRows = Math.Min(rows, _tokens.Count);
        int safeCols = Math.Min(cols, _tokens.Count);
        if (safeRows == 0 || safeCols == 0) return;

        unsafe
        {
            byte[] mask = new byte[(long)safeRows * safeCols];
            fixed (double* wPtr = weight)
            fixed (byte*   mPtr = mask)
            {
                DynInterop.SparsityPerTensorTopkStreaming(
                    wPtr, (nuint)((long)safeRows * safeCols), TopkPct, mPtr);
                DynInterop.SparsityPerRowTopkStreaming(
                    wPtr, (nuint)safeRows, (nuint)safeCols, (nuint)TopkPerRow, mPtr);
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            for (int r = 0; r < safeRows; r++)
            {
                var subj = _tokens[r].EntityId;
                for (int c = 0; c < safeCols; c++)
                {
                    if (mask[(long)r * safeCols + c] == 0) continue;
                    var obj   = _tokens[c].EntityId;
                    var attId = LlamaRecipeExtractor.ComputeAttestationId(subj, kindId, obj, _sourceId);
                    b.AddAttestation(new AttestationRow(
                        Id: attId, SubjectId: subj, KindId: kindId, ObjectId: obj,
                        SourceId: _sourceId, ContextId: null,
                        RatingFp1e9: ScaleToRating(weight[(long)r * cols + c]),
                        RdFp1e9: kRdFp1e9, VolatilityFp1e9: kVolatilityFp1e9,
                        LastObservedAtUnixUs: now, ObservationCount: 1));
                }
            }
        }
    }

    private SubstrateChange BuildPairAttestations(
        IList<(uint i, uint j, float score)> pairs, Hash128 kindId, string unitName)
    {
        var b = new SubstrateChangeBuilder(_sourceId, $"pairs/{unitName}",
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: pairs.Count);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        foreach (var (qi, kj, score) in pairs)
        {
            if (qi >= _tokens.Count || kj >= _tokens.Count) continue;
            var subj  = _tokens[(int)qi].EntityId;
            var obj   = _tokens[(int)kj].EntityId;
            var attId = LlamaRecipeExtractor.ComputeAttestationId(subj, kindId, obj, _sourceId);
            b.AddAttestation(new AttestationRow(
                Id: attId, SubjectId: subj, KindId: kindId, ObjectId: obj,
                SourceId: _sourceId, ContextId: null,
                RatingFp1e9: ScaleToRating(score),
                RdFp1e9: kRdFp1e9, VolatilityFp1e9: kVolatilityFp1e9,
                LastObservedAtUnixUs: now, ObservationCount: 1));
        }
        return b.Build();
    }

    /* Load the raw tensor bytes from the safetensors file. */
    private byte[] LoadRawBytes(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string name)
    {
        var tref = refMap[name];
        byte[] rawBytes = new byte[tref.DataLength];
        using var fs = new FileStream(_safetensorsPath, FileMode.Open, FileAccess.Read,
                                      FileShare.Read, 1 << 16, useAsync: false);
        fs.Seek(tref.AbsoluteDataStart, SeekOrigin.Begin);
        int total = 0;
        while (total < rawBytes.Length)
        {
            int n = fs.Read(rawBytes, total, rawBytes.Length - total);
            if (n == 0) throw new IOException($"safetensors: truncated data for {name}");
            total += n;
        }
        return rawBytes;
    }

    /* Load a BF16 tensor as raw ushort[] without decoding.
     * The C scorer expects the native BF16 bytes — no intermediate f64. */
    private ushort[] LoadRawBF16(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string name, long expectedElements)
    {
        byte[] raw = LoadRawBytes(refMap, name);
        if (raw.Length != expectedElements * 2)
            throw new InvalidOperationException(
                $"BF16 size mismatch for {name}: got {raw.Length} bytes, expected {expectedElements * 2}");
        return System.Runtime.InteropServices.MemoryMarshal
               .Cast<byte, ushort>(raw).ToArray();
    }

    /* Load a BF16 or F32 tensor, decode to f32[], for Wq/Wk weight arrays.
     * These are small per-head (≤ d_model × head_dim × 4 bytes) so f32 is fine. */
    private float[] LoadRawBF16AsF32(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string name, long expectedElements)
    {
        var tref = refMap[name];
        byte[] raw = LoadRawBytes(refMap, name);
        float[] result = new float[expectedElements];
        unsafe
        {
            fixed (byte*  rawPtr = raw)
            fixed (float* outPtr = result)
            {
                if (tref.Dtype == "BF16")
                {
                    ushort* src = (ushort*)rawPtr;
                    for (long i = 0; i < expectedElements; i++)
                    {
                        uint bits = (uint)src[i] << 16;
                        float f;
                        Buffer.MemoryCopy(&bits, &f, 4, 4);
                        outPtr[i] = f;
                    }
                }
                else if (tref.Dtype == "F32")
                {
                    Buffer.MemoryCopy(rawPtr, outPtr, expectedElements * 4, raw.Length);
                }
                /* Other dtypes → zero-filled (R4) */
            }
        }
        return result;
    }

    /* Load a tensor as f64[] — used for non-QK roles (v_proj, gate, etc.)
     * where the full row-major weight is needed for sparsity filtering. */
    private double[] LoadAndDecodeF64(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string name, long expectedElements)
    {
        var tref = refMap[name];
        byte[] rawBytes = LoadRawBytes(refMap, name);
        double[] decoded = new double[expectedElements];
        unsafe
        {
            fixed (byte*   rawPtr = rawBytes)
            fixed (double* outPtr = decoded)
            {
                if (tref.Dtype == "BF16")
                    SynthInterop.Bf16Decode(rawPtr, (nuint)expectedElements, outPtr);
                else if (tref.Dtype == "F32")
                {
                    float* f32 = (float*)rawPtr;
                    for (long i = 0; i < expectedElements; i++) outPtr[i] = f32[i];
                }
                /* Other dtypes → zero-filled (R4) */
            }
        }
        return decoded;
    }

    /* Sigmoid-based weight magnitude → Glicko-2 rating [1000, 1800] FP×1e9 */
    private static long ScaleToRating(double weight)
    {
        double scaled = 1000.0 + 800.0 * (1.0 - 1.0 / (1.0 + Math.Abs(weight) * 10.0));
        return (long)(Math.Clamp(scaled, 1000.0, 1800.0) * 1e9);
    }
}
