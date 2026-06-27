using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

// ── Lane B: manifest-driven, fold-everything circuit extractor ────────────────────────────────
// Model ingestion = pull token-to-token relations out of the weights, store nothing else (no
// physicalities, no axis entities, no stored matrices — only codepoints surface to the glome).
//
// The pipeline reads the shape-inferred ModelManifest (Lane A) and runs every circuit it can:
//   SIMILAR_TO   — the embedding self-similarity field (robust SVD reduction, denoised)
//   ATTENDS      — per (layer, head) QK bilinear: which tokens query which keys
//   OV_RELATES   — per layer OV circuit: what a head writes when it attends
//   COMPLETES_TO — per layer MLP: which token completes which (gate·up → down → unembed)
//
// Every circuit value becomes a Glicko game via laplace_score_fp (already computed inside the
// kernels) and is folded with NativeAttestation.Aggregated(games=1, sumScoreFp). There is NO
// pre-floor and NO top-k partner cap: sparsity is EARNED post-fold by consensus_fold (an edge that
// only ever drew stays neutral and is pruned at read time), never imposed before the evidence is
// seen. The DB consensus fold accumulates repeated (subject,type,object) witnesses across layers
// and heads, so accumulation is intrinsic — C# only stages one aggregated game per circuit pair.
public sealed class ModelTokenEdgeETL
{
    private const int RowTile       = 256;
    private const int AttsPerChange = 200_000;
    private const int EigTargetDim  = 64;
    private const int TopPairsPerCircuit = 64;   // decoder-ring sample size, NOT a fold cap

    // theta is the kernel's emit threshold. The plan deletes the lossy floor: default 0 means
    // "emit every non-zero pair" (an exact zero is a draw and contributes nothing anyway). It stays
    // env-overridable purely as a tractability escape hatch for very large models, not as policy.
    private static readonly double Theta =
        double.TryParse(Environment.GetEnvironmentVariable("LAPLACE_MODEL_EDGE_FLOOR"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var tf) ? tf : 0.0;

    // "all" runs every circuit plane; "similarity" restricts to the embedding plane (cheap path for
    // smoke tests / constrained hosts). Default honors the plan: run every circuit.
    // LAPLACE_MODEL_PLANES selects which planes run: "all" (default — similarity + CONTINUES_TO +
    // per-layer attention/OV/MLP), "similarity" (embedding plane only — cheap smoke), or "continues"
    // (embedding + LM-head direct path only — targeted CONTINUES_TO without the per-layer cost).
    private static readonly string PlanesMode =
        (Environment.GetEnvironmentVariable("LAPLACE_MODEL_PLANES") ?? "all").ToLowerInvariant();
    private static bool RunSimilarity => PlanesMode is "all" or "similarity";
    private static bool RunContinues  => PlanesMode is "all" or "continues";
    private static bool RunLayers     => PlanesMode == "all";

    // Track A2: norm-gain folding is ON by default; LAPLACE_MODEL_NORMFOLD=0 disables it so the
    // pre-fold baseline (B0) can be captured on the identical eval harness for a true before/after.
    private static readonly bool NormFold =
        !string.Equals(Environment.GetEnvironmentVariable("LAPLACE_MODEL_NORMFOLD"), "0", StringComparison.Ordinal);

    private readonly string _modelDir;
    private readonly ModelManifest _manifest;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _source;
    private readonly ILogger _log;
    private readonly HeadClassifier? _classifier;

    public ModelTokenEdgeETL(string modelDir, ModelManifest manifest,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens, Hash128 sourceId,
        ILogger? log = null, HeadClassifier? classifier = null)
    {
        _modelDir = modelDir; _manifest = manifest; _tokens = tokens; _source = sourceId;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _classifier = classifier;
    }

    private static double WeightFor(string relation) =>
        RelationTypeRegistry.Resolve(relation).Rank * SourceTrust.AiModelProbe;

    public async IAsyncEnumerable<SubstrateChange> EmitAsync(
        int commitEpoch, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_manifest.Coverage == Coverage.Unsupported)
        {
            _log.LogWarning("phase=edges: model '{Name}' is unsupported (model_type={Mt}); "
                + "recipe scalars only, no circuit decrypt", _manifest.ModelName, _manifest.Config.ModelType);
            yield break;
        }

        var embedRole = _manifest.Embedding;
        if (embedRole is null)
        {
            _log.LogWarning("phase=edges: no embedding table classified (modality={Mod}, coverage={Cov}); "
                + "nothing to extract", _manifest.Modality, _manifest.Coverage);
            yield break;
        }

        var cfg = _manifest.Config;
        int vocab = cfg.VocabSize, d = cfg.HiddenSize;

        // Collapse the address book onto distinct content token entities (king==king across models).
        var ents = new List<Hash128>(vocab);
        var rowOfToken = new List<int>(vocab);
        var seen = new HashSet<Hash128>();
        foreach (var rec in _tokens)
        {
            if (rec.TokenId < 0 || rec.TokenId >= vocab) continue;
            if (!seen.Add(rec.EntityId)) continue;
            ents.Add(rec.EntityId);
            rowOfToken.Add(rec.TokenId);
        }
        int n = ents.Count;
        if (n < 4)
        {
            _log.LogWarning("phase=edges: only {N} content tokens; skipping", n);
            yield break;
        }

        var refs = SafetensorsContainerParser.ParseModel(_modelDir);
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(refs.Count, StringComparer.Ordinal);
        foreach (var r in refs) refMap[r.Name] = r;

        // Gather the content-token rows once → Af[n,d] (float, fed to project_embedding kernels).
        float[] embed = WeightTensorETL.LoadTensorF32(refMap, embedRole.Name, (long)vocab * d);
        var Af = new float[(long)n * d];
        for (int i = 0; i < n; i++)
            Array.Copy(embed, (long)rowOfToken[i] * d, Af, (long)i * d, d);

        // ── Plane 1: SIMILAR_TO (embedding self-similarity, robust SVD reduction) ──────────────
        if (RunSimilarity)
            await foreach (var change in EmitSimilarityPlane(Af, ents, n, d, commitEpoch, ct))
                yield return change;

        if (!_manifest.TextPlanesRunnable)
        {
            _log.LogInformation("phase=edges: coverage={Cov} modality={Mod}; embedding-plane only",
                _manifest.Coverage, _manifest.Modality);
            yield break;
        }

        // ── Plane: CONTINUES_TO (LM-head direct path E·W_U, UNTIED only; global, not per-layer) ──
        if (RunContinues)
            await foreach (var change in EmitContinuesPlane(Af, ents, rowOfToken, n, d, commitEpoch, refMap, ct))
                yield return change;

        if (!RunLayers) yield break;

        // The deep planes contract on the hidden dim, so they need the gathered rows as double.
        var Ad = new double[(long)n * d];
        for (long i = 0; i < (long)n * d; i++) Ad[i] = Af[i];

        int layers = _manifest.LayerCount;
        for (int L = 0; L < layers; L++)
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var change in EmitAttentionLayer(L, Af, Ad, ents, n, d, commitEpoch, refMap, ct))
                yield return change;
            await foreach (var change in EmitOvLayer(L, Af, Ad, ents, n, d, commitEpoch, refMap, ct))
                yield return change;
            await foreach (var change in EmitMlpLayer(L, Ad, ents, n, d, commitEpoch, refMap, ct))
                yield return change;
        }
    }

    // ── Plane 1: SIMILAR_TO ───────────────────────────────────────────────────────────────────
    private async IAsyncEnumerable<SubstrateChange> EmitSimilarityPlane(
        float[] Af, List<Hash128> ents, int n, int d, int commitEpoch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int kmax = Math.Min(EigTargetDim, Math.Min(d, n));
        if (kmax < 2) yield break;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Truncated SVD (MKL) → dominant subspace. F = U·diag(S) preserves E·Eᵀ (the token-similarity
        // field), denoised to the retained rank. SVD preserves inner products; Laplacian eigenmaps
        // would distort them — wrong tool here.
        var U = new float[(long)n * kmax];
        var S = new float[kmax];
        var Vt = new float[(long)kmax * d];
        int rc = RunSvd(Af, n, d, kmax, U, S, Vt, out int rank);
        if (rc != 0) { _log.LogWarning("phase=edges: tensor_svd_truncate rc={Rc}; skipping similarity", rc); yield break; }
        if (rank < 2) { _log.LogWarning("phase=edges: SVD rank {R}<2; skipping similarity", rank); yield break; }

        var Y = new double[(long)n * rank];
        for (int i = 0; i < n; i++)
            for (int t = 0; t < rank; t++)
                Y[(long)i * rank + t] = (double)U[(long)i * kmax + t] * S[t];
        NormRows(Y, n, rank);
        _log.LogInformation("phase=edges: SVD reduced {N:N0} tokens d={D}->rank {R} (tol 1%), {Sec:F1}s; "
            + "folding SIMILAR_TO (every pair, no floor)", n, d, rank, sw.Elapsed.TotalSeconds);

        var typeId = RelationTypeRegistry.RelationTypeId("SIMILAR_TO");
        double weight = WeightFor("SIMILAR_TO");
        await foreach (var change in EmitBilinearPairs(
            Y, Y, n, rank, typeId, weight, ents, ents, commitEpoch,
            new CircuitDescriptor(Layer: -1, Head: -1, Plane: "similarity", RelationName: "SIMILAR_TO"),
            sampleForDecoder: false, ct))
            yield return change;
    }

    // ── Plane: CONTINUES_TO (LM-head direct path) ───────────────────────────────────────────────
    // The "direct path" logit of token j following token i is E[i]·W_U[j] (embedding · unembedding):
    // how strongly token i, sitting in the residual, directly promotes token j as the next token.
    // This is the generative/continuation signal. It is meaningful ONLY for UNTIED embeddings — when
    // tied, W_U == E and E[i]·E[j] is just SIMILAR_TO (symmetric), so we skip to avoid a redundant
    // (and mislabeled) plane. Directional: CONTINUES_TO(i→j) ≠ CONTINUES_TO(j→i).
    private async IAsyncEnumerable<SubstrateChange> EmitContinuesPlane(
        float[] Af, List<Hash128> ents, List<int> rowOfToken, int n, int d, int commitEpoch,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _manifest.Config;
        var embedRole = _manifest.Embedding;
        var lmRole = _manifest.LmHead;   // explicit lm_head, or (tied) falls back to the embedding role
        if (cfg.TieWordEmbeddings || lmRole is null || embedRole is null
            || string.Equals(lmRole.Name, embedRole.Name, StringComparison.Ordinal))
        {
            _log.LogInformation("phase=edges: CONTINUES_TO skipped — tied embeddings (≡ SIMILAR_TO)");
            yield break;
        }

        float[] Ufull;
        try { Ufull = WeightTensorETL.LoadTensorF32(refMap, lmRole.Name, (long)cfg.VocabSize * d); }
        catch (Exception ex)
        { _log.LogWarning("phase=edges: lm_head load failed ({Msg}); CONTINUES_TO skipped", ex.Message); yield break; }

        // EXACT full-d direct path: gather the content-token unembedding rows aligned with `ents`/Af,
        // normalize both sides to cosine, and fold the full E·Uᵀ. The native MKL bilinear is ~n²·d and
        // costs seconds; no rank truncation — the score is the true direct-path projection.
        var Ed = new double[(long)n * d];
        var Ud = new double[(long)n * d];
        for (int i = 0; i < n; i++)
        {
            long erow = (long)i * d, urow = (long)rowOfToken[i] * d;
            for (int t = 0; t < d; t++) { Ed[erow + t] = Af[erow + t]; Ud[erow + t] = Ufull[urow + t]; }
        }
        NormRows(Ed, n, d);
        NormRows(Ud, n, d);

        var typeId = RelationTypeRegistry.RelationTypeId("CONTINUES_TO");
        double weight = WeightFor("CONTINUES_TO");
        _log.LogInformation("phase=edges: folding CONTINUES_TO (LM-head direct path, untied, full d={D}) over {N} tokens", d, n);
        await foreach (var change in EmitBilinearPairs(
            Ed, Ud, n, d, typeId, weight, ents, ents, commitEpoch,
            new CircuitDescriptor(Layer: -1, Head: -1, Plane: "continues", RelationName: "CONTINUES_TO"),
            sampleForDecoder: true, ct))
            yield return change;
    }

    // ── Plane 2: ATTENDS (per layer, per head QK bilinear) ──────────────────────────────────────
    private async IAsyncEnumerable<SubstrateChange> EmitAttentionLayer(
        int L, float[] Af, double[] Ad, List<Hash128> ents, int n, int d, int commitEpoch,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _manifest.Config;
        var qRole = _manifest.Single(L, TensorRoleKind.AttnQ);
        var kRole = _manifest.Single(L, TensorRoleKind.AttnK);
        if (qRole is null || kRole is null) yield break;
        int H = cfg.NumHeads, Hkv = cfg.NumKvHeads, hd = cfg.HeadDim;
        if (H <= 0 || hd <= 0) yield break;
        int attn = H * hd, kvDim = Hkv * hd;

        float[] Wq, Wk;
        try
        {
            Wq = WeightTensorETL.LoadTensorF32(refMap, qRole.Name, (long)attn * d);
            Wk = WeightTensorETL.LoadTensorF32(refMap, kRole.Name, (long)kvDim * d);
        }
        catch (Exception ex) { _log.LogWarning("phase=edges L{L}: attn load failed: {Msg}", L, ex.Message); yield break; }

        // A2: fold the pre-attention norm gain into Q/K so the bilinear sees the normed residual.
        // Qwen3 per-head q/k RMSNorm gains ([hd]) apply to the head slices below (post-projection).
        float[]? gQ = null, gK = null;
        if (NormFold)
        {
            var gIn = LoadGain(refMap, _manifest.InputNorm(L), d);
            if (gIn is not null) { ScaleCols(Wq, attn, d, gIn); ScaleCols(Wk, kvDim, d, gIn); }
            gQ = LoadGain(refMap, _manifest.QNorm(L), hd);
            gK = LoadGain(refMap, _manifest.KNorm(L), hd);
        }

        var Q = new double[(long)n * attn];
        var Kraw = new double[(long)n * kvDim];
        var Kexp = new double[(long)n * attn];
        int rcq, rck, rce;
        unsafe
        {
            fixed (float* pa = Af) fixed (float* pw = Wq) fixed (double* pq = Q)
                rcq = DynInterop.ProjectEmbedding(pa, (nuint)n, (nuint)d, pw, (nuint)attn, pq);
            fixed (float* pa = Af) fixed (float* pw = Wk) fixed (double* pk = Kraw)
                rck = DynInterop.ProjectEmbedding(pa, (nuint)n, (nuint)d, pw, (nuint)kvDim, pk);
            rce = 0;
            fixed (double* pk = Kraw) fixed (double* pe = Kexp)
                rce = DynInterop.ExpandKvHeadsD(pk, (nuint)n, (nuint)H, (nuint)Hkv, (nuint)hd, pe);
        }
        if (rcq != 0 || rck != 0 || rce != 0)
        { _log.LogWarning("phase=edges L{L}: QK projection rc=({A},{B},{C}); skipping", L, rcq, rck, rce); yield break; }

        var typeId = RelationTypeRegistry.RelationTypeId("ATTENDS");
        double weight = WeightFor("ATTENDS");
        var Qh = new double[(long)n * hd];
        var Kh = new double[(long)n * hd];
        for (int h = 0; h < H; h++)
        {
            ct.ThrowIfCancellationRequested();
            SliceHead(Q, Qh, n, attn, h, hd);
            SliceHead(Kexp, Kh, n, attn, h, hd);
            if (gQ is not null) ScaleColsD(Qh, n, hd, gQ);   // A2: per-head q_norm (pre-RoPE in-model)
            if (gK is not null) ScaleColsD(Kh, n, hd, gK);   // A2: per-head k_norm
            await foreach (var change in EmitBilinearPairs(
                Qh, Kh, n, hd, typeId, weight, ents, ents, commitEpoch,
                new CircuitDescriptor(L, h, "attention", "ATTENDS"), sampleForDecoder: true, ct))
                yield return change;
        }
    }

    // ── Plane 3: OV_RELATES (per layer OV circuit) ──────────────────────────────────────────────
    private async IAsyncEnumerable<SubstrateChange> EmitOvLayer(
        int L, float[] Af, double[] Ad, List<Hash128> ents, int n, int d, int commitEpoch,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _manifest.Config;
        var vRole = _manifest.Single(L, TensorRoleKind.AttnV);
        var oRole = _manifest.Single(L, TensorRoleKind.AttnO);
        if (vRole is null || oRole is null) yield break;
        int H = cfg.NumHeads, Hkv = cfg.NumKvHeads, hd = cfg.HeadDim;
        if (H <= 0 || hd <= 0) yield break;
        int attn = H * hd, kvDim = Hkv * hd;

        float[] Wv, Wo;
        try
        {
            Wv = WeightTensorETL.LoadTensorF32(refMap, vRole.Name, (long)kvDim * d);
            Wo = WeightTensorETL.LoadTensorF32(refMap, oRole.Name, (long)d * attn);
        }
        catch (Exception ex) { _log.LogWarning("phase=edges L{L}: OV load failed: {Msg}", L, ex.Message); yield break; }

        // A2: V reads the same pre-attention-normed residual → fold γ_input into Wv columns.
        // Wo writes back to the (post-attention) residual, so it is NOT norm-scaled.
        if (NormFold)
        {
            var gIn = LoadGain(refMap, _manifest.InputNorm(L), d);
            if (gIn is not null) ScaleCols(Wv, kvDim, d, gIn);
        }

        var Vraw = new double[(long)n * kvDim];
        var Vexp = new double[(long)n * attn];
        var OVout = new double[(long)n * d];
        int rcv, rce, rco;
        unsafe
        {
            fixed (float* pa = Af) fixed (float* pw = Wv) fixed (double* pv = Vraw)
                rcv = DynInterop.ProjectEmbedding(pa, (nuint)n, (nuint)d, pw, (nuint)kvDim, pv);
            fixed (double* pv = Vraw) fixed (double* pe = Vexp)
                rce = DynInterop.ExpandKvHeadsD(pv, (nuint)n, (nuint)H, (nuint)Hkv, (nuint)hd, pe);
            // Vexp[n,attn] @ Wo[d,attn]^T → OVout[n,d]: what the OV circuit writes back to residual.
            fixed (double* pv = Vexp) fixed (float* pw = Wo) fixed (double* po = OVout)
                rco = DynInterop.ProjectEmbeddingD(pv, (nuint)n, (nuint)attn, pw, (nuint)d, po);
        }
        if (rcv != 0 || rce != 0 || rco != 0)
        { _log.LogWarning("phase=edges L{L}: OV projection rc=({A},{B},{C}); skipping", L, rcv, rce, rco); yield break; }

        NormRows(OVout, n, d);
        var En = (double[])Ad.Clone();
        NormRows(En, n, d);

        var typeId = RelationTypeRegistry.RelationTypeId("OV_RELATES");
        double weight = WeightFor("OV_RELATES");
        await foreach (var change in EmitBilinearPairs(
            OVout, En, n, d, typeId, weight, ents, ents, commitEpoch,
            new CircuitDescriptor(L, -1, "ov", "OV_RELATES"), sampleForDecoder: true, ct))
            yield return change;
    }

    // ── Plane 4: COMPLETES_TO (per layer MLP) ───────────────────────────────────────────────────
    private async IAsyncEnumerable<SubstrateChange> EmitMlpLayer(
        int L, double[] Ad, List<Hash128> ents, int n, int d, int commitEpoch,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _manifest.Config;
        var upRole   = _manifest.Single(L, TensorRoleKind.MlpUp);
        var downRole = _manifest.Single(L, TensorRoleKind.MlpDown);
        var gateRole = _manifest.Single(L, TensorRoleKind.MlpGate);
        if (upRole is null || downRole is null) yield break;   // MoE layers have no dense MLP here
        int I = cfg.IntermediateSize;
        if (I <= 0) yield break;

        double[]? gate = null, up, down;
        try
        {
            up   = LoadDouble(refMap, upRole.Name, (long)I * d);
            down = LoadDouble(refMap, downRole.Name, (long)d * I);
            if (gateRole is not null) gate = LoadDouble(refMap, gateRole.Name, (long)I * d);
        }
        catch (Exception ex) { _log.LogWarning("phase=edges L{L}: MLP load failed: {Msg}", L, ex.Message); yield break; }

        // A2: gate/up read the post-attention-normed residual → fold γ_post into their input columns.
        // down writes back to the residual, and the unemb comparison side stays raw — neither scaled.
        if (NormFold)
        {
            var gPost = LoadGain(refMap, _manifest.PostAttnNorm(L), d);
            if (gPost is not null)
            {
                ScaleColsD(up, I, d, gPost);
                if (gate is not null) ScaleColsD(gate, I, d, gPost);
            }
        }

        var typeId = RelationTypeRegistry.RelationTypeId("COMPLETES_TO");
        double weight = WeightFor("COMPLETES_TO");
        var descriptor = new CircuitDescriptor(L, -1, "mlp", "COMPLETES_TO");

        long cap = (long)RowTile * n;
        var oR = new int[cap]; var oC = new int[cap]; var oV = new double[cap]; var oS = new long[cap];
        var collector = _classifier is null ? null : new TopPairCollector(TopPairsPerCircuit);

        var b = NewChunk(commitEpoch); int inChunk = 0; long edges = 0;
        for (int rb = 0; rb < n; rb += RowTile)
        {
            ct.ThrowIfCancellationRequested();
            int re = Math.Min(rb + RowTile, n);
            int cnt = RunFfnTile(Ad, n, d, gate, up, down, I, rb, re, oR, oC, oV, oS, cap);
            if (cnt < 0) { _log.LogWarning("phase=edges L{L}: ffn tile failed; skipping MLP", L); yield break; }
            for (int e = 0; e < cnt; e++)
            {
                if (oC[e] == oR[e]) continue;
                b.AddAttestation(NativeAttestation.Aggregated(
                    ents[oR[e]], typeId, ents[oC[e]], _source, null, 1, oS[e], weight));
                collector?.Offer(ents[oR[e]], ents[oC[e]], oS[e]);
                edges++;
                if (++inChunk >= AttsPerChange) { yield return b.Build(); b = NewChunk(commitEpoch); inChunk = 0; await Task.Yield(); }
            }
        }
        if (inChunk > 0) yield return b.Build();
        _log.LogInformation("phase=edges L{L}: {E:N0} COMPLETES_TO edges folded", L, edges);

        await foreach (var change in ClassifyIfPossible(descriptor, collector, commitEpoch, ct))
            yield return change;
    }

    // Tile a left[n,r] × right[n,r] bilinear, fold EVERY non-zero pair as a Glicko game, stream the
    // attestations, and (optionally) sample the strongest pairs for the decoder ring.
    private async IAsyncEnumerable<SubstrateChange> EmitBilinearPairs(
        double[] left, double[] right, int n, int r, Hash128 typeId, double weight,
        List<Hash128> rowEnts, List<Hash128> colEnts, int commitEpoch,
        CircuitDescriptor descriptor, bool sampleForDecoder,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long cap = (long)RowTile * n;
        var oR = new int[cap]; var oC = new int[cap]; var oV = new double[cap]; var oS = new long[cap];
        var collector = (_classifier is null || !sampleForDecoder) ? null : new TopPairCollector(TopPairsPerCircuit);

        // Symmetric relations (SIMILAR_TO) score identically both ways, so the tile emits each pair
        // twice (a→b and b→a) with the same value — pure redundancy. Emit each once (oR<oC) for those.
        // Directional relations (ATTENDS/OV_RELATES) have genuinely different scores per direction → keep both.
        bool canonicalize = RelationTypeRegistry.Resolve(descriptor.RelationName).Symmetry
                            == RelationTypeRegistry.Symmetry.Symmetric;

        var b = NewChunk(commitEpoch); int inChunk = 0;
        for (int rb = 0; rb < n; rb += RowTile)
        {
            ct.ThrowIfCancellationRequested();
            int re = Math.Min(rb + RowTile, n);
            int cnt = RunBilinearTile(left, rb, re, right, n, r, oR, oC, oV, oS, cap);
            if (cnt < 0) yield break;
            for (int e = 0; e < cnt; e++)
            {
                if (oC[e] == oR[e]) continue;                       // no self-edge
                if (canonicalize && oC[e] < oR[e]) continue;        // symmetric: emit each pair once
                b.AddAttestation(NativeAttestation.Aggregated(
                    rowEnts[oR[e]], typeId, colEnts[oC[e]], _source, null, 1, oS[e], weight));
                collector?.Offer(rowEnts[oR[e]], colEnts[oC[e]], oS[e]);
                if (++inChunk >= AttsPerChange) { yield return b.Build(); b = NewChunk(commitEpoch); inChunk = 0; await Task.Yield(); }
            }
        }
        if (inChunk > 0) yield return b.Build();

        await foreach (var change in ClassifyIfPossible(descriptor, collector, commitEpoch, ct))
            yield return change;
    }

    private async IAsyncEnumerable<SubstrateChange> ClassifyIfPossible(
        CircuitDescriptor descriptor, TopPairCollector? collector, int commitEpoch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_classifier is null || collector is null) yield break;
        var pairs = collector.Drain();
        if (pairs.Count == 0) yield break;
        var change = await _classifier.ClassifyAsync(descriptor, pairs, commitEpoch, ct);
        if (change is { } c) yield return c;
    }

    private SubstrateChangeBuilder NewChunk(int epoch) =>
        new SubstrateChangeBuilder(_source, "model/token-edges", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: AttsPerChange)
            .SetCommitEpoch(epoch);

    private static int RunSvd(float[] A, int n, int d, int kmax, float[] U, float[] S, float[] Vt, out int rank)
    {
        unsafe
        {
            nuint r = 0;
            fixed (float* ap = A) fixed (float* up = U) fixed (float* sp = S) fixed (float* vp = Vt)
            {
                int rc = SynInterop.TensorSvdTruncate(ap, (nuint)n, (nuint)d, 0.01, &r, up, sp, vp, (nuint)kmax);
                rank = rc == 0 ? (int)r : 0;
                return rc;
            }
        }
    }

    // Project rows of `pts`[n,d] onto the basis `w`[r,d]: out[i,t] = Σ_j pts[i,j]·w[t,j] (= pts·wᵀ).
    private static int RunProject(float[] pts, int n, int d, float[] w, int r, double[] outp)
    {
        unsafe
        {
            fixed (float* pp = pts) fixed (float* pw = w) fixed (double* po = outp)
                return DynInterop.ProjectEmbedding(pp, (nuint)n, (nuint)d, pw, (nuint)r, po);
        }
    }

    private static int RunFfnTile(double[] emb, int n, int d, double[]? gate, double[] up, double[] down,
        int interm, int rb, int re, int[] oR, int[] oC, double[] oV, long[] oS, long cap)
    {
        unsafe
        {
            nuint count = 0; int overflow = 0;
            fixed (double* pe = emb) fixed (double* pg = gate) fixed (double* pu = up) fixed (double* pdn = down)
            fixed (int* pR = oR) fixed (int* pC = oC) fixed (double* pV = oV) fixed (long* pS = oS)
            {
                int rc = DynInterop.FfnTokenPairsTile(pe, (nuint)n, (nuint)d, pe,
                    pg, pu, pdn, (nuint)interm, (nuint)rb, (nuint)re, Theta,
                    pR, pC, pV, pS, (nuint)cap, &count, &overflow);
                return rc != 0 ? -1 : (int)count;
            }
        }
    }

    private static int RunBilinearTile(double[] left, int rb, int re, double[] right, int n, int dim,
        int[] oR, int[] oC, double[] oV, long[] oS, long cap)
    {
        unsafe
        {
            nuint count = 0; int overflow = 0;
            fixed (double* pl = left) fixed (double* pr = right)
            fixed (int* pR = oR) fixed (int* pC = oC) fixed (double* pV = oV) fixed (long* pS = oS)
            {
                int rc = DynInterop.BilinearEdgesTile(pl, (nuint)rb, (nuint)re, pr, (nuint)n,
                    (nuint)dim, Theta, pR, pC, pV, pS, (nuint)cap, &count, &overflow);
                return rc != 0 ? -1 : (int)count;
            }
        }
    }

    private static void SliceHead(double[] full, double[] head, int n, int fullDim, int h, int hd)
    {
        for (int i = 0; i < n; i++)
            Array.Copy(full, (long)i * fullDim + (long)h * hd, head, (long)i * hd, hd);
    }

    private static double[] LoadDouble(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string name, long elems)
    {
        float[] f = WeightTensorETL.LoadTensorF32(refMap, name, elems);
        var dbl = new double[elems];
        for (long i = 0; i < elems; i++) dbl[i] = f[i];
        return dbl;
    }

    private static void NormRows(double[] v, int n, int dim)
    {
        unsafe
        {
            fixed (double* p = v)
                if (DynInterop.NormRowsD(p, (nuint)n, (nuint)dim) != 0)
                    throw new InvalidOperationException("norm_rows_d failed");
        }
    }

    // ── Track A2: norm-gain folding ────────────────────────────────────────────────────────────
    // A transformer block reads the residual AFTER RMSNorm/LayerNorm: the operator sees x⊙γ, not x.
    // Folding the diagonal gain into the consuming weight is exact — (W·diag(γ))·x = W·(γ⊙x) — and
    // scaling the weight's input columns is cheaper than scaling the n-row probe (weights ≪ n·d).
    // The per-row RMS rescale is scale-invariant under the bilinear/cosine score, so the diagonal
    // gain is the complete first-order correction for RMSNorm models (LayerNorm mean/β unmodeled).
    // Load a [dim] norm gain γ as float, or null when the role is absent / shape disagrees (→ identity).
    private static float[]? LoadGain(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, TensorRole? role, int dim)
    {
        if (role is null) return null;
        try { return WeightTensorETL.LoadTensorF32(refMap, role.Name, dim); }
        catch (Exception) { return null; }
    }

    // Scale each row's input columns by γ[j] (the diagonal half of W·diag(γ)). M is [rows, dim] row-major.
    private static void ScaleCols(float[] M, long rows, int dim, float[] g)
    {
        for (long i = 0; i < rows; i++)
            for (int j = 0; j < dim; j++) M[i * dim + j] *= g[j];
    }
    private static void ScaleColsD(double[] M, long rows, int dim, float[] g)
    {
        for (long i = 0; i < rows; i++)
            for (int j = 0; j < dim; j++) M[i * dim + j] *= g[j];
    }

    // Bounded selection of the strongest pairs for the decoder ring; never affects what is folded.
    private sealed class TopPairCollector
    {
        private readonly int _cap;
        private readonly List<CircuitPair> _heap;   // min-heap on ScoreFp
        public TopPairCollector(int cap) { _cap = cap; _heap = new List<CircuitPair>(cap + 1); }

        public void Offer(Hash128 subject, Hash128 obj, long scoreFp)
        {
            if (_heap.Count < _cap) { _heap.Add(new CircuitPair(subject, obj, scoreFp)); SiftUp(_heap.Count - 1); }
            else if (scoreFp > _heap[0].ScoreFp) { _heap[0] = new CircuitPair(subject, obj, scoreFp); SiftDown(0); }
        }

        public IReadOnlyList<CircuitPair> Drain()
        {
            _heap.Sort((a, b) => b.ScoreFp.CompareTo(a.ScoreFp));
            return _heap;
        }

        private void SiftUp(int i)
        {
            while (i > 0) { int p = (i - 1) / 2; if (_heap[i].ScoreFp >= _heap[p].ScoreFp) break; (_heap[i], _heap[p]) = (_heap[p], _heap[i]); i = p; }
        }
        private void SiftDown(int i)
        {
            int nN = _heap.Count;
            while (true)
            {
                int l = 2 * i + 1, rr = 2 * i + 2, s = i;
                if (l < nN && _heap[l].ScoreFp < _heap[s].ScoreFp) s = l;
                if (rr < nN && _heap[rr].ScoreFp < _heap[s].ScoreFp) s = rr;
                if (s == i) break;
                (_heap[i], _heap[s]) = (_heap[s], _heap[i]); i = s;
            }
        }
    }
}
