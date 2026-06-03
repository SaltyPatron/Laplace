using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Weight-tensor ETL for transformer models.
///
///  - <see cref="EmitCircuitMemoriesAsync"/> — every interior circuit (QK / OV / FFN)
///    projected through the embedding (E / E_U) once, contracted tile-by-tile via the
///    engine (ModelCircuitEdges → bilinear_edges_tile, exact f64), emitted as SIGNED
///    token×token Glicko-2 observations (score = ½(1+tanh(m/M)), per-arena M measured
///    from the arena's own magnitudes, (layer,head) witness in context_id).
///    Nonlinearities (softmax, SiLU/SwiGLU gating, norms) are runtime, never attested.
///  - <see cref="EmitS3MorphAsync"/> — embed_tokens placed on the shared Unicode S³
///    frame as Projection physicalities (the per-model embed species).
///
/// What this ETL is NOT:
/// - container decomposition (IContainerParser does that)
/// - dtype decoding (the generic dtype→f32 dispatch below decodes, never detects)
/// - vocab ingest (the tokenizer parser does that, BEFORE this runs)
/// - hashing (HashComposer does that, BEFORE this runs)
/// - DB writes (SubstrateCRUD does that, AFTER this yields)
/// - running the model (the substrate doesn't load + doesn't execute)
/// </summary>
public sealed class WeightTensorETL
{
    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _witnessType;
    private readonly Hash128 _sourceId;
    private readonly IReadOnlyList<SafetensorsContainerParser.TensorReference> _refs;
    private readonly ILogger _log;

    public WeightTensorETL(
        string modelDir,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId,
        Hash128 witnessType,
        ILogger? log = null)
    {
        _recipe      = recipe;
        _tokens      = tokens;
        _sourceId    = sourceId;
        _witnessType = witnessType;
        _log         = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        // Sharded-aware: union of every *.safetensors shard; each tensor carries its FilePath.
        _refs        = SafetensorsContainerParser.ParseModel(modelDir);
    }

    /// <summary>
    /// Placement axis: morph <c>embed_tokens</c> onto the shared S³ Unicode frame and emit
    /// one Projection physicality per token — the per-model embed species. Streams the
    /// embedding table; never a recompute.
    /// </summary>
    public async IAsyncEnumerable<SubstrateChange> EmitS3MorphAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
            _refs.Count, StringComparer.Ordinal);
        foreach (var r in _refs) refMap[r.Name] = r;
        int vocabSize = _recipe.VocabSize, dModel = _recipe.HiddenSize;
        float[] embed = LoadRawBF16AsF32(refMap, "model.embed_tokens.weight",
            (long)vocabSize * dModel);
        var morph = new TokenS3Morph(embed, vocabSize, dModel, _tokens, _sourceId, _log);
        foreach (var change in morph.Emit())
        {
            ct.ThrowIfCancellationRequested();
            yield return change;
            await Task.Yield();
        }
    }

    /// <summary>
    /// Relation axis: ALL interior circuits as signed token×token attestations through the
    /// embedding. Circuits are detected generically by <see cref="ModelGeometry"/> from
    /// tensor SHAPE (no per-family code). Each circuit's two sides are projected through
    /// E / E_U ONCE (<see cref="ModelCircuitEdges.ProjectCircuit"/>), then the operator
    /// M = Left·Rightᵀ is contracted tile-by-tile (engine <c>bilinear_edges_tile</c>,
    /// exact f64 dgemm) and emitted as Glicko-2 observations — score ½(1+tanh(m/M)) from
    /// the SIGNED coupling, per-arena M measured, (layer,head) witness as context_id
    /// (evidence keeps provenance; consensus drops it). Never argmax, never top-k,
    /// never per-token magnitude.
    /// </summary>
    public async IAsyncEnumerable<SubstrateChange> EmitCircuitMemoriesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
            _refs.Count, StringComparer.Ordinal);
        foreach (var r in _refs) refMap[r.Name] = r;

        int vocab = _recipe.VocabSize, dModel = _recipe.HiddenSize, interm = _recipe.IntermediateSize;
        int nHeads = _recipe.NumHeads, nKv = _recipe.NumKvHeads, headDim = dModel / nHeads;
        int qPerKv = nHeads / Math.Max(1, nKv);

        var tokById = new LlamaTokenizerParser.TokenRecord?[vocab];
        foreach (var rec in _tokens)
            if (rec.TokenId >= 0 && rec.TokenId < vocab) tokById[rec.TokenId] = rec;

        var geo = ModelGeometry.Detect(vocab, dModel, nHeads, nKv, headDim, interm, _recipe.NumLayers,
            _refs.Select(r => (r.Name, r.Shape)));
        string embedName  = geo.EmbeddingName  ?? "model.embed_tokens.weight";
        string unembName  = geo.UnembeddingName ?? embedName;
        float[] E   = LoadRawBF16AsF32(refMap, embedName, (long)vocab * dModel);
        float[] E_U = unembName != embedName ? LoadRawBF16AsF32(refMap, unembName, (long)vocab * dModel) : E;
        // Arena calibration fidelity (see CalibrateArena). NOTE: the θ cut this budget
        // derives is an OPEN violation of "no floors / budgets ever" — the set→set
        // Merkle-hyperedge emit (dedup by construction) is the volume answer that
        // replaces it. Tracked in docs/INGESTION-STATUS.md "Still open".
        double fidelity = 0.90;
        if (double.TryParse(Environment.GetEnvironmentVariable("LAPLACE_CIRCUIT_FIDELITY"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var fv) && fv > 0.0 && fv <= 1.0)
            fidelity = fv;
        Func<int, Hash128?> tokEnt = i => tokById[i]?.EntityId;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var counters = new long[2];   // [0]=memories, [1]=relations

        // Witness entities — emitted ONCE so every attestation's context_id FK resolves.
        // DRIVEN BY the detected circuits so the witness tuple (source, layer, head, expert)
        // matches what the attestation loop references exactly: attention (QK/OV) → one witness
        // per (layer, head) with expert = -1 (attention is shared, not per-expert); FFN → one
        // witness per (layer, expert) with head = -1 (MoE: a distinct witness per expert; dense:
        // expert = -1). De-duped because QK and OV of the same head share a witness.
        {
            var wb = new SubstrateChangeBuilder(_sourceId, "model/witnesses",
                entityCapacity: geo.Circuits.Count * (nHeads + 1), physicalityCapacity: 0, attestationCapacity: 0);
            var seenWit = new HashSet<Hash128>();
            foreach (var circ in geo.Circuits)
            {
                if (circ.Kind == ModelGeometry.CircuitKind.FFN)
                {
                    var id = WitnessId(_sourceId, circ.Layer, -1, circ.Expert);
                    if (seenWit.Add(id)) wb.AddEntity(new EntityRow(id, 0, _witnessType, _sourceId));
                }
                else
                {
                    for (int h = 0; h < nHeads; h++)
                    {
                        var id = WitnessId(_sourceId, circ.Layer, h, circ.Expert);
                        if (seenWit.Add(id)) wb.AddEntity(new EntityRow(id, 0, _witnessType, _sourceId));
                    }
                }
            }
            yield return wb.Build();
        }
        _log.LogInformation("phase=circuits: {N} circuits detected (generic, shape-based); fidelity budget={Fidelity} ({Ms} ms)",
            geo.Circuits.Count, fidelity, sw.ElapsedMilliseconds);

        const double modelTrust = 0.50;   // SourceTrust.AiModelProbe; kind_rank from the registry

        ModelCircuitEdges.CircuitForm FormOf(ModelGeometry.CircuitKind k) => k switch {
            ModelGeometry.CircuitKind.QK => ModelCircuitEdges.CircuitForm.Qk,
            ModelGeometry.CircuitKind.OV => ModelCircuitEdges.CircuitForm.Ov,
            _                            => ModelCircuitEdges.CircuitForm.Ffn };
        string KindName(ModelGeometry.CircuitKind k) => k switch {
            ModelGeometry.CircuitKind.QK => "ATTENDS",
            ModelGeometry.CircuitKind.OV => "OV_RELATES",
            _                            => "COMPLETES_TO" };

        // Project a circuit to its low-rank operands M = encProj·decProjᵀ (the only magnitude
        // cut so far is the bf16 the weights carry; M is contracted per row tile downstream,
        // never materialized dense over the vocab).
        (double[] encProj, int rEnc, double[] decProj, int rDec) ProjectCirc(
            string encodeName, string decodeName, ModelGeometry.CircuitKind kind)
        {
            var encRef = refMap[encodeName]; var decRef = refMap[decodeName];
            float[] enc = LoadRawBF16AsF32(refMap, encodeName, (long)encRef.Shape[0] * encRef.Shape[1]);
            float[] dec = LoadRawBF16AsF32(refMap, decodeName, (long)decRef.Shape[0] * decRef.Shape[1]);
            return ModelCircuitEdges.ProjectCircuit(FormOf(kind), enc, encRef.Shape[0],
                dec, decRef.Shape[0], decRef.Shape[1], E, E_U, vocab, dModel);
        }
        static System.Collections.Generic.IEnumerable<int> PickEvenly(int count, int n)
        {
            if (n <= 1 || count <= 1) { if (count > 0) yield return 0; yield break; }
            if (count <= n) { for (int i = 0; i < count; i++) yield return i; yield break; }
            for (int i = 0; i < n; i++) yield return (int)((long)i * (count - 1) / (n - 1));
        }

        // PASS 1 — per-ARENA calibration. M (the §10 tanh scale) and θ are frozen PER KIND,
        // pooled across a bounded sample of the kind's witness sub-ops (≤3 layers × ≤4 heads).
        // An arena is a KIND (ATTENDS/OV_RELATES/COMPLETES_TO), not a circuit; every head/layer
        // is a WITNESS into it and must share ONE scale, else the consensus can't tell a strong
        // witness from a weak one. Calibration is bounded — never the full vocab.
        var arena = new System.Collections.Generic.Dictionary<ModelGeometry.CircuitKind, (double M, double theta)>();
        foreach (var grp in geo.Circuits.GroupBy(c => c.Kind))
        {
            ct.ThrowIfCancellationRequested();
            var circs = grp.OrderBy(c => c.Layer).ToList();
            var samples = new System.Collections.Generic.List<(double[] left, double[] right, int r)>();
            foreach (int li in PickEvenly(circs.Count, 3))
            {
                var circ = circs[li];
                var (encP, rE, decP, rD) = ProjectCirc(circ.Encode, circ.Decode, circ.Kind);
                if (circ.Kind == ModelGeometry.CircuitKind.FFN)
                    samples.Add((encP, decP, rE));
                else
                    foreach (int h in PickEvenly(nHeads, 4))
                    {
                        int kvHead = h / qPerKv;
                        int encHead = circ.Kind == ModelGeometry.CircuitKind.QK ? h : kvHead;
                        int decHead = circ.Kind == ModelGeometry.CircuitKind.QK ? kvHead : h;
                        samples.Add((ModelCircuitEdges.SliceHead(encP, vocab, rE, encHead, headDim),
                                     ModelCircuitEdges.SliceHead(decP, vocab, rD, decHead, headDim), headDim));
                    }
            }
            var (M, theta, recall) = ModelCircuitEdges.CalibrateArena(samples, vocab, fidelity);
            arena[grp.Key] = (M, theta);
            _log.LogInformation("phase=calibrate {Kind}: arena M={M:E3} θ={C:F2}·M recall={Recall:P1} ({N} sub-ops)",
                grp.Key, M, M > 0.0 ? theta / M : 0.0, recall, samples.Count);
        }

        // PASS 2 — emit every witness's edges scored against its arena's FROZEN (M, θ).
        foreach (var circ in geo.Circuits)
        {
            ct.ThrowIfCancellationRequested();
            var (M, theta) = arena[circ.Kind];
            if (M <= 0.0) continue;
            string kindName = KindName(circ.Kind);
            var (encProj, rEnc, decProj, rDec) = ProjectCirc(circ.Encode, circ.Decode, circ.Kind);

            if (circ.Kind == ModelGeometry.CircuitKind.FFN)
            {
                Hash128 wit = WitnessId(_sourceId, circ.Layer, -1, circ.Expert);
                // STREAM bounded changes — the emitter flushes the COPY in chunks so a circuit
                // never becomes one multi-GB intent; the witness entity rides every chunk (FK).
                foreach (var chg in ModelCircuitEdges.Emit(encProj, decProj, vocab, rEnc, kindName,
                             M, theta, modelTrust, tokEnt, _sourceId, wit, _witnessType,
                             $"circuit/L{circ.Layer}/ffn"))
                {
                    counters[1] += chg.Attestations.Length;
                    yield return chg; await Task.Yield();
                }
            }
            else  // attention: one witness per (layer, head), all scored against the arena scale
            {
                for (int h = 0; h < nHeads; h++)
                {
                    ct.ThrowIfCancellationRequested();
                    int kvHead = h / qPerKv;
                    int encHead = circ.Kind == ModelGeometry.CircuitKind.QK ? h : kvHead;  // QK: Q has nHeads; OV: V has nKv
                    int decHead = circ.Kind == ModelGeometry.CircuitKind.QK ? kvHead : h;  // QK: K has nKv; OV: O has nHeads
                    double[] left  = ModelCircuitEdges.SliceHead(encProj, vocab, rEnc, encHead, headDim);
                    double[] right = ModelCircuitEdges.SliceHead(decProj, vocab, rDec, decHead, headDim);
                    Hash128 wit = WitnessId(_sourceId, circ.Layer, h, circ.Expert);
                    foreach (var chg in ModelCircuitEdges.Emit(left, right, vocab, headDim, kindName,
                                 M, theta, modelTrust, tokEnt, _sourceId, wit, _witnessType,
                                 $"circuit/L{circ.Layer}/{kindName}/h{h}"))
                    {
                        counters[1] += chg.Attestations.Length;
                        yield return chg; await Task.Yield();
                    }
                }
            }
            _log.LogInformation("phase=circuits L{Layer} {Kind}: {Rel:N0} edges (cum), wall {Ms} ms",
                circ.Layer, circ.Kind, counters[1], sw.ElapsedMilliseconds);
        }
        _log.LogInformation("phase=circuits done: {Rel:N0} edges, {Ms} ms", counters[1], sw.ElapsedMilliseconds);
    }

    /* Per-(layer, head, expert) witness id — the model-circuit provenance carried as the
     * attestation context_id. head = -1 is the layer's FFN witness; expert = -1 is dense /
     * shared attention, expert ≥ 0 is an MoE expert (so two experts in one layer do NOT
     * collapse to one witness). Deterministic per (source, layer, head, expert). */
    private static Hash128 WitnessId(Hash128 source, int layer, int head, int expert)
    {
        Span<byte> b = stackalloc byte[16 + 4 + 4 + 4];
        source.WriteBytes(b.Slice(0, 16));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.Slice(16, 4), layer);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.Slice(20, 4), head);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.Slice(24, 4), expert);
        return Hash128.Blake3(b);
    }

    /* Load helpers. */
    private byte[] LoadRawBytes(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string name)
    {
        var tref = refMap[name];
        byte[] rawBytes = new byte[tref.DataLength];
        // Open the SHARD this tensor lives in (sharded-aware); FilePath is set by ParseModel.
        using var fs = new FileStream(tref.FilePath, FileMode.Open, FileAccess.Read,
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

    private float[] LoadRawBF16AsF32(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string name, long expectedElements)
    {
        var tref = refMap[name];
        byte[] raw = LoadRawBytes(refMap, name);
        float[] result = new float[expectedElements];
        // Generic dtype → f32 dispatch. The dtype is self-describing (safetensors header),
        // so handling a new one is just a decoder, never detection. Covers every safetensors
        // numeric/bool dtype incl. the float8 quant formats; fails loud (never zeros) on
        // anything else (e.g. GGUF block-quant, which is a different container entirely).
        long n = expectedElements;
        unsafe
        {
            fixed (byte* rp = raw)
            fixed (float* op = result)
            {
                switch (tref.Dtype)
                {
                    case "F32":  { float*  s = (float*)rp;  for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "F64":  { double* s = (double*)rp; for (long i = 0; i < n; i++) op[i] = (float)s[i]; break; }
                    case "F16":  { ushort* s = (ushort*)rp; for (long i = 0; i < n; i++) op[i] = (float)BitConverter.UInt16BitsToHalf(s[i]); break; }
                    case "BF16": { ushort* s = (ushort*)rp; for (long i = 0; i < n; i++) { uint b = (uint)s[i] << 16; float f; Buffer.MemoryCopy(&b, &f, 4, 4); op[i] = f; } break; }
                    case "F8_E5M2": { byte* s = rp; for (long i = 0; i < n; i++) op[i] = DecodeE5M2(s[i]); break; }
                    case "F8_E4M3": { byte* s = rp; for (long i = 0; i < n; i++) op[i] = DecodeE4M3(s[i]); break; }
                    case "I64":  { long*  s = (long*)rp;  for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I32":  { int*   s = (int*)rp;   for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I16":  { short* s = (short*)rp; for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I8":   { sbyte* s = (sbyte*)rp; for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "U8":   for (long i = 0; i < n; i++) op[i] = rp[i]; break;
                    case "BOOL": for (long i = 0; i < n; i++) op[i] = rp[i] != 0 ? 1f : 0f; break;
                    default:
                        throw new NotSupportedException(
                            $"tensor '{name}' dtype '{tref.Dtype}' has no decoder. safetensors numeric/bool " +
                            "are covered; GGUF block-quant (Q4_K/Q6_K/…) is a separate container needing its " +
                            "own dequantizer. Refusing to ingest zeros.");
                }
            }
        }
        return result;
    }

    /* float8 E5M2 (1-5-2, bias 15) → f32. Same 5-bit exponent/bias as IEEE half, so widen
     * the mantissa into a half and let the half decoder handle normals/subnormals/inf/nan. */
    private static float DecodeE5M2(byte b)
    {
        int sign = (b >> 7) & 1, exp = (b >> 2) & 0x1F, mant = b & 0x3;
        ushort half = (ushort)((sign << 15) | (exp << 10) | (mant << 8));
        return (float)BitConverter.UInt16BitsToHalf(half);
    }

    /* float8 E4M3FN (1-4-3, bias 7, no inf; S.1111.111 = NaN; max 448) → f32. */
    private static float DecodeE4M3(byte b)
    {
        int sign = (b >> 7) & 1, exp = (b >> 3) & 0xF, mant = b & 0x7;
        float v;
        if (exp == 0)                 v = mant * MathF.ScaleB(1f, -9);          // subnormal: mant/8·2⁻⁶
        else if (exp == 15 && mant == 7) v = float.NaN;
        else                          v = (1f + mant * 0.125f) * MathF.ScaleB(1f, exp - 7);
        return sign != 0 ? -v : v;
    }
}
