using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

















public sealed class ModelTokenEdgeETL
{
    public readonly record struct EdgeAttestationRecord(
        Hash128 Subject, Hash128 Object, Hash128 TypeId, long ScoreFp, double Weight);

    public static void StageEdgeAttestation(
        SubstrateChangeBuilder b, EdgeAttestationRecord rec, Hash128 sourceId) =>
        b.AddAttestation(NativeAttestation.Aggregated(
            rec.Subject, rec.TypeId, rec.Object, sourceId, null, 1, rec.ScoreFp, rec.Weight));

    private const int RowTile = 256;
    private const int AttsPerChange = 200_000;
    private const int EigTargetDim = 64;
    private const int TopPairsPerCircuit = 64;




    private const double Theta = 0.0;

    // Per-subject-row edge budget per plane. Without this, a zero floor emits every
    // positive-scoring pair — O(V²) per plane, per head — which is computationally
    // unbounded on a real vocabulary (V=32k ⇒ ~5×10⁸ candidate pairs per plane).
    // Per-row top-|score| selection bounds every plane at V×k while keeping exactly
    // the strongest partners, and makes EstimateMatchupUnits' partner assumption true
    // instead of 3-6 orders optimistic. ATTENDS divides the budget across heads so a
    // layer's total attention budget matches the other planes'.
    internal const int EdgeTopK = 64;






    private const string PlanesMode = "all";
    private static bool RunSimilarity => PlanesMode is "all" or "similarity" or "shallow";
    private static bool RunContinues => PlanesMode is "all" or "continues" or "shallow";
    private static bool RunLayers => PlanesMode == "all";



    private const bool NormFold = true;

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
        int commitEpoch,
        ISubstrateReader? reader,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
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


        float[] embed = WeightTensorETL.LoadTensorF32(refMap, embedRole.Name, (long)vocab * d);
        var Af = new float[(long)n * d];
        for (int i = 0; i < n; i++)
            Array.Copy(embed, (long)rowOfToken[i] * d, Af, (long)i * d, d);


        if (RunSimilarity)
            await foreach (var change in EmitSimilarityPlane(Af, ents, n, d, commitEpoch, reader, options, ct))
                yield return change;

        if (!_manifest.TextPlanesRunnable)
        {
            _log.LogInformation("phase=edges: coverage={Cov} modality={Mod}; embedding-plane only",
                _manifest.Coverage, _manifest.Modality);
            yield break;
        }


        if (RunContinues)
            await foreach (var change in EmitContinuesPlane(Af, ents, rowOfToken, n, d, commitEpoch, refMap, reader, options, ct))
                yield return change;

        if (!RunLayers) yield break;


        var Ad = new double[(long)n * d];
        for (long i = 0; i < (long)n * d; i++) Ad[i] = Af[i];

        int layers = _manifest.LayerCount;
        for (int L = 0; L < layers; L++)
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var change in EmitAttentionLayer(L, Af, Ad, ents, n, d, commitEpoch, refMap, reader, options, ct))
                yield return change;
            await foreach (var change in EmitOvLayer(L, Af, Ad, ents, n, d, commitEpoch, refMap, reader, options, ct))
                yield return change;
            await foreach (var change in EmitMlpLayer(L, Ad, ents, n, d, commitEpoch, refMap, reader, options, ct))
                yield return change;
        }
    }


    private async IAsyncEnumerable<SubstrateChange> EmitSimilarityPlane(
        float[] Af, List<Hash128> ents, int n, int d, int commitEpoch,
        ISubstrateReader? reader, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int kmax = Math.Min(EigTargetDim, Math.Min(d, n));
        if (kmax < 2) yield break;

        var sw = System.Diagnostics.Stopwatch.StartNew();



        var U = new float[(long)n * kmax];
        var S = new float[kmax];
        var Vt = new float[(long)kmax * d];
        var Ac = CenteredCopyF(Af, n, d);
        int rc = RunSvd(Ac, n, d, kmax, U, S, Vt, out int rank);
        if (rc == -2)
            throw new InvalidOperationException(
                "tensor_svd_truncate rc=-2: MKL/LAPACK unavailable in laplace_synthesis — "
                + "rebuild with setvars and -DLAPLACE_SYNTHESIS_REQUIRE_MKL=ON.");
        if (rc != 0) { _log.LogWarning("phase=edges: tensor_svd_truncate rc={Rc}; skipping similarity", rc); yield break; }
        if (rank < 2) { _log.LogWarning("phase=edges: SVD rank {R}<2; skipping similarity", rank); yield break; }

        var Y = new double[(long)n * rank];
        for (int i = 0; i < n; i++)
            for (int t = 0; t < rank; t++)
                Y[(long)i * rank + t] = (double)U[(long)i * kmax + t] * S[t];
        NormRows(Y, n, rank);
        _log.LogInformation("phase=edges: SVD reduced {N:N0} tokens d={D}->rank {R} (tol 1%), {Sec:F1}s; "
            + "folding SIMILAR_TO (every pair, no floor)", n, d, rank, sw.Elapsed.TotalSeconds);

        var typeId = RelationTypeRegistry.Resolve("SIMILAR_TO").Id;
        double weight = WeightFor("SIMILAR_TO");
        await foreach (var change in EmitBilinearPairs(
            Y, Y, n, rank, typeId, weight, ents, ents, commitEpoch,
            new CircuitDescriptor(Layer: -1, Head: -1, Plane: "similarity", RelationName: "SIMILAR_TO"),
            sampleForDecoder: false, topK: EdgeTopK, reader, options, ct))
            yield return change;
    }







    private async IAsyncEnumerable<SubstrateChange> EmitContinuesPlane(
        float[] Af, List<Hash128> ents, List<int> rowOfToken, int n, int d, int commitEpoch,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        ISubstrateReader? reader, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _manifest.Config;
        var embedRole = _manifest.Embedding;
        var lmRole = _manifest.LmHead;
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




        var Ed = new double[(long)n * d];
        var Ud = new double[(long)n * d];
        for (int i = 0; i < n; i++)
        {
            long erow = (long)i * d, urow = (long)rowOfToken[i] * d;
            for (int t = 0; t < d; t++) { Ed[erow + t] = Af[erow + t]; Ud[erow + t] = Ufull[urow + t]; }
        }
        CenterColumns(Ed, n, d);
        CenterColumns(Ud, n, d);
        NormRows(Ed, n, d);
        NormRows(Ud, n, d);

        var typeId = RelationTypeRegistry.Resolve("CONTINUES_TO").Id;
        double weight = WeightFor("CONTINUES_TO");
        _log.LogInformation("phase=edges: folding CONTINUES_TO (LM-head direct path, untied, full d={D}) over {N} tokens", d, n);
        await foreach (var change in EmitBilinearPairs(
            Ed, Ud, n, d, typeId, weight, ents, ents, commitEpoch,
            new CircuitDescriptor(Layer: -1, Head: -1, Plane: "continues", RelationName: "CONTINUES_TO"),
            sampleForDecoder: true, topK: EdgeTopK, reader, options, ct))
            yield return change;
    }


    private async IAsyncEnumerable<SubstrateChange> EmitAttentionLayer(
        int L, float[] Af, double[] Ad, List<Hash128> ents, int n, int d, int commitEpoch,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        ISubstrateReader? reader, DecomposerOptions options,
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

        var typeId = RelationTypeRegistry.Resolve("ATTENDS").Id;
        double weight = WeightFor("ATTENDS");
        var Qh = new double[(long)n * hd];
        var Kh = new double[(long)n * hd];
        // The layer's ATTENDS budget is shared across heads so one layer's attention
        // plane emits ~V*EdgeTopK edges total, matching the OV/MLP planes.
        int perHeadK = Math.Max(1, EdgeTopK / Math.Max(1, H));
        for (int h = 0; h < H; h++)
        {
            ct.ThrowIfCancellationRequested();
            SliceHead(Q, Qh, n, attn, h, hd);
            SliceHead(Kexp, Kh, n, attn, h, hd);
            if (gQ is not null) ScaleColsD(Qh, n, hd, gQ);
            if (gK is not null) ScaleColsD(Kh, n, hd, gK);
            await foreach (var change in EmitBilinearPairs(
                Qh, Kh, n, hd, typeId, weight, ents, ents, commitEpoch,
                new CircuitDescriptor(L, h, "attention", "ATTENDS"), sampleForDecoder: true,
                topK: perHeadK, reader, options, ct))
                yield return change;
        }
    }


    private async IAsyncEnumerable<SubstrateChange> EmitOvLayer(
        int L, float[] Af, double[] Ad, List<Hash128> ents, int n, int d, int commitEpoch,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        ISubstrateReader? reader, DecomposerOptions options,
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

            fixed (double* pv = Vexp) fixed (float* pw = Wo) fixed (double* po = OVout)
                rco = DynInterop.ProjectEmbeddingD(pv, (nuint)n, (nuint)attn, pw, (nuint)d, po);
        }
        if (rcv != 0 || rce != 0 || rco != 0)
        { _log.LogWarning("phase=edges L{L}: OV projection rc=({A},{B},{C}); skipping", L, rcv, rce, rco); yield break; }

        NormRows(OVout, n, d);
        var En = (double[])Ad.Clone();
        NormRows(En, n, d);

        var typeId = RelationTypeRegistry.Resolve("OV_RELATES").Id;
        double weight = WeightFor("OV_RELATES");
        await foreach (var change in EmitBilinearPairs(
            OVout, En, n, d, typeId, weight, ents, ents, commitEpoch,
            new CircuitDescriptor(L, -1, "ov", "OV_RELATES"), sampleForDecoder: true,
            topK: EdgeTopK, reader, options, ct))
            yield return change;
    }


    private async IAsyncEnumerable<SubstrateChange> EmitMlpLayer(
        int L, double[] Ad, List<Hash128> ents, int n, int d, int commitEpoch,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        ISubstrateReader? reader, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _manifest.Config;
        var upRole = _manifest.Single(L, TensorRoleKind.MlpUp);
        var downRole = _manifest.Single(L, TensorRoleKind.MlpDown);
        var gateRole = _manifest.Single(L, TensorRoleKind.MlpGate);
        if (upRole is null || downRole is null) yield break;
        int I = cfg.IntermediateSize;
        if (I <= 0) yield break;

        double[]? gate = null, up, down;
        try
        {
            up = LoadDouble(refMap, upRole.Name, (long)I * d);
            down = LoadDouble(refMap, downRole.Name, (long)d * I);
            if (gateRole is not null) gate = LoadDouble(refMap, gateRole.Name, (long)I * d);
        }
        catch (Exception ex) { _log.LogWarning("phase=edges L{L}: MLP load failed: {Msg}", L, ex.Message); yield break; }



        if (NormFold)
        {
            var gPost = LoadGain(refMap, _manifest.PostAttnNorm(L), d);
            if (gPost is not null)
            {
                ScaleColsD(up, I, d, gPost);
                if (gate is not null) ScaleColsD(gate, I, d, gPost);
            }
        }

        var typeId = RelationTypeRegistry.Resolve("COMPLETES_TO").Id;
        double weight = WeightFor("COMPLETES_TO");
        var descriptor = new CircuitDescriptor(L, -1, "mlp", "COMPLETES_TO");

        long cap = (long)RowTile * n;
        var oR = new int[cap]; var oC = new int[cap]; var oV = new double[cap]; var oS = new long[cap];
        var collector = _classifier is null ? null : new TopPairCollector(TopPairsPerCircuit);

        long edges = 0;
        await foreach (var batch in IngestComposePipeline.RunAsync(
                           EnumerateFfnEdgeRecords(Ad, n, d, gate, up, down, I, ents, typeId, weight,
                               oR, oC, oV, oS, cap, collector, ct),
                           (rec, b) => StageEdgeAttestation(b, rec, _source),
                           _source, $"model/edges/mlp/L{L}", AttsPerChange, reader, options, ct,
                           commitEpoch, AttsPerChange))
        {
            edges += batch.Attestations.Length;
            yield return batch;
        }
        _log.LogInformation("phase=edges L{L}: {E:N0} COMPLETES_TO edges folded", L, edges);

        await foreach (var change in EmitClassifyChange(descriptor, collector, commitEpoch, reader, options, ct))
            yield return change;
    }



    private async IAsyncEnumerable<SubstrateChange> EmitBilinearPairs(
        double[] left, double[] right, int n, int r, Hash128 typeId, double weight,
        List<Hash128> rowEnts, List<Hash128> colEnts, int commitEpoch,
        CircuitDescriptor descriptor, bool sampleForDecoder, int topK,
        ISubstrateReader? reader, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        bool canonicalize = RelationTypeRegistry.Resolve(descriptor.RelationName).Symmetry
                            == RelationTypeRegistry.Symmetry.Symmetric;
        var collector = (_classifier is null || !sampleForDecoder) ? null : new TopPairCollector(TopPairsPerCircuit);
        string label = EdgeBatchLabel(descriptor);

        await foreach (var batch in IngestComposePipeline.RunAsync(
                           EnumerateBilinearEdgeRecords(left, right, n, r, typeId, weight, rowEnts, colEnts,
                               canonicalize, topK, collector, ct),
                           (rec, b) => StageEdgeAttestation(b, rec, _source),
                           _source, label, AttsPerChange, reader, options, ct, commitEpoch, AttsPerChange))
            yield return batch;

        await foreach (var change in EmitClassifyChange(descriptor, collector, commitEpoch, reader, options, ct))
            yield return change;
    }

    private static string EdgeBatchLabel(CircuitDescriptor descriptor)
    {
        var label = $"model/edges/{descriptor.Plane}";
        if (descriptor.Layer >= 0) label += $"/L{descriptor.Layer}";
        if (descriptor.Head >= 0) label += $".H{descriptor.Head}";
        return label;
    }

    private static async IAsyncEnumerable<EdgeAttestationRecord> EnumerateBilinearEdgeRecords(
        double[] left, double[] right, int n, int r,
        Hash128 typeId, double weight,
        List<Hash128> rowEnts, List<Hash128> colEnts,
        bool canonicalize, int topK,
        TopPairCollector? collector,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long cap = (long)RowTile * n;
        var oR = new int[cap]; var oC = new int[cap]; var oV = new double[cap]; var oS = new long[cap];
        for (int rb = 0; rb < n; rb += RowTile)
        {
            ct.ThrowIfCancellationRequested();
            int re = Math.Min(rb + RowTile, n);
            int cnt = RunBilinearTile(left, rb, re, right, n, r, oR, oC, oV, oS, cap);
            if (cnt < 0) yield break;
            cnt = SelectTopKPerRow(cnt, rb, re, oR, oC, oV, oS, topK);
            for (int e = 0; e < cnt; e++)
            {
                if (oC[e] == oR[e]) continue;
                if (canonicalize && oC[e] < oR[e]) continue;
                var subject = rowEnts[oR[e]];
                var obj = colEnts[oC[e]];
                collector?.Offer(subject, obj, oS[e]);
                yield return new EdgeAttestationRecord(subject, obj, typeId, oS[e], weight);
            }
        }
    }

    private static async IAsyncEnumerable<EdgeAttestationRecord> EnumerateFfnEdgeRecords(
        double[] Ad, int n, int d, double[]? gate, double[] up, double[] down, int I,
        List<Hash128> ents, Hash128 typeId, double weight,
        int[] oR, int[] oC, double[] oV, long[] oS, long cap,
        TopPairCollector? collector,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (int rb = 0; rb < n; rb += RowTile)
        {
            ct.ThrowIfCancellationRequested();
            int re = Math.Min(rb + RowTile, n);
            int cnt = RunFfnTile(Ad, n, d, gate, up, down, I, rb, re, oR, oC, oV, oS, cap);
            if (cnt < 0) yield break;
            cnt = SelectTopKPerRow(cnt, rb, re, oR, oC, oV, oS, EdgeTopK);
            for (int e = 0; e < cnt; e++)
            {
                if (oC[e] == oR[e]) continue;
                var subject = ents[oR[e]];
                var obj = ents[oC[e]];
                collector?.Offer(subject, obj, oS[e]);
                yield return new EdgeAttestationRecord(subject, obj, typeId, oS[e], weight);
            }
        }
    }

    private async IAsyncEnumerable<SubstrateChange> EmitClassifyChange(
        CircuitDescriptor descriptor, TopPairCollector? collector, int commitEpoch,
        ISubstrateReader? reader, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_classifier is null || collector is null) yield break;
        var pairs = collector.Drain();
        if (pairs.Count == 0) yield break;
        var record = await _classifier.TryClassifyRecordAsync(descriptor, pairs, ct);
        if (record is null) yield break;
        await foreach (var batch in IngestComposePipeline.RunAsync(
                           SingleClassifyRecordAsync(record.Value, ct),
                           (rec, b) => HeadClassifier.StageClassifyRecord(b, rec, _source),
                           _source, "model/decoder-ring", 1, reader, options, ct, commitEpoch))
            yield return batch;
    }

    private static async IAsyncEnumerable<HeadClassifier.CircuitClassifyRecord> SingleClassifyRecordAsync(
        HeadClassifier.CircuitClassifyRecord record, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield return record;
        await Task.CompletedTask;
    }

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


    private static int RunProject(float[] pts, int n, int d, float[] w, int r, double[] outp)
    {
        unsafe
        {
            fixed (float* pp = pts) fixed (float* pw = w) fixed (double* po = outp)
                return DynInterop.ProjectEmbedding(pp, (nuint)n, (nuint)d, pw, (nuint)r, po);
        }
    }




    private static void CenterColumns(double[] m, long n, int d)
    {
        var mean = new double[d];
        for (long i = 0; i < n; i++) for (int j = 0; j < d; j++) mean[j] += m[i * d + j];
        for (int j = 0; j < d; j++) mean[j] /= n;
        for (long i = 0; i < n; i++) for (int j = 0; j < d; j++) m[i * d + j] -= mean[j];
    }
    private static float[] CenteredCopyF(float[] a, long n, int d)
    {
        var mean = new double[d];
        for (long i = 0; i < n; i++) for (int j = 0; j < d; j++) mean[j] += a[i * d + j];
        for (int j = 0; j < d; j++) mean[j] /= n;
        var c = new float[n * d];
        for (long i = 0; i < n; i++) for (int j = 0; j < d; j++) c[i * d + j] = (float)(a[i * d + j] - mean[j]);
        return c;
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
                if (rc == 0 && overflow != 0)
                    throw new InvalidOperationException(
                        $"ffn_token_pairs_tile overflow at cap {cap}: the native tile drops edges in scan "
                        + "order (not by score), so results would be silently biased -- raise "
                        + "LAPLACE_MODEL_EDGE_FLOOR to prune before the cap.");
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
                if (rc == 0 && overflow != 0)
                    throw new InvalidOperationException(
                        $"bilinear_edges_tile overflow at cap {cap}: the native tile drops edges in scan "
                        + "order (not by score), so results would be silently biased -- raise "
                        + "LAPLACE_MODEL_EDGE_FLOOR to prune before the cap.");
                return rc != 0 ? -1 : (int)count;
            }
        }
    }

    // Keep only the top-k |value| edges per subject row within one tile's results,
    // compacting the parallel arrays in place. Tiles partition rows, so per-tile
    // selection is exact per-row selection. Returns the new count.
    private static int SelectTopKPerRow(int cnt, int rb, int re, int[] oR, int[] oC, double[] oV, long[] oS, int k)
    {
        if (cnt <= 0 || k <= 0) return Math.Max(cnt, 0);
        int rows = re - rb;
        var perRow = new int[rows];
        for (int e = 0; e < cnt; e++) perRow[oR[e] - rb]++;
        bool over = false;
        for (int i = 0; i < rows; i++) if (perRow[i] > k) { over = true; break; }
        if (!over) return cnt;

        var offsets = new int[rows + 1];
        for (int i = 0; i < rows; i++) offsets[i + 1] = offsets[i] + perRow[i];
        var idx = new int[cnt];
        var cursor = (int[])offsets.Clone();
        for (int e = 0; e < cnt; e++) idx[cursor[oR[e] - rb]++] = e;

        var keep = new List<int>(Math.Min(cnt, rows * k));
        for (int i = 0; i < rows; i++)
        {
            int start = offsets[i], len = perRow[i];
            if (len <= k)
            {
                for (int j = 0; j < len; j++) keep.Add(idx[start + j]);
                continue;
            }
            var rowIdx = new int[len];
            Array.Copy(idx, start, rowIdx, 0, len);
            Array.Sort(rowIdx, (a, b) => Math.Abs(oV[b]).CompareTo(Math.Abs(oV[a])));
            for (int j = 0; j < k; j++) keep.Add(rowIdx[j]);
        }
        keep.Sort();
        int w = 0;
        foreach (int e in keep)
        {
            oR[w] = oR[e]; oC[w] = oC[e]; oV[w] = oV[e]; oS[w] = oS[e]; w++;
        }
        return w;
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








    private static float[]? LoadGain(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, TensorRole? role, int dim)
    {
        if (role is null) return null;
        try { return WeightTensorETL.LoadTensorF32(refMap, role.Name, dim); }
        catch (Exception) { return null; }
    }


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


    private sealed class TopPairCollector
    {
        private readonly int _cap;
        private readonly List<CircuitPair> _heap;
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
