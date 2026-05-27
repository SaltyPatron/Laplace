using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;    // QkPair
using Laplace.SubstrateCRUD;
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

    /* Content-based feature entity IDs, computed from actual weight columns/rows.
     * Populated at the start of ExtractAsync before first emit. */
    private Hash128[] _residIds = null!;
    private Hash128[] _kvIds    = null!;
    private Hash128[] _ffnIds   = null!;

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

        /* Content-based feature entity IDs: Blake3(weight bytes) — no model name, no
         * axis label, no dim index.  Same weight values → same entity across models.
         * Load layer-0 v_proj and gate_proj to anchor kv and ffn feature IDs. */
        ushort[] v0_bf16 = refMap.ContainsKey("model.layers.0.self_attn.v_proj.weight")
            ? LoadRawBF16(refMap, "model.layers.0.self_attn.v_proj.weight", (long)kvDim * dModel)
            : new ushort[(long)kvDim * dModel];
        ushort[] g0_bf16 = refMap.ContainsKey("model.layers.0.mlp.gate_proj.weight")
            ? LoadRawBF16(refMap, "model.layers.0.mlp.gate_proj.weight", (long)interm * dModel)
            : new ushort[(long)interm * dModel];

        _residIds = new Hash128[dModel];
        _kvIds    = new Hash128[kvDim];
        _ffnIds   = new Hash128[interm];
        for (int d = 0; d < dModel; d++)  _residIds[d] = ModelFeatureEntityId.FromBF16Column(E_bf16, vocabSize, dModel, d);
        for (int d = 0; d < kvDim;  d++)  _kvIds[d]    = ModelFeatureEntityId.FromBF16Row(v0_bf16, dModel, d);
        for (int d = 0; d < interm; d++)  _ffnIds[d]   = ModelFeatureEntityId.FromBF16Row(g0_bf16, dModel, d);

        /* Phase 0 (ADR 0056): feature/hidden-dim entities — the object/subject axis the
         * interior roles attest against. Emitted FIRST so attestations referencing them
         * satisfy the FK ordering. */
        yield return BuildFeatureEntities(dModel, kvDim, interm);
        await Task.Yield();

        /* Identity [dModel × dModel] — lets the projection scorer read a token-indexed
         * matrix's per-cell magnitudes directly (M·Iᵀ = M); reused for EMBEDS + OUTPUT. */
        float[] identity = new float[(long)dModel * dModel];
        for (int d = 0; d < dModel; d++) identity[(long)d * dModel + d] = 1.0f;

        /* EMBEDS: per-cell magnitude of embed_tokens → (token, hidden_dim). */
        var embedAccum = new Dictionary<(uint t, uint d), (double sum, int count)>(1 << 20);
        AccumulateProjectionScores(E_bf16, identity, vocabSize, dModel, dModel, embedAccum);
        foreach (var c in BuildProjectionAttestationBatches(embedAccum, vocabSize, _kinds.Embeds, "d", tokenSubject: true, "embeds")) { yield return c; await Task.Yield(); }

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

        /* Interior projection roles: E·Wᵀ token→feature magnitude, aggregated across layers.
         * V/GATES/UP keep the token as subject; O/DOWN flip (feature is the subject per
         * ADR 0056's (hidden_dim, text) orientation). */
        var vAccum  = new Dictionary<(uint t, uint d), (double sum, int count)>(1 << 20);
        var oAccum  = new Dictionary<(uint t, uint d), (double sum, int count)>(1 << 20);
        var gAccum  = new Dictionary<(uint t, uint d), (double sum, int count)>(1 << 20);
        var uAccum  = new Dictionary<(uint t, uint d), (double sum, int count)>(1 << 20);
        var dnAccum = new Dictionary<(uint t, uint d), (double sum, int count)>(1 << 20);

        for (int layer = 0; layer < _recipe.NumLayers; layer++)
        {
            ct.ThrowIfCancellationRequested();
            string p = $"model.layers.{layer}.";

            if (refMap.ContainsKey(p + "self_attn.v_proj.weight"))
            {
                float[] w = LoadRawBF16AsF32(refMap, p + "self_attn.v_proj.weight", (long)kvDim * dModel);
                AccumulateProjectionScores(E_bf16, w, vocabSize, dModel, kvDim, vAccum);
            }
            if (refMap.ContainsKey(p + "self_attn.o_proj.weight"))
            {
                /* o_proj is [dModel × attnOut]; transpose to [attnOut × dModel] so E·Wᵀ = E·o_proj. */
                float[] w = LoadRawBF16AsF32(refMap, p + "self_attn.o_proj.weight", (long)dModel * attnOut);
                AccumulateProjectionScores(E_bf16, Transpose(w, dModel, attnOut), vocabSize, dModel, attnOut, oAccum);
            }
            if (refMap.ContainsKey(p + "mlp.gate_proj.weight"))
            {
                float[] w = LoadRawBF16AsF32(refMap, p + "mlp.gate_proj.weight", (long)interm * dModel);
                AccumulateProjectionScores(E_bf16, w, vocabSize, dModel, interm, gAccum);
            }
            if (refMap.ContainsKey(p + "mlp.up_proj.weight"))
            {
                float[] w = LoadRawBF16AsF32(refMap, p + "mlp.up_proj.weight", (long)interm * dModel);
                AccumulateProjectionScores(E_bf16, w, vocabSize, dModel, interm, uAccum);
            }
            if (refMap.ContainsKey(p + "mlp.down_proj.weight"))
            {
                /* down_proj is [dModel × interm]; transpose to [interm × dModel] so E·Wᵀ = E·down_proj. */
                float[] w = LoadRawBF16AsF32(refMap, p + "mlp.down_proj.weight", (long)dModel * interm);
                AccumulateProjectionScores(E_bf16, Transpose(w, dModel, interm), vocabSize, dModel, interm, dnAccum);
            }
            await Task.Yield();
        }

        foreach (var c in BuildProjectionAttestationBatches(vAccum,  vocabSize, _kinds.VProjects,    "kv",  tokenSubject: true,  "v"))     { yield return c; await Task.Yield(); }
        foreach (var c in BuildProjectionAttestationBatches(oAccum,  vocabSize, _kinds.OProjects,    "d",   tokenSubject: false, "o"))     { yield return c; await Task.Yield(); }
        foreach (var c in BuildProjectionAttestationBatches(gAccum,  vocabSize, _kinds.Gates,        "ffn", tokenSubject: true,  "gates")) { yield return c; await Task.Yield(); }
        foreach (var c in BuildProjectionAttestationBatches(uAccum,  vocabSize, _kinds.UpProjects,   "ffn", tokenSubject: true,  "up"))    { yield return c; await Task.Yield(); }
        foreach (var c in BuildProjectionAttestationBatches(dnAccum, vocabSize, _kinds.DownProjects, "ffn", tokenSubject: false, "down"))  { yield return c; await Task.Yield(); }

        /* OUTPUT_PROJECTS: per-cell magnitude of lm_head → (hidden_dim, token) (feature subject). */
        if (refMap.ContainsKey("lm_head.weight"))
        {
            ushort[] lm = LoadRawBF16(refMap, "lm_head.weight", (long)vocabSize * dModel);
            var outAccum = new Dictionary<(uint t, uint d), (double sum, int count)>(1 << 20);
            AccumulateProjectionScores(lm, identity, vocabSize, dModel, dModel, outAccum);
            foreach (var c in BuildProjectionAttestationBatches(outAccum, vocabSize, _kinds.OutputProjects, "d", tokenSubject: false, "output")) { yield return c; await Task.Yield(); }
        }
        /* NORMALIZES (per-dim layernorm scale) deferred — small unary signal, not weight-bearing for the round-trip. */
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

    /* Q_PROJECTS (token↔token): lottery-ticket → batched attestation changes. */
    private IEnumerable<SubstrateChange> BuildQkAttestationBatches(
        Dictionary<(uint q, uint k), (double sum, int count)> accum,
        int vocabSize, Hash128 kindId, string unitName)
    {
        var survivors = LotteryTicket(accum, vocabSize);
        var seen = new HashSet<Hash128>(survivors.Count);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        SubstrateChangeBuilder? b = null; int inBatch = 0, batchNo = 0;
        foreach (var (qi, kj, score) in survivors)
        {
            if (qi >= (uint)_tokens.Count || kj >= (uint)_tokens.Count) continue;
            var subj  = _tokens[(int)qi].EntityId;
            var obj   = _tokens[(int)kj].EntityId;
            var attId = LlamaRecipeExtractor.ComputeAttestationId(subj, kindId, obj, _sourceId);
            if (!seen.Add(attId)) continue;
            b ??= new SubstrateChangeBuilder(_sourceId, $"qk/{unitName}/{batchNo}",
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: AttBatchSize);
            b.AddAttestation(new AttestationRow(
                Id: attId, SubjectId: subj, KindId: kindId, ObjectId: obj,
                SourceId: _sourceId, ContextId: null,
                RatingFp1e9: ScaleToRating(score), RdFp1e9: kRdFp1e9, VolatilityFp1e9: kVolatilityFp1e9,
                LastObservedAtUnixUs: now, ObservationCount: 1));
            if (++inBatch >= AttBatchSize) { yield return b.Build(); b = null; inBatch = 0; batchNo++; }
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

    /* Model_Feature type (hidden/feature-dim entities) + content-based per-(axis,dim) id. */
    private static readonly Hash128 ModelFeatureTypeId = Hash128.OfCanonical("substrate/type/Model_Feature/v1");
    private Hash128 FeatureId(string axis, int dim) => axis switch
    {
        "d"   => _residIds[dim],
        "kv"  => _kvIds[dim],
        "ffn" => _ffnIds[dim],
        _     => throw new ArgumentOutOfRangeException(nameof(axis), axis, null),
    };

    /* Phase 0 (ADR 0056): the feature/hidden-dim entities the interior roles attest against.
     * axes: "d" = dModel hidden dims (EMBEDS/O/OUTPUT), "kv" = v_proj value dims,
     * "ffn" = intermediate dims (gate/up/down). */
    private SubstrateChange BuildFeatureEntities(int dModel, int kvDim, int interm)
    {
        int total = dModel + kvDim + interm;
        var b = new SubstrateChangeBuilder(_sourceId, "feature/entities",
            entityCapacity: total, physicalityCapacity: 0, attestationCapacity: 0);
        for (int d = 0; d < dModel; d++) b.AddEntity(FeatureId("d", d),   tier: 0, ModelFeatureTypeId, firstObservedBy: _sourceId);
        for (int d = 0; d < kvDim;  d++) b.AddEntity(FeatureId("kv", d),  tier: 0, ModelFeatureTypeId, firstObservedBy: _sourceId);
        for (int d = 0; d < interm; d++) b.AddEntity(FeatureId("ffn", d), tier: 0, ModelFeatureTypeId, firstObservedBy: _sourceId);
        return b.Build();
    }

    /* E·Wᵀ token→feature projection accumulator (ADR 0056 interior-role math_function).
     * Calls the C scorer; accumulates (token, feature_dim) → (Σ value, count) for
     * within-model aggregation across layers. */
    private void AccumulateProjectionScores(
        ushort[] E_bf16, float[] W_f32, int vocabSize, int dModel, int nOut,
        Dictionary<(uint t, uint d), (double sum, int count)> accum)
    {
        long cap = (long)vocabSize * TopkPerRow;
        var pairs = new QkPair[cap];
        GCHandle hE = GCHandle.Alloc(E_bf16, GCHandleType.Pinned);
        GCHandle hW = GCHandle.Alloc(W_f32,  GCHandleType.Pinned);
        GCHandle hP = GCHandle.Alloc(pairs,  GCHandleType.Pinned);
        try
        {
            unsafe
            {
                int n = SynthInterop.ComputeStaticProjectionScores(
                    (ushort*)hE.AddrOfPinnedObject(), (nuint)vocabSize, (nuint)dModel,
                    (float*)hW.AddrOfPinnedObject(), (nuint)nOut,
                    (nuint)TopkPerRow,
                    (QkPair*)hP.AddrOfPinnedObject(), (nuint)cap);
                if (n < 0) throw new InvalidOperationException($"compute_static_projection_scores returned {n}");
                for (int i = 0; i < n; i++)
                {
                    var pr  = pairs[i];
                    var key = (pr.QueryIdx, pr.KeyIdx);
                    if (accum.TryGetValue(key, out var e)) accum[key] = (e.sum + pr.Score, e.count + 1);
                    else accum[key] = (pr.Score, 1);
                }
            }
        }
        finally { hE.Free(); hW.Free(); hP.Free(); }
    }

    /* Build (token↔feature) attestations from an accumulated projection matchup set.
     * tokenSubject=true → (token, feature); false → (feature, token) (ADR 0056 axis
     * orientation for O/DOWN/OUTPUT). The feature endpoint is a Model_Feature entity. */
    /* Interior role (token↔feature): lottery-ticket → batched attestation changes.
     * tokenSubject=true → (token, feature); false → (feature, token) per ADR 0056. */
    private IEnumerable<SubstrateChange> BuildProjectionAttestationBatches(
        Dictionary<(uint t, uint d), (double sum, int count)> accum,
        int vocabSize, Hash128 kindId, string axis, bool tokenSubject, string unitName)
    {
        var survivors = LotteryTicket(accum, vocabSize);
        var seen = new HashSet<Hash128>(survivors.Count);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        SubstrateChangeBuilder? b = null; int inBatch = 0, batchNo = 0;
        foreach (var (t, d, score) in survivors)
        {
            if (t >= (uint)_tokens.Count) continue;
            var tok  = _tokens[(int)t].EntityId;
            var feat = FeatureId(axis, (int)d);
            var subj = tokenSubject ? tok : feat;
            var obj  = tokenSubject ? feat : tok;
            var attId = LlamaRecipeExtractor.ComputeAttestationId(subj, kindId, obj, _sourceId);
            if (!seen.Add(attId)) continue;
            b ??= new SubstrateChangeBuilder(_sourceId, $"proj/{unitName}/{batchNo}",
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: AttBatchSize);
            b.AddAttestation(new AttestationRow(
                Id: attId, SubjectId: subj, KindId: kindId, ObjectId: obj,
                SourceId: _sourceId, ContextId: null,
                RatingFp1e9: ScaleToRating(score), RdFp1e9: kRdFp1e9, VolatilityFp1e9: kVolatilityFp1e9,
                LastObservedAtUnixUs: now, ObservationCount: 1));
            if (++inBatch >= AttBatchSize) { yield return b.Build(); b = null; inBatch = 0; batchNo++; }
        }
        if (b != null) yield return b.Build();
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

    /* Signed fixed-point ×1e9 — preserves sign AND magnitude exactly (BF16-faithful).
     * The old sigmoid-of-|weight| discarded the sign and saturated the magnitude into
     * [1000,1800], which alone made faithful reconstruction impossible. Reconstruction
     * needs the actual value, so store it. (Magnitude for Glicko/A* = |rating|.) */
    private static long ScaleToRating(double weight) => (long)Math.Round(weight * 1e9);
}
