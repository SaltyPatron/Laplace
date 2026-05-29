using System.Runtime.InteropServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;    // QkPair, NativeInterop
using Laplace.SubstrateCRUD;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Universal weight-tensor ETL per ADR 0056. One algorithm; per-architecture-family
/// data drives the per-tensor reduction (math_function + kind_id + subject/object
/// axis mapping). Per ADR 0056:153 the per-family table is *data registered on
/// architecture-template entities*, not code per family. For Stream B-minimum this
/// file ships the Llama-family registry as a static dictionary; Stream B-complete
/// migrates that into substrate `TENSOR_NAME_MEANS_MECHANICAL_ROLE` attestations
/// per ADR 0043:66.
///
/// Phase 1: per-(i, j) matchup via spec.math_function on the tensor.
/// Phase 2: within-model aggregation (sum) across (layer, head, expert) instances
///          → one accumulator entry per (subject, kind, object).
/// Phase 3: lottery-ticket sparsity — per-kind top-k by aggregate magnitude.
/// Phase 5: emit one AttestationRow per (subject, kind, object, source) tuple
///          via AttestationFactory.Create with KindValueTier.T9 +
///          TrustClass.AiModelProbeTier7 priors. context = NULL per the ADR 0056
///          amendment + GLOSSARY explicit rule (per-position is recipe content).
///
/// Phase 4 (static-mathematical retention validator) is deferred; the per-kind
/// top-k acts as the noise floor for Stream B-minimum.
///
/// What this ETL is NOT (per ADR 0056 lines 217-225):
///   - container decomposition (ADR 0055 IContainerParser does that)
///   - dtype decoding (ADR 0043 TensorDtypeDecoder does that)
///   - vocab ingest (ADR 0043 ModalityBinder does that, BEFORE this runs)
///   - hashing (ADR 0048 HashComposer does that, BEFORE this runs)
///   - DB writes (ADR 0050 SubstrateCRUD does that, AFTER this yields)
///   - running the model (ADR 0055 — substrate doesn't load + doesn't execute)
/// </summary>
public sealed class WeightTensorETL
{
    private const int    QkPerRowCap  = 256;
    private const int    AttBatchSize = 4096;

    /* Noise floor: attestations with aggregate magnitude at or below this
     * value are not real observations — they are gradient jitter or training
     * artifacts that did not fire. True zeros are faster in every downstream
     * computation than near-zero floats, and the substrate should not record
     * non-relationships. The lottery ticket is what survives multi-model
     * Glicko-2 consensus, not a pre-selected top-k percentage from one model. */
    private const double NoiseFloor   = 1e-9;

    private readonly string _safetensorsPath;
    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly LlamaWeightExtractor.KindIds _kinds;
    private readonly Hash128 _sourceId;
    private readonly IReadOnlyList<SafetensorsContainerParser.TensorReference> _refs;

    public WeightTensorETL(
        string modelDir,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId,
        LlamaWeightExtractor.KindIds kinds)
    {
        _recipe          = recipe;
        _tokens          = tokens;
        _sourceId        = sourceId;
        _kinds           = kinds;
        _safetensorsPath = Path.Combine(modelDir, "model.safetensors");
        _refs            = SafetensorsContainerParser.ParseHeader(_safetensorsPath);
    }

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
        int interm    = _recipe.IntermediateSize;
        int kvDim     = nKvHeads * headDim;
        int attnOut   = nHeads * headDim;

        ushort[] E_bf16 = LoadRawBF16(refMap, "model.embed_tokens.weight",
                                       (long)vocabSize * dModel);

        // ─── EMBEDS (unary per token) ───────────────────────────────────
        // ADR 0056:163 — (text_entity, embed_dim), per-cell magnitude, one
        // instance. Reduce embed row to a per-token scalar via L2 norm of
        // the dModel-wide row. Emit one attestation per token, object NULL.
        var embedAccum = ReducePerCellMagnitude(E_bf16, vocabSize, dModel);
        foreach (var c in EmitUnaryBatches(embedAccum, _kinds.Embeds, "embeds"))
        { yield return c; await Task.Yield(); }

        // ─── OUTPUT_PROJECTS (unary per token) ──────────────────────────
        // ADR 0056:164 — (hidden_dim, text_entity), per-cell magnitude.
        // For lm_head with shape [vocab, hidden_dim] (Llama convention),
        // reduce per-row to a per-token scalar. Object NULL.
        if (refMap.ContainsKey("lm_head.weight"))
        {
            ushort[] lm = LoadRawBF16(refMap, "lm_head.weight",
                                       (long)vocabSize * dModel);
            var lmAccum = ReducePerCellMagnitude(lm, vocabSize, dModel);
            foreach (var c in EmitUnaryBatches(lmAccum, _kinds.OutputProjects, "output_projects"))
            { yield return c; await Task.Yield(); }
        }

        // ─── Q_PROJECTS (binary text×text) ──────────────────────────────
        // ADR 0056:157 — q_proj[i,:] · k_proj[j,:]ᵀ per (layer, head), aggregated.
        // Use the existing compute_static_qk_scores_batch C primitive: it
        // computes per-(query, key) top-k pairs for each (layer, head) in one
        // call. Accumulate (sum, count) per (i, j) across all layers' batches.
        var qkAccum = new Dictionary<(uint q, uint k), (double sum, int count)>(1 << 20);
        for (int layer = 0; layer < _recipe.NumLayers; layer++)
        {
            ct.ThrowIfCancellationRequested();
            string qName = $"model.layers.{layer}.self_attn.q_proj.weight";
            string kName = $"model.layers.{layer}.self_attn.k_proj.weight";
            if (!refMap.ContainsKey(qName) || !refMap.ContainsKey(kName)) continue;
            float[] qW = LoadRawBF16AsF32(refMap, qName, (long)nHeads * headDim * dModel);
            float[] kW = LoadRawBF16AsF32(refMap, kName, (long)nKvHeads * headDim * dModel);
            AccumulateQkScoresBatch(E_bf16, qW, kW, vocabSize, dModel, nHeads, nKvHeads, headDim, qkAccum);
            await Task.Yield();
        }
        foreach (var c in EmitBinaryBatches(qkAccum, _kinds.QProjects, "q_projects"))
        { yield return c; await Task.Yield(); }

        // ─── V / O / GATES / UP / DOWN — unary per token via per-row magnitude ───
        // ADR 0056:158-162 spec table: object axis is hidden_dim (V), hidden_dim (O),
        // hidden_dim (GATES), intermediate_dim (UP), intermediate_dim (DOWN); reduction
        // is per-cell magnitude. Per the corrected understanding the per-(layer, head,
        // dim) is recipe content (not substrate entities), so the substrate emission is
        // unary per token (object NULL) with per-instance L2 sums aggregated across
        // layers. Object axis ≠ token, so we reduce ALONG the dim axis to one value
        // per token.
        var perTokenAccum = new (Hash128 kindId, double[] perToken, string label)[5];
        perTokenAccum[0] = (_kinds.VProjects,    new double[vocabSize], "v_projects");
        perTokenAccum[1] = (_kinds.OProjects,    new double[vocabSize], "o_projects");
        perTokenAccum[2] = (_kinds.Gates,        new double[vocabSize], "gates");
        perTokenAccum[3] = (_kinds.UpProjects,   new double[vocabSize], "up_projects");
        perTokenAccum[4] = (_kinds.DownProjects, new double[vocabSize], "down_projects");

        for (int layer = 0; layer < _recipe.NumLayers; layer++)
        {
            ct.ThrowIfCancellationRequested();
            string p = $"model.layers.{layer}.";

            AggregateLayerThroughEmbed(refMap, p + "self_attn.v_proj.weight",
                kvDim, dModel, E_bf16, vocabSize, perTokenAccum[0].perToken);
            AggregateLayerThroughEmbed(refMap, p + "self_attn.o_proj.weight",
                dModel, attnOut, E_bf16, vocabSize, perTokenAccum[1].perToken);
            AggregateLayerThroughEmbed(refMap, p + "mlp.gate_proj.weight",
                interm, dModel, E_bf16, vocabSize, perTokenAccum[2].perToken);
            AggregateLayerThroughEmbed(refMap, p + "mlp.up_proj.weight",
                interm, dModel, E_bf16, vocabSize, perTokenAccum[3].perToken);
            AggregateLayerThroughEmbed(refMap, p + "mlp.down_proj.weight",
                dModel, interm, E_bf16, vocabSize, perTokenAccum[4].perToken);
            await Task.Yield();
        }

        for (int i = 0; i < perTokenAccum.Length; i++)
        {
            var (kindId, perToken, label) = perTokenAccum[i];
            foreach (var c in EmitUnaryBatches(perToken, kindId, label))
            { yield return c; await Task.Yield(); }
        }

        // ─── NORMALIZES — unary on model recipe entity ──────────────────
        // ADR 0056:165 — unary (hidden_dim,), per-cell magnitude across layers.
        // The per-(layer, role, dim) is recipe content; substrate emission is
        // ONE unary attestation on the model recipe entity carrying the
        // aggregate. Subject = recipe entity, object = NULL.
        double normAggregate = 0.0;
        int normCount = 0;
        for (int layer = 0; layer < _recipe.NumLayers; layer++)
        {
            foreach (var role in new[] {
                $"model.layers.{layer}.input_layernorm.weight",
                $"model.layers.{layer}.post_attention_layernorm.weight"
            })
            {
                if (!refMap.ContainsKey(role)) continue;
                float[] w = LoadRawBF16AsF32(refMap, role, dModel);
                for (int d = 0; d < dModel; d++) normAggregate += Math.Abs(w[d]);
                normCount += dModel;
            }
        }
        if (refMap.ContainsKey("model.norm.weight"))
        {
            float[] w = LoadRawBF16AsF32(refMap, "model.norm.weight", dModel);
            for (int d = 0; d < dModel; d++) normAggregate += Math.Abs(w[d]);
            normCount += dModel;
        }
        if (normCount > 0)
        {
            var b = new SubstrateChangeBuilder(_sourceId, "normalizes",
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 1);
            b.AddAttestation(AttestationFactory.Create(
                subject:   _recipe.RecipeEntityId,
                kindId:    _kinds.Normalizes,
                obj:       null,
                sourceId:  _sourceId,
                contextId: null,
                tier:      KindValueTier.T9,
                trust:     TrustClass.AiModelProbeTier7,
                observationCount: normCount));
            yield return b.Build();
        }

        // K_PROJECTS — Per ADR 0056:157 spec table, transformer attention collapses Q+K
        // joint via Q_PROJECTS; K_PROJECTS kind is bootstrapped for future architectures
        // (encoder-decoder cross-attn etc.) but transformer-family doesn't emit it
        // separately. No emission here for Llama-family.
    }

    /* ----------------------------------------------------------- *
     * Per-cell-magnitude reduction over a [vocab × dim] tensor:
     * returns one scalar per token (L2 norm of the row).
     * Used for EMBEDS + OUTPUT_PROJECTS (one-instance) and as the
     * base per-instance contribution for V/O/G/U/D aggregation.
     * ----------------------------------------------------------- */
    private static double[] ReducePerCellMagnitude(ushort[] tensorBf16, int rows, int cols)
    {
        var result = new double[rows];
        for (int r = 0; r < rows; r++)
        {
            double s = 0.0;
            long off = (long)r * cols;
            for (int c = 0; c < cols; c++)
            {
                uint bits = (uint)tensorBf16[off + c] << 16;
                float v = BitConverter.UInt32BitsToSingle(bits);
                s += (double)v * v;
            }
            result[r] = Math.Sqrt(s);
        }
        return result;
    }

    /* Aggregate one V/O/G/U/D-style layer through the embedding into a
     * per-token contribution: project E @ |W| → per-token magnitude (sum
     * across the hidden/intermediate axis). Add to the per-token accumulator.
     * Memory-bounded — bf16 tensor + accumulator both small relative to model. */
    private void AggregateLayerThroughEmbed(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string tensorName, int outDim, int inDim,
        ushort[] E_bf16, int vocab, double[] perTokenAccum)
    {
        if (!refMap.ContainsKey(tensorName)) return;
        float[] w = LoadRawBF16AsF32(refMap, tensorName, (long)outDim * inDim);

        // |W|[out, in] summed over out → per-input-dim magnitude vector
        var perInDim = new double[inDim];
        for (int o = 0; o < outDim; o++)
        {
            long off = (long)o * inDim;
            for (int i = 0; i < inDim; i++)
                perInDim[i] += Math.Abs(w[off + i]);
        }

        // E[vocab, dModel] @ perInDim → per-token contribution (when inDim == dModel)
        // For tensors with inDim != dModel (down_proj input is intermediate_dim),
        // we can't directly project through E; fall back to global magnitude sum
        // distributed uniformly across tokens. Documented Stream B-minimum
        // approximation; Stream B-complete uses materialize_tensor's recipe layout.
        if (inDim == E_bf16.Length / vocab)  // E columns match
        {
            for (int t = 0; t < vocab; t++)
            {
                double s = 0.0;
                long off = (long)t * inDim;
                for (int i = 0; i < inDim; i++)
                {
                    uint bits = (uint)E_bf16[off + i] << 16;
                    float v = BitConverter.UInt32BitsToSingle(bits);
                    s += (double)v * perInDim[i];
                }
                perTokenAccum[t] += Math.Abs(s);
            }
        }
        else
        {
            double total = 0.0;
            for (int i = 0; i < inDim; i++) total += perInDim[i];
            double perToken = total / vocab;
            for (int t = 0; t < vocab; t++) perTokenAccum[t] += perToken;
        }
    }

    /* Emit unary attestations (one per token, object NULL).
     * Records every token whose aggregate magnitude clears the noise floor.
     * Near-zero = no real observation = not recorded (true zero is correct). */
    private IEnumerable<SubstrateChange> EmitUnaryBatches(
        double[] perTokenAccum, Hash128 kindId, string unitName)
    {
        SubstrateChangeBuilder? b = null;
        int inBatch = 0;
        int batchIdx = 0;
        var seen = new HashSet<Hash128>();

        for (int tokenIdx = 0; tokenIdx < perTokenAccum.Length; tokenIdx++)
        {
            if (perTokenAccum[tokenIdx] <= NoiseFloor) continue;
            if (tokenIdx >= _tokens.Count) continue;
            var subj = _tokens[tokenIdx].EntityId;
            var row = AttestationFactory.Create(
                subj, kindId, obj: null, _sourceId, contextId: null,
                tier: KindValueTier.T9, trust: TrustClass.AiModelProbeTier7);
            if (!seen.Add(row.Id)) continue;

            b ??= new SubstrateChangeBuilder(_sourceId,
                $"{unitName}/batch-{batchIdx}",
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: AttBatchSize);
            b.AddAttestation(row);
            if (++inBatch >= AttBatchSize) { yield return b.Build(); b = null; inBatch = 0; batchIdx++; }
        }
        if (b != null) yield return b.Build();
    }

    /* Emit binary attestations (subject=token_i, object=token_j) for Q_PROJECTS.
     * Records every pair whose aggregate magnitude clears the noise floor —
     * positive (attends-to) and negative (repels) alike. Near-zero = no real
     * observation = not recorded. The lottery ticket is what survives
     * multi-model Glicko-2 consensus when additional models are ingested. */
    private IEnumerable<SubstrateChange> EmitBinaryBatches(
        Dictionary<(uint q, uint k), (double sum, int count)> accum,
        Hash128 kindId, string unitName)
    {
        SubstrateChangeBuilder? bb = null;
        int inBatch = 0;
        int batchIdx = 0;
        var seen = new HashSet<Hash128>();

        foreach (var ((qi, kj), (sum, count)) in accum)
        {
            if (count <= 0 || Math.Abs(sum) <= NoiseFloor) continue;
            if (qi >= (uint)_tokens.Count || kj >= (uint)_tokens.Count) continue;
            var subj = _tokens[(int)qi].EntityId;
            var obj  = _tokens[(int)kj].EntityId;
            var row  = AttestationFactory.Create(subj, kindId, obj, _sourceId, contextId: null,
                                                  tier: KindValueTier.T9, trust: TrustClass.AiModelProbeTier7,
                                                  observationCount: count);
            if (!seen.Add(row.Id)) continue;

            bb ??= new SubstrateChangeBuilder(_sourceId,
                $"{unitName}/batch-{batchIdx}",
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: AttBatchSize);
            bb.AddAttestation(row);
            if (++inBatch >= AttBatchSize) { yield return bb.Build(); bb = null; inBatch = 0; batchIdx++; }
        }
        if (bb != null) yield return bb.Build();
    }

    /* Q_PROJECTS per-(layer, head) accumulator via existing C primitive. */
    private void AccumulateQkScoresBatch(
        ushort[] E_bf16, float[] wqAll, float[] wkAll,
        int vocabSize, int dModel, int nHeads, int nKvHeads, int headDim,
        Dictionary<(uint q, uint k), (double sum, int count)> accum)
    {
        int queriesPerKv = nHeads / nKvHeads;
        long capPerHead = (long)vocabSize * QkPerRowCap;
        var pairsFlat = new QkPair[nHeads * capPerHead];
        var counts    = new int[nHeads];

        GCHandle eHandle     = GCHandle.Alloc(E_bf16, GCHandleType.Pinned);
        GCHandle wqHandle    = GCHandle.Alloc(wqAll,  GCHandleType.Pinned);
        GCHandle wkHandle    = GCHandle.Alloc(wkAll,  GCHandleType.Pinned);
        GCHandle pairsHandle = GCHandle.Alloc(pairsFlat, GCHandleType.Pinned);
        GCHandle cntHandle   = GCHandle.Alloc(counts,    GCHandleType.Pinned);
        try
        {
            unsafe
            {
                ushort* ePtr     = (ushort*)eHandle.AddrOfPinnedObject();
                float*  wqPtr    = (float*) wqHandle.AddrOfPinnedObject();
                float*  wkPtr    = (float*) wkHandle.AddrOfPinnedObject();
                QkPair* pairsPtr = (QkPair*)pairsHandle.AddrOfPinnedObject();
                int*    cntPtr   = (int*)   cntHandle.AddrOfPinnedObject();

                int rc = SynthInterop.ComputeStaticQkScoresBatch(
                    ePtr, (nuint)vocabSize, (nuint)dModel,
                    wqPtr, wkPtr,
                    (nuint)nHeads, (nuint)nKvHeads, (nuint)headDim,
                    (nuint)queriesPerKv, (nuint)QkPerRowCap,
                    pairsPtr, cntPtr, (nuint)capPerHead);
                if (rc != 0)
                    throw new InvalidOperationException($"compute_static_qk_scores_batch returned {rc}");

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

    /* Load helpers (lifted from the pre-Stream-A LlamaWeightExtractor). */
    private byte[] LoadRawBytes(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string name)
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

    private ushort[] LoadRawBF16(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string name, long expectedElements)
    {
        byte[] raw = LoadRawBytes(refMap, name);
        if (raw.Length != expectedElements * 2)
            throw new InvalidOperationException(
                $"BF16 size mismatch for {name}: got {raw.Length} bytes, expected {expectedElements * 2}");
        return MemoryMarshal.Cast<byte, ushort>(raw).ToArray();
    }

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
            }
        }
        return result;
    }
}
