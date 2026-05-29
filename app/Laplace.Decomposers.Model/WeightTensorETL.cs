using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
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
    private const int    AttBatchSize = 4096;

    /* QK noise floor — SEPARATE from the unary NoiseFloor (1e-9). Pre-softmax q·k scores
     * are dense-but-tiny (TinyLlama L0: |q·k| median 2e-3, p99 1.7e-2, max 0.14); at 1e-9
     * ~99% of pairs survive (~1B unique relations — infeasible to store), with no natural
     * gap. Attention is sparse in EFFECT (softmax concentrates on few keys), so low scores
     * are genuine non-relationships. 0.05 keeps the meaningful attention tail. This floor
     * (B) makes QK storable; the spatial-indexed kernel (A) finds these survivors
     * sub-quadratically instead of scanning all pairs. */
    private const double QkNoiseFloor = 0.05;

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
    private readonly ILogger _log;

    public WeightTensorETL(
        string modelDir,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId,
        LlamaWeightExtractor.KindIds kinds,
        ILogger? log = null)
    {
        _recipe          = recipe;
        _tokens          = tokens;
        _sourceId        = sourceId;
        _kinds           = kinds;
        _log             = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
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

        // Validate config dims against the ACTUAL embedding tensor shape — per
        // dimension, not just the product LoadRawBF16 checks. Catches an under-
        // specified/mis-parsed config (e.g. vocab & hidden both wrong but product
        // right) before any reduction runs. Fail-loud, faithfulness mandate.
        if (refMap.TryGetValue("model.embed_tokens.weight", out var embedRef) &&
            (embedRef.Shape.Length != 2 || embedRef.Shape[0] != vocabSize || embedRef.Shape[1] != dModel))
        {
            throw new InvalidOperationException(
                $"Recipe dims (vocab={vocabSize}, hidden={dModel}) disagree with " +
                $"model.embed_tokens.weight shape [{string.Join(",", embedRef.Shape)}]. " +
                "Refusing to ingest mismatched geometry.");
        }

        var phase = System.Diagnostics.Stopwatch.StartNew();
        ushort[] E_bf16 = LoadRawBF16(refMap, "model.embed_tokens.weight",
                                       (long)vocabSize * dModel);

        // ─── EMBEDS (unary per token) ───────────────────────────────────
        // ADR 0056:163 — (text_entity, embed_dim), per-cell magnitude, one
        // instance. Reduce embed row to a per-token scalar via L2 norm of
        // the dModel-wide row. Emit one attestation per token, object NULL.
        var embedAccum = ReducePerCellMagnitude(E_bf16, vocabSize, dModel);
        foreach (var c in EmitUnaryBatches(embedAccum, _kinds.Embeds, "embeds"))
        { yield return c; await Task.Yield(); }
        _log.LogInformation("phase=EMBEDS done: {N} tokens ({Ms} ms)", vocabSize, phase.ElapsedMilliseconds);

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
            _log.LogInformation("phase=OUTPUT_PROJECTS done ({Ms} ms)", phase.ElapsedMilliseconds);
        }
        phase.Restart();

        // ─── Q_PROJECTS (binary text×text) — exact, threshold (NOT top-k), streamed ───
        // Temporary isolation gate: LAPLACE_SKIP_QK=1 ingests the (bounded, fast) unary
        // kinds only, to verify the kernel rewrite end-to-end and isolate the O(vocab²)
        // QK cost. Removed once QK scaling is decided.
        if (Environment.GetEnvironmentVariable("LAPLACE_SKIP_QK") != "1")
        {
        // ADR 0056:157 — q_proj[i,:]·k_proj[j,:]ᵀ per (layer, head). The engine kernel
        // emits every |q·k| > NoiseFloor pair in bounded query-row windows; we stream
        // them straight to attestation batches. No cross-layer Dictionary, no vocab×k
        // pinned buffer (the prior 80 GB OOM). Cross-instance consensus is the DB's job
        // (Glicko consensus-upsert chunk); the interim relies on content-addressed
        // dedup at the writer. NOTE: with top-k removed, QK volume is governed solely by
        // NoiseFloor — calibrated empirically against ingest survivor counts.
        float[] E_f32 = LoadRawBF16AsF32(refMap, "model.embed_tokens.weight",
                                          (long)vocabSize * dModel);
        int queriesPerKv = nHeads / Math.Max(1, nKvHeads);
        var qkBuf = new QkPairF64[1 << 20];   // 16 MB bounded scratch, reused
        // Project Q+K through the embedding ONCE per layer for ALL heads (streams E a single
        // time/layer), then score each head from the caches — instead of re-streaming the
        // ~250 MB E once per (head) inside the pruned kernel (~64×/layer of memory traffic).
        // q_cache [vocab][nHeads][headDim], k_cache [vocab][nKv][headDim], f64; allocated
        // ONCE and reused across layers (TinyLlama: q_cache ~512 MB, k_cache ~64 MB). The
        // cache holds the IDENTICAL compensated projections the pruned kernel computes, so
        // per-head scoring (ScoreQkHeadCached) is bit-identical to the per-head pruned kernel.
        var qCache = new double[(long)vocabSize * nHeads   * headDim];
        var kCache = new double[(long)vocabSize * nKvHeads * headDim];
        // Diagnostic: LAPLACE_QK_BENCH=1 runs the kernel for every head and accumulates
        // native compute time + pair counts but SKIPS emit/marshal/DB — isolates the
        // native projection+scoring cost from the managed pipeline.
        bool qkBench = Environment.GetEnvironmentVariable("LAPLACE_QK_BENCH") == "1";
        var kernelSw = new System.Diagnostics.Stopwatch();
        long kernelPairs = 0; long kernelCalls = 0;
        for (int layer = 0; layer < _recipe.NumLayers; layer++)
        {
            ct.ThrowIfCancellationRequested();
            string qName = $"model.layers.{layer}.self_attn.q_proj.weight";
            string kName = $"model.layers.{layer}.self_attn.k_proj.weight";
            if (!refMap.ContainsKey(qName) || !refMap.ContainsKey(kName)) continue;
            float[] qW = LoadRawBF16AsF32(refMap, qName, (long)nHeads   * headDim * dModel);
            float[] kW = LoadRawBF16AsF32(refMap, kName, (long)nKvHeads * headDim * dModel);
            // Stream E once: project all heads' Q + all kv heads' K into the caches.
            kernelSw.Start();
            ProjectLayerQk(E_f32, vocabSize, dModel, qW, nHeads, kW, nKvHeads, headDim,
                           qCache, kCache);
            kernelSw.Stop();
            for (int h = 0; h < nHeads; h++)
            {
                int kvHead = h / queriesPerKv;
                int q0 = 0;
                while (q0 < vocabSize)
                {
                    // One call per head normally (whole vocab) so the cached scorer builds
                    // its key-norm table ONCE; the overflow path shrinks the window only if
                    // a head's survivors ever exceed the buffer (rare at this floor).
                    int win = vocabSize - q0;
                    long n;
                    int overflow;
                    while (true)
                    {
                        kernelSw.Start();
                        n = QkWindowCached(qCache, nHeads, kCache, nKvHeads, vocabSize, headDim,
                                           h, kvHead, QkNoiseFloor, q0, q0 + win, qkBuf, out overflow);
                        kernelSw.Stop();
                        kernelCalls++;
                        if (overflow == 0) break;
                        win = Math.Max(1, win / 2);   // shrink window until the batch fits
                    }
                    kernelPairs += n;
                    if (!qkBench)
                        foreach (var c in EmitQkBatches(qkBuf, (int)n)) yield return c;
                    await Task.Yield();
                    q0 += win;
                }
            }
            _log.LogInformation(
                "phase=QK layer {Layer}/{Total}: kernel {KMs} ms cumulative ({Calls} calls), "
                + "{Pairs:N0} pairs above floor, wall {WallMs} ms{Bench}",
                layer + 1, _recipe.NumLayers, kernelSw.ElapsedMilliseconds, kernelCalls,
                kernelPairs, phase.ElapsedMilliseconds, qkBench ? " [BENCH: emit skipped]" : "");
        }
        _log.LogInformation("phase=QK done: {KMs} ms kernel, {Pairs:N0} pairs above floor, {WallMs} ms wall",
            kernelSw.ElapsedMilliseconds, kernelPairs, phase.ElapsedMilliseconds);

        } // end QK isolation gate (LAPLACE_SKIP_QK)
        phase.Restart();

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
                kvDim, dModel, E_bf16, dModel, vocabSize, perTokenAccum[0].perToken);
            AggregateLayerThroughEmbed(refMap, p + "self_attn.o_proj.weight",
                dModel, attnOut, E_bf16, dModel, vocabSize, perTokenAccum[1].perToken);
            AggregateLayerThroughEmbed(refMap, p + "mlp.gate_proj.weight",
                interm, dModel, E_bf16, dModel, vocabSize, perTokenAccum[2].perToken);
            AggregateLayerThroughEmbed(refMap, p + "mlp.up_proj.weight",
                interm, dModel, E_bf16, dModel, vocabSize, perTokenAccum[3].perToken);
            AggregateLayerThroughEmbed(refMap, p + "mlp.down_proj.weight",
                dModel, interm, E_bf16, dModel, vocabSize, perTokenAccum[4].perToken);
            await Task.Yield();
        }

        for (int i = 0; i < perTokenAccum.Length; i++)
        {
            var (kindId, perToken, label) = perTokenAccum[i];
            foreach (var c in EmitUnaryBatches(perToken, kindId, label))
            { yield return c; await Task.Yield(); }
        }
        _log.LogInformation("phase=unary V/O/GATES/UP/DOWN done: {Layers} layers × 5 tensors ({Ms} ms)",
            _recipe.NumLayers, phase.ElapsedMilliseconds);

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
    private static unsafe double[] ReducePerCellMagnitude(ushort[] tensorBf16, int rows, int cols)
    {
        // Exact, deterministic, TBB+SIMD engine kernel (Neumaier-compensated f64,
        // fixed column order) — bit-parity verified against this former scalar path.
        var result = new double[rows];
        fixed (ushort* tp = tensorBf16)
        fixed (double* op = result)
        {
            int rc = SynthInterop.ComputePerTokenL2Magnitude(tp, (nuint)rows, (nuint)cols, op);
            if (rc != 0)
                throw new InvalidOperationException($"compute_per_token_l2_magnitude returned {rc}");
        }
        return result;
    }

    /* Aggregate one V/O/G/U/D-style layer through the embedding into a
     * per-token contribution: project E @ |W| → per-token magnitude (sum
     * across the hidden/intermediate axis). Add to the per-token accumulator.
     * Memory-bounded — bf16 tensor + accumulator both small relative to model. */
    private unsafe void AggregateLayerThroughEmbed(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string tensorName, int outDim, int inDim,
        ushort[] E_bf16, int dModel, int vocab, double[] perTokenAccum)
    {
        if (!refMap.ContainsKey(tensorName)) return;
        // Exact, deterministic, TBB+SIMD engine kernel — perInDim[i]=Σ_o|W[o,i]| then
        // projection E·perInDim (inDim==dModel) or uniform fallback. Bit-parity verified
        // against this former scalar path. (bf16-only tensors for now; F16/F32 dtype
        // generality lands with the TensorDtypeDecoder chunk for Phi-2.)
        ushort[] w = LoadRawBF16(refMap, tensorName, (long)outDim * inDim);
        var outv = new double[vocab];
        fixed (ushort* ep = E_bf16)
        fixed (ushort* wp = w)
        fixed (double* op = outv)
        {
            int rc = SynthInterop.ComputeProjectionPerToken(
                ep, (nuint)vocab, (nuint)dModel, wp, (nuint)outDim, (nuint)inDim, op);
            if (rc != 0)
                throw new InvalidOperationException($"compute_projection_per_token returned {rc}");
        }
        for (int t = 0; t < vocab; t++) perTokenAccum[t] += outv[t];
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
            var row = AttestationFactory.CreateWeighted(
                subj, kindId, obj: null, _sourceId, contextId: null,
                tier: KindValueTier.T9, trust: TrustClass.AiModelProbeTier7,
                magnitude: perTokenAccum[tokenIdx], floor: NoiseFloor);
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
    /* Stream above-threshold QK pairs (from a kernel window) as Q_PROJECTS attestation
     * batches. No C# dedup set — that would be unbounded; content-addressed dedup /
     * consensus is the writer's job. */
    private IEnumerable<SubstrateChange> EmitQkBatches(QkPairF64[] buf, int n)
    {
        SubstrateChangeBuilder? bb = null;
        int inBatch = 0, batchIdx = 0;
        int tokCount = _tokens.Count;
        for (int i = 0; i < n; i++)
        {
            uint qi = buf[i].QueryIdx, kj = buf[i].KeyIdx;
            if (qi >= (uint)tokCount || kj >= (uint)tokCount) continue;
            var row = AttestationFactory.CreateWeighted(
                _tokens[(int)qi].EntityId, _kinds.QProjects, _tokens[(int)kj].EntityId,
                _sourceId, contextId: null,
                tier: KindValueTier.T9, trust: TrustClass.AiModelProbeTier7,
                magnitude: buf[i].Score, floor: QkNoiseFloor);

            bb ??= new SubstrateChangeBuilder(_sourceId, $"q_projects/batch-{batchIdx}",
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: AttBatchSize);
            bb.AddAttestation(row);
            if (++inBatch >= AttBatchSize) { yield return bb.Build(); bb = null; inBatch = 0; batchIdx++; }
        }
        if (bb != null) yield return bb.Build();
    }

    /* Project a layer's Q + K through the embedding ONCE for ALL heads, streaming E a
     * single time, into the reusable f64 caches. qCache [vocab][nHeads][headDim],
     * kCache [vocab][nKvHeads][headDim], row-major. The per-element compensated projection
     * (fixed order) is identical to the pruned kernel's, so scoring from these caches is
     * bit-identical. Replaces the prior per-head E re-streaming. */
    private static unsafe void ProjectLayerQk(
        float[] eF32, int vocab, int dModel, float[] qW, int nHeads, float[] kW, int nKv,
        int headDim, double[] qCache, double[] kCache)
    {
        int rc;
        fixed (float* ep = eF32)
        fixed (float* qp = qW)
        fixed (float* kp = kW)
        fixed (double* qc = qCache)
        fixed (double* kc = kCache)
        {
            rc = SynthInterop.ProjectQkLayer(
                ep, (nuint)vocab, (nuint)dModel, qp, (nuint)nHeads, kp, (nuint)nKv,
                (nuint)headDim, qc, kc);
        }
        if (rc != 0) throw new InvalidOperationException($"project_qk_layer returned {rc}");
    }

    /* One (layer, head) query-row window scored from the pre-projected caches (no E touch).
     * Fills buf with every above-floor pair for query rows [q0, q1); sets overflow=1
     * (returning the whole-row prefix that fit) when buf is too small — caller shrinks
     * the window and retries. Bit-identical to the per-head pruned kernel. */
    private static unsafe long QkWindowCached(
        double[] qCache, int nHeads, double[] kCache, int nKv, int vocab, int headDim,
        int head, int kvHead, double floor, int q0, int q1, QkPairF64[] buf, out int overflow)
    {
        int of;
        long n;
        fixed (double* qc = qCache)
        fixed (double* kc = kCache)
        fixed (QkPairF64* bp = buf)
        {
            // Sub-quadratic exact (Cauchy-Schwarz norm-pruned) over the cached projections;
            // bit-identical to compute_qk_pairs_above_threshold_pruned for this head.
            n = SynthInterop.ScoreQkHeadCached(
                qc, (nuint)nHeads, kc, (nuint)nKv, (nuint)vocab, (nuint)headDim,
                (nuint)head, (nuint)kvHead, floor, (nuint)q0, (nuint)q1, bp, (nuint)buf.Length, &of);
        }
        overflow = of;
        if (n < 0) throw new InvalidOperationException("score_qk_head_cached returned -1");
        return n;
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
