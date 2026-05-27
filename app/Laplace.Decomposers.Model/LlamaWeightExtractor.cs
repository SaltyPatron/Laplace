using System.Runtime.InteropServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;    // QkPair
using Laplace.SubstrateCRUD;
using DynamicsInterop = Laplace.Engine.Dynamics.NativeInterop;
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

    /* Attestations per emitted SubstrateChange — one COPY/transaction per batch.
     * A whole role's survivor set (tens of thousands to ~1M rows) in a single COPY
     * exceeds the Npgsql command timeout; stream in bounded batches like the vocab path. */
    private const int AttBatchSize = 4096;

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

        int kvDim  = nKvHeads * headDim;
        int interm = _recipe.IntermediateSize;
        int attnOut = nHeads * headDim;   // o_proj output dim (== dModel for square models)

        /* Embedding matrix E [vocabSize × dModel] as raw BF16 — shared read-only by every
         * scorer (decoded to f32 once inside each C call). */
        ushort[] E_bf16 = LoadRawBF16(refMap, "model.embed_tokens.weight",
                                       (long)vocabSize * dModel);

        /* embed_tokens → per-token PROJECTION physicality (ADR 0056 corrected codec).
         * The embedding row is the model's per-token address in N-d; we align
         * the N-d source frame onto each token entity's Unicode-canonical
         * CONTENT 4D anchor via the dynamics pipeline (Procrustes fit on
         * single/multi-codepoint tokens whose CONTENT physicalities were
         * emitted by TextDecomposer through LlamaTokenizerParser). Each
         * token gets one PROJECTION physicality row at substrate-canonical
         * 4D — not a per-cell attestation, not a feature-dim relation. */
        foreach (var c in BuildEmbeddingPhysicalityBatches(E_bf16, vocabSize, dModel))
        {
            yield return c;
            await Task.Yield();
        }

        /* Q_PROJECTS: static QK bilinear E·Wq·Wkᵀ·Eᵀ, aggregated across all layers/heads. */
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
        foreach (var c in BuildQkAttestationBatches(qkAccum, vocabSize, _kinds.QProjects, "q_projects")) { yield return c; await Task.Yield(); }

        /* Interior tensors V / O / GATES / UP / DOWN — token×token via self-bilinear
         * E·W·Wᵀ·Eᵀ per ADR 0056 corrected codec (per ADR 0044 Part C: tokens are
         * substrate entities; non-token "feature dim" entities were conventional-AI
         * smuggling). Reuses compute_static_qk_scores via AccumulateQkScoresBatch with
         * Wq = Wk = W (the QK formula collapses to self-similarity in W's projection
         * space). Within-model aggregated across layers via the shared (uint q, uint k)
         * → (sum, count) accumulator per ADR 0056 Phase 2. */
        var vAccum  = new Dictionary<(uint q, uint k), (double sum, int count)>(1 << 20);
        var oAccum  = new Dictionary<(uint q, uint k), (double sum, int count)>(1 << 20);
        var gAccum  = new Dictionary<(uint q, uint k), (double sum, int count)>(1 << 20);
        var uAccum  = new Dictionary<(uint q, uint k), (double sum, int count)>(1 << 20);
        var dnAccum = new Dictionary<(uint q, uint k), (double sum, int count)>(1 << 20);

        for (int layer = 0; layer < _recipe.NumLayers; layer++)
        {
            ct.ThrowIfCancellationRequested();
            string p = $"model.layers.{layer}.";

            if (refMap.ContainsKey(p + "self_attn.v_proj.weight"))
            {
                /* v_proj is [kvDim × dModel] — single self-bilinear head over the full
                 * value-projection space (within-model aggregation collapses kv-head
                 * structure into the per-(token, kind, token) row anyway). */
                float[] w = LoadRawBF16AsF32(refMap, p + "self_attn.v_proj.weight", (long)kvDim * dModel);
                AccumulateQkScoresBatch(E_bf16, w, w, vocabSize, dModel,
                    nHeads: 1, nKvHeads: 1, headDim: kvDim, vAccum);
            }
            if (refMap.ContainsKey(p + "self_attn.o_proj.weight"))
            {
                /* o_proj is [dModel × attnOut]; transpose to [attnOut × dModel] for
                 * the head-structured layout AccumulateQkScoresBatch expects. */
                float[] w = LoadRawBF16AsF32(refMap, p + "self_attn.o_proj.weight", (long)dModel * attnOut);
                float[] wT = Transpose(w, dModel, attnOut);
                AccumulateQkScoresBatch(E_bf16, wT, wT, vocabSize, dModel,
                    nHeads: 1, nKvHeads: 1, headDim: attnOut, oAccum);
            }
            if (refMap.ContainsKey(p + "mlp.gate_proj.weight"))
            {
                float[] w = LoadRawBF16AsF32(refMap, p + "mlp.gate_proj.weight", (long)interm * dModel);
                AccumulateQkScoresBatch(E_bf16, w, w, vocabSize, dModel,
                    nHeads: 1, nKvHeads: 1, headDim: interm, gAccum);
            }
            if (refMap.ContainsKey(p + "mlp.up_proj.weight"))
            {
                float[] w = LoadRawBF16AsF32(refMap, p + "mlp.up_proj.weight", (long)interm * dModel);
                AccumulateQkScoresBatch(E_bf16, w, w, vocabSize, dModel,
                    nHeads: 1, nKvHeads: 1, headDim: interm, uAccum);
            }
            if (refMap.ContainsKey(p + "mlp.down_proj.weight"))
            {
                /* down_proj is [dModel × interm]; transpose to [interm × dModel]. */
                float[] w = LoadRawBF16AsF32(refMap, p + "mlp.down_proj.weight", (long)dModel * interm);
                float[] wT = Transpose(w, dModel, interm);
                AccumulateQkScoresBatch(E_bf16, wT, wT, vocabSize, dModel,
                    nHeads: 1, nKvHeads: 1, headDim: interm, dnAccum);
            }
            await Task.Yield();
        }

        foreach (var c in BuildQkAttestationBatches(vAccum,  vocabSize, _kinds.VProjects,    "v_projects"))    { yield return c; await Task.Yield(); }
        foreach (var c in BuildQkAttestationBatches(oAccum,  vocabSize, _kinds.OProjects,    "o_projects"))    { yield return c; await Task.Yield(); }
        foreach (var c in BuildQkAttestationBatches(gAccum,  vocabSize, _kinds.Gates,        "gates"))         { yield return c; await Task.Yield(); }
        foreach (var c in BuildQkAttestationBatches(uAccum,  vocabSize, _kinds.UpProjects,   "up_projects"))   { yield return c; await Task.Yield(); }
        foreach (var c in BuildQkAttestationBatches(dnAccum, vocabSize, _kinds.DownProjects, "down_projects")) { yield return c; await Task.Yield(); }

        /* OUTPUT_PROJECTS + lm_head: lm_head is [vocab × dModel]; the self-bilinear
         * E·lm_head·lm_headᵀ·Eᵀ would materialize a vocab×vocab head — too large for
         * the current primitive's per-head buffer. Deferred to a follow-up that emits
         * lm_head as a second PROJECTION-class physicality (output-direction) via the
         * same Procrustes path the embedding row uses. Schema needs a fourth
         * PhysicalityKind value (or sub-typing convention) to coexist with the
         * input-direction PROJECTION physicality already emitted above. */

        /* NORMALIZES (per-dim layernorm scale) deferred — recipe-level metadata, not
         * a token-pair claim. Future emission would attach to the recipe entity. */
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

    /* Token×token attestation emission via AttestationFactory (ADR 0044 priors).
     * Drives Q_PROJECTS + the corrected V/O/GATES/UP/DOWN extractions. Routes
     * Glicko-2 prior derivation through the shared factory — T9 tier ×
     * AiModelProbeTier7 trust class — instead of the previous raw-weight×1e9
     * scaling that conflated weight magnitude with rating. */
    private IEnumerable<SubstrateChange> BuildQkAttestationBatches(
        Dictionary<(uint q, uint k), (double sum, int count)> accum,
        int vocabSize, Hash128 kindId, string unitName)
    {
        var survivors = LotteryTicket(accum, vocabSize);
        var seen = new HashSet<Hash128>(survivors.Count);
        SubstrateChangeBuilder? b = null;
        int inBatch = 0;
        int batchStart = 0, batchEnd = 0;
        int idx = 0;
        foreach (var (qi, kj, score) in survivors)
        {
            idx++;
            if (qi >= (uint)_tokens.Count || kj >= (uint)_tokens.Count) continue;
            var subj  = _tokens[(int)qi].EntityId;
            var obj   = _tokens[(int)kj].EntityId;
            var row   = AttestationFactory.Create(subj, kindId, obj, _sourceId, null,
                                                  KindValueTier.T9, TrustClass.AiModelProbeTier7);
            if (!seen.Add(row.Id)) continue;
            if (b is null) { batchStart = idx - 1; batchEnd = Math.Min(batchStart + AttBatchSize - 1, survivors.Count - 1); }
            b ??= new SubstrateChangeBuilder(_sourceId,
                $"{unitName}/{batchStart}..{batchEnd}",
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: AttBatchSize);
            b.AddAttestation(row);
            if (++inBatch >= AttBatchSize) { yield return b.Build(); b = null; inBatch = 0; }
        }
        if (b != null) yield return b.Build();
    }

    /* Lottery-ticket sparsity (ADR 0007 / RULES R3): per-tensor top-k% across the whole
     * matchup set, then per-subject top-k. No flat threshold. Shared by the QK (token↔token)
     * and projection (token↔feature) builders. Tuple element names differ at call sites but
     * the underlying (uint,uint) key type is identical, so both accum shapes bind here. */
    private List<(uint a, uint b, float score)> LotteryTicket(
        Dictionary<(uint a, uint b), (double sum, int count)> accum, int rowHint)
    {
        var means = new (uint a, uint b, double mean)[accum.Count];
        int idx = 0;
        foreach (var (key, (sum, count)) in accum) means[idx++] = (key.a, key.b, sum / count);

        int keepN = Math.Max(1, (int)(means.Length * TopkPct));
        if (keepN < means.Length)
        {
            Array.Sort(means, (a, b) => Math.Abs(b.mean).CompareTo(Math.Abs(a.mean)));
            means = means.AsSpan(0, keepN).ToArray();
        }

        var byRow = new Dictionary<uint, List<(uint b, double mean)>>(rowHint);
        foreach (var (a, b, mean) in means)
        {
            if (!byRow.TryGetValue(a, out var list)) { list = []; byRow[a] = list; }
            list.Add((b, mean));
        }

        var survivors = new List<(uint a, uint b, float score)>(byRow.Count * TopkPerRow);
        foreach (var (a, list) in byRow)
        {
            list.Sort((x, y) => Math.Abs(y.mean).CompareTo(Math.Abs(x.mean)));
            int take = Math.Min(TopkPerRow, list.Count);
            for (int i = 0; i < take; i++) survivors.Add((a, list[i].b, (float)list[i].mean));
        }
        return survivors;
    }

    /* Model_Feature / FeatureId / BuildFeatureEntities / AccumulateProjectionScores /
     * BuildProjectionAttestationBatches removed — they implemented the previous
     * "(token, feature_dim) attestation against fake feature-dim entities" shape that
     * the ADR 0056 codec correction replaced with (a) PROJECTION physicalities for
     * embed_tokens / lm_head and (b) self-bilinear E·W·Wᵀ·Eᵀ → token×token
     * attestations for the interior V/O/GATES/UP/DOWN tensors. The latter flow
     * through AccumulateQkScoresBatch + BuildQkAttestationBatches above. */

    /* embed_tokens → per-token PROJECTION physicality via Procrustes-aligned
     * dynamics pipeline (ADR 0056 corrected codec — replaces the previous
     * per-cell EMBEDS attestation emission). One PROJECTION row per token,
     * 4D coord = Procrustes(N-d embed row → substrate-canonical S³ frame),
     * anchored on token entities that already have CONTENT 4D coords from
     * TextDecomposer's tier-tree composition. */
    private IEnumerable<SubstrateChange> BuildEmbeddingPhysicalityBatches(
        ushort[] E_bf16, int vocabSize, int dModel,
        int batchSize = 4096)
    {
        /* 1. Collect tokens with CONTENT 4D anchors (set by LlamaTokenizerParser
         *    via TextDecomposer.Run during Parse). Subsample to ~512 anchor
         *    pairs uniformly to keep Procrustes fit memory bounded. */
        var anchorIndices = new List<int>(_tokens.Count);
        for (int t = 0; t < _tokens.Count; t++)
            if (_tokens[t].HasContentCoord)
                anchorIndices.Add(t);

        if (anchorIndices.Count < 4)
            throw new InvalidOperationException(
                $"LlamaWeightExtractor: only {anchorIndices.Count} token anchors carry " +
                "CONTENT 4D physicalities; need at least 4. Linguistic-seed ingest " +
                "(Unicode + TextDecomposer) must precede model ingest.");

        int nAnchors = Math.Min(512, anchorIndices.Count);
        int stride = Math.Max(1, anchorIndices.Count / nAnchors);

        var anchorSrc = new double[(long)nAnchors * dModel];
        var anchorTgt = new double[(long)nAnchors * 4];
        int actualN = 0;
        for (int k = 0; k < nAnchors; k++)
        {
            int tIdx = anchorIndices[k * stride];
            if (tIdx >= vocabSize) break;
            var rec = _tokens[tIdx];
            long rowOff = (long)tIdx * dModel;
            for (int d = 0; d < dModel; d++)
            {
                uint bits = (uint)E_bf16[rowOff + d] << 16;
                anchorSrc[(long)actualN * dModel + d] =
                    (double)BitConverter.UInt32BitsToSingle(bits);
            }
            anchorTgt[(long)actualN * 4 + 0] = rec.ContentX;
            anchorTgt[(long)actualN * 4 + 1] = rec.ContentY;
            anchorTgt[(long)actualN * 4 + 2] = rec.ContentZ;
            anchorTgt[(long)actualN * 4 + 3] = rec.ContentM;
            actualN++;
        }

        /* 2. Fit Procrustes via liblaplace_dynamics (MKL/Eigen-backed). */
        IntPtr transform;
        double residual;
        unsafe
        {
            fixed (double* srcPtr = anchorSrc)
            fixed (double* tgtPtr = anchorTgt)
            {
                transform = DynamicsInterop.ProcrustesFit(
                    srcPtr, (nuint)actualN, (nuint)dModel, tgtPtr);
            }
        }
        if (transform == IntPtr.Zero)
            throw new InvalidOperationException(
                "ProcrustesFit returned NULL — alignment failed.");
        residual = DynamicsInterop.ProcrustesResidual(transform);

        try
        {
            /* 3. Apply transform to every token; emit PROJECTION physicality. */
            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            var rowF64 = new double[dModel];
            var out4d  = new double[4];
            SubstrateChangeBuilder? b = null;
            int inBatch = 0;
            int batchStart = 0;
            int n = Math.Min(vocabSize, _tokens.Count);

            for (int t = 0; t < n; t++)
            {
                long rowOff = (long)t * dModel;
                for (int d = 0; d < dModel; d++)
                {
                    uint bits = (uint)E_bf16[rowOff + d] << 16;
                    rowF64[d] = (double)BitConverter.UInt32BitsToSingle(bits);
                }

                unsafe
                {
                    fixed (double* rowPtr = rowF64)
                    fixed (double* outPtr = out4d)
                    {
                        DynamicsInterop.ProcrustesApply(
                            transform, rowPtr, (nuint)dModel, outPtr);
                    }
                }

                var entityId = _tokens[t].EntityId;
                var hilbert = Hilbert128.Encode(out4d);
                var physId = PhysicalityId.Compute(
                    entityId, _sourceId, PhysicalityKind.Projection,
                    out4d[0], out4d[1], out4d[2], out4d[3],
                    ReadOnlySpan<double>.Empty);

                if (b is null) { batchStart = t; }
                b ??= new SubstrateChangeBuilder(_sourceId,
                    $"embed_tokens/{batchStart}..{Math.Min(batchStart + batchSize - 1, n - 1)}",
                    entityCapacity: 0, physicalityCapacity: batchSize, attestationCapacity: 0);

                b.AddPhysicality(new PhysicalityRow(
                    Id: physId,
                    EntityId: entityId,
                    SourceId: _sourceId,
                    Kind: PhysicalityKind.Projection,
                    CoordX: out4d[0], CoordY: out4d[1], CoordZ: out4d[2], CoordM: out4d[3],
                    HilbertIndex: hilbert,
                    TrajectoryXyzm: null, NConstituents: 0,
                    AlignmentResidual: residual,
                    SourceDim: dModel,
                    ObservedAtUnixUs: nowUs));

                if (++inBatch >= batchSize)
                {
                    yield return b.Build();
                    b = null;
                    inBatch = 0;
                }
            }

            if (b != null) yield return b.Build();
        }
        finally
        {
            DynamicsInterop.ProcrustesFree(transform);
        }
    }

    /* Row-major transpose [rows × cols] → [cols × rows]. */
    private static float[] Transpose(float[] m, int rows, int cols)
    {
        var o = new float[(long)rows * cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                o[(long)c * rows + r] = m[(long)r * cols + c];
        return o;
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

}
