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
    // A pipeline record is a CHUNK of pre-staged rows, not one pair: the whole
    // chunk crosses the managed→native boundary in ONE AggregatedBatch call
    // (attestation ids, orientation, outcome — all native, batch-grain). The
    // per-pair P/Invoke was the emission path's serial wall. Chunk size is the
    // machine's own per-commit row budget (IngestSizing, Rule #12) — no
    // constant, nothing dropped; chunks stream until the plane is exhausted.
    public readonly record struct EdgeRowChunk(AttestationRow[] Rows, int Count);

    private static EdgeRowChunk BuildChunk(
        AttestationAggregatedCellNative[] cells, int count,
        Hash128 typeId, Hash128 source, double weight, AttestationStagedNative[] staged)
    {
        NativeAttestation.AggregatedBatch(cells, count, typeId, source, null, weight, staged);
        var rows = new AttestationRow[count];
        for (int i = 0; i < count; i++) rows[i] = NativeAttestation.Row(in staged[i]);
        return new EdgeRowChunk(rows, count);
    }

    private static void StageEdgeChunk(SubstrateChangeBuilder b, EdgeRowChunk chunk)
    {
        for (int i = 0; i < chunk.Count; i++) b.AddAttestation(chunk.Rows[i]);
    }

    public readonly record struct OccurrenceRecord(Hash128 Token, Hash128 Coordinate, long ScoreFp);

    // Witnessed occurrence: this token APPEARS_IN this circuit coordinate, at the
    // salience the checkpoint itself assigns, scored through the native score law.
    // Every token the circuit touches is recorded — the source's assertion is never
    // truncated; the fold and read-side RD/eff_mu are the noise model. The
    // coordinate is shared content across models; the model is only the source, so
    // consensus (token, APPEARS_IN, coord) rates cross-model convergence.

    public static string ResolvePlanesMode()
    {
        var m = Environment.GetEnvironmentVariable("LAPLACE_MODEL_PLANES");
        return string.IsNullOrWhiteSpace(m) ? "structure" : m.Trim().ToLowerInvariant();
    }

    private const int RowTile = 256;
    private const int EigTargetDim = 64;
    private const int TopPairsPerCircuit = 64;

    // Rows per change/chunk: the machine's commit-row budget from the one sizing
    // authority (IngestSizing.ResolveForSource — RAM + topology derived), not a
    // hand-set constant (Rule #12).
    private readonly int _rowsPerChange = IngestSizing.ResolveForSource(IngestSourceProfile.Default).CommitRows;

    // The native tiles' emission contract is strictly |score| > Theta; zero keeps
    // every pair the checkpoint asserts. No per-row budgets, no floors: identical
    // (subject, type, object, source) ids merge across circuits in the working set
    // (observation_count/sum_score), so plane volume aggregates instead of
    // ballooning, and weak couplings are draws the fold rates.
    private const double Theta = 0.0;






    // "structure" (default) deposits bounded APPEARS_IN occurrences aggregated from
    // the native pair tiles; the token-pair planes themselves are the versioned
    // analyzer's business ("all"/"similarity"/"continues"/"shallow").
    private readonly string _mode = ResolvePlanesMode();
    private bool StructureMode => _mode == "structure";
    private bool RunSimilarity => _mode is "all" or "similarity" or "shallow";
    private bool RunContinues => _mode is "all" or "continues" or "shallow";
    private bool RunLayers => _mode is "all" or "structure";
    // "factors" (campaign doc 26 item A): deposit the WITNESS — per-head factor
    // trajectories (entity -> firefly), native-dim, FACTOR vertices. No pair
    // tiles, no pair attestations: pair evidence is virtual (derivable from the
    // trajectories under the versioned derivation law; receipts at walk grain).
    private bool RunFactors => _mode == "factors";



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



        if (RunFactors)
        {
            await foreach (var change in EmitFactorTrajectories(Af, n, d, commitEpoch, refMap, reader, options, ct))
                yield return change;
            yield break;
        }

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
        unsafe { fixed (float* pf = Af) fixed (double* pd = Ad)
            DynInterop.F32ToF64(pf, (nuint)((long)n * d), pd); }

        // Layers are independent: produce them with bounded fan-out (memory is
        // the bound — each in-flight layer holds GB-scale projection buffers)
        // into one bounded channel; the single consumer preserves the async-
        // enumerator contract for the runner. Machine-derived width, no
        // constant: the working-set budget divided by one layer's buffer
        // footprint (n×attn doubles ≈ the dominant allocation), clamped to
        // the compose-worker count.
        int layers = _manifest.LayerCount;
        long perLayerBytes = 3L * n * d * sizeof(double);
        int fanOut = (int)Math.Clamp(
            MemoryTopology.WorkingSetBudgetBytes / Math.Max(1, perLayerBytes),
            1, IngestTopology.Current.ComposeWorkers);
        var chan = System.Threading.Channels.Channel.CreateBounded<SubstrateChange>(
            new System.Threading.Channels.BoundedChannelOptions(fanOut * 2)
            { SingleReader = true, SingleWriter = false });
        var gate = new SemaphoreSlim(fanOut, fanOut);
        var producers = new List<Task>(layers);
        for (int i = 0; i < layers; i++)
        {
            int L = i;
            producers.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    await foreach (var change in EmitAttentionLayer(L, Af, Ad, ents, n, d, commitEpoch, refMap, reader, options, ct))
                        await chan.Writer.WriteAsync(change, ct);
                    await foreach (var change in EmitOvLayer(L, Af, Ad, ents, n, d, commitEpoch, refMap, reader, options, ct))
                        await chan.Writer.WriteAsync(change, ct);
                    await foreach (var change in EmitMlpLayer(L, Ad, ents, n, d, commitEpoch, refMap, reader, options, ct))
                        await chan.Writer.WriteAsync(change, ct);
                }
                finally { gate.Release(); }
            }, ct));
        }
        var completion = Task.WhenAll(producers).ContinueWith(t =>
        {
            chan.Writer.Complete(t.Exception?.GetBaseException());
            return t;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();

        await foreach (var change in chan.Reader.ReadAllAsync(ct))
            yield return change;
        await completion;
    }

    private readonly record struct FactorDeposit(Hash128 Slice, double[] Xyzm, int Tokens, int Hd);

    // Item A deposit: per (projection tensor, head) — one Projection physicality
    // on the head's byte-range SLICE entity (same content law as tensors; slices
    // ARE tensors), trajectory = token-ordered FACTOR vertices, fixed runs of
    // ceil(hd/6) vertices per token so addressing is pure arithmetic
    // (vertex = tokenOrdinal * ceil(hd/6)). Probe input fixes BERT defects a-c:
    // x_t = LayerNorm(E[t] + P[0] + S[0]; gamma, beta), projection biases applied.
    private async IAsyncEnumerable<SubstrateChange> EmitFactorTrajectories(
        float[] Af, int n, int d, int commitEpoch,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        ISubstrateReader? reader, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _manifest.Config;
        var profile = ArchitectureProfile.For(cfg.ModelType);
        int H = cfg.NumHeads, Hkv = cfg.NumKvHeads, hd = cfg.HeadDim;
        if (H <= 0 || hd <= 0)
        { _log.LogWarning("phase=factors: no head geometry (H={H}, hd={Hd}); skipping", H, hd); yield break; }
        int attn = H * hd, kvDim = Hkv * hd;

        var X = new double[(long)n * d];
        unsafe
        {
            fixed (float* pa = Af) fixed (double* px = X)
                DynInterop.F32ToF64(pa, (nuint)((long)n * d), px);
        }
        AddRowZero(X, n, d, refMap, profile.PositionEmbeddings);
        AddRowZero(X, n, d, refMap, profile.TokenTypeEmbeddings);
        if (profile.EmbeddingNormWeight is not null)
        {
            var gamma = WeightTensorETL.LoadTensorF32(refMap, profile.EmbeddingNormWeight, d);
            var beta = TryLoadVector(refMap, profile.EmbeddingNormBias, d);
            int rc;
            unsafe
            {
                fixed (double* px = X) fixed (float* pg = gamma) fixed (float* pb = beta)
                    rc = DynInterop.LayerNormRowsD(px, (nuint)n, (nuint)d, pg,
                        beta is null ? null : pb, profile.NormEps);
            }
            if (rc != 0)
            { _log.LogWarning("phase=factors: layer_norm_rows_d rc={Rc}; aborting", rc); yield break; }
        }

        int vPerTok = (hd + (int)FactorWalk.ValuesPerVertex - 1) / FactorWalk.ValuesPerVertex;
        long depositsTotal = 0;
        for (int L = 0; L < _manifest.LayerCount; L++)
        {
            ct.ThrowIfCancellationRequested();
            var qRole = _manifest.Single(L, TensorRoleKind.AttnQ);
            var kRole = _manifest.Single(L, TensorRoleKind.AttnK);
            if (qRole is null || kRole is null) continue;

            var deposits = new List<FactorDeposit>(H + Hkv);
            BuildHeadDeposits(deposits, X, n, d, refMap, qRole.Name, attn, H, hd, profile, vPerTok, L, "q");
            BuildHeadDeposits(deposits, X, n, d, refMap, kRole.Name, kvDim, Hkv, hd, profile, vPerTok, L, "k");
            if (deposits.Count == 0) continue;
            depositsTotal += deposits.Count;

            await foreach (var batch in IngestComposePipeline.RunAsync(
                               EnumerateDepositsAsync(deposits, ct),
                               (dep, b) =>
                               {
                                   b.AddEntity(dep.Slice, EntityTier.Word,
                                       ModelCheckpoint.TensorTypeId, firstObservedBy: _source);
                                   b.AddPhysicality(new PhysicalityRow(
                                       Id: PhysicalityId.Compute(dep.Slice, PhysicalityType.Projection),
                                       EntityId: dep.Slice, SourceId: _source,
                                       Type: PhysicalityType.Projection,
                                       CoordX: dep.Xyzm[0], CoordY: dep.Xyzm[1],
                                       CoordZ: dep.Xyzm[2], CoordM: dep.Xyzm[3],
                                       HilbertIndex: default,
                                       TrajectoryXyzm: dep.Xyzm, NConstituents: dep.Tokens,
                                       AlignmentResidual: null, SourceDim: dep.Hd,
                                       ObservedAtUnixUs: IngestClock.NowUnixUs()));
                               },
                               _source, $"model/factors/L{L}", 1,
                               reader, options, ct, commitEpoch, _rowsPerChange))
                yield return batch;
        }
        _log.LogInformation("phase=factors: {Dep:N0} head-slice factor trajectories deposited "
            + "({Tok:N0} tokens x {V} vertices/token)", depositsTotal, n, vPerTok);
    }

    private void BuildHeadDeposits(
        List<FactorDeposit> deposits, double[] X, int n, int d,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string weightName, int outDim, int heads, int hd,
        ArchitectureProfile profile, int vPerTok, int L, string tag)
    {
        float[] W;
        try { W = WeightTensorETL.LoadTensorF32(refMap, weightName, (long)outDim * d); }
        catch (Exception ex)
        { _log.LogWarning("phase=factors L{L}.{Tag}: load failed: {Msg}", L, tag, ex.Message); return; }

        var P = new double[(long)n * outDim];
        int rc;
        unsafe
        {
            fixed (double* px = X) fixed (float* pw = W) fixed (double* pp = P)
                rc = DynInterop.ProjectEmbeddingD(px, (nuint)n, (nuint)d, pw, (nuint)outDim, pp);
        }
        if (rc != 0)
        { _log.LogWarning("phase=factors L{L}.{Tag}: projection rc={Rc}; skipping", L, tag, rc); return; }

        if (profile.HasBiases)
        {
            var bias = TryLoadVector(refMap, ArchitectureProfile.BiasOf(weightName), outDim);
            if (bias is not null)
                unsafe
                {
                    fixed (double* pp = P) fixed (float* pb = bias)
                        DynInterop.AddRowVectorD(pp, (nuint)n, (nuint)outDim, pb);
                }
        }

        Hash128[] slices;
        try { slices = ModelCheckpoint.HeadSliceIds(refMap[weightName], heads); }
        catch (Exception ex)
        { _log.LogWarning("phase=factors L{L}.{Tag}: slice ids failed: {Msg}", L, tag, ex.Message); return; }

        var head = new double[(long)n * hd];
        for (int h = 0; h < heads; h++)
        {
            SliceHead(P, head, n, outDim, h, hd);
            deposits.Add(new FactorDeposit(slices[h], PackFactorTrajectory(head, n, hd, vPerTok), n, hd));
        }
    }

    private static double[] PackFactorTrajectory(double[] M, int n, int hd, int vPerTok)
    {
        var xyzm = new double[(long)n * vPerTok * 4];
        var vals = new float[hd];
        for (int t = 0; t < n; t++)
        {
            for (int j = 0; j < hd; j++) vals[j] = (float)M[(long)t * hd + j];
            byte[] packed = FactorWalk.Pack(vals);
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(packed)
                .CopyTo(xyzm.AsSpan(t * vPerTok * 4, vPerTok * 4));
        }
        return xyzm;
    }

    private void AddRowZero(double[] X, int n, int d,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string? name)
    {
        if (name is null) return;
        if (!refMap.TryGetValue(name, out var t)) return;
        long rows = (t.AbsoluteDataEnd - t.AbsoluteDataStart) / 4 / d;
        var full = WeightTensorETL.LoadTensorF32(refMap, name, rows * d);
        var row0 = new float[d];
        Array.Copy(full, 0, row0, 0, d);
        unsafe
        {
            fixed (double* px = X) fixed (float* pv = row0)
                DynInterop.AddRowVectorD(px, (nuint)n, (nuint)d, pv);
        }
    }

    private static float[]? TryLoadVector(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string? name, int dim)
    {
        if (name is null || !refMap.ContainsKey(name)) return null;
        try { return WeightTensorETL.LoadTensorF32(refMap, name, dim); }
        catch (Exception) { return null; }
    }

    private static async IAsyncEnumerable<FactorDeposit> EnumerateDepositsAsync(
        List<FactorDeposit> deposits, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var dep in deposits)
        {
            ct.ThrowIfCancellationRequested();
            yield return dep;
        }
        await Task.CompletedTask;
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

        var Yf = new float[(long)n * rank];
        for (int i = 0; i < n; i++)
            Array.Copy(U, (long)i * kmax, Yf, (long)i * rank, rank);
        var Y = new double[(long)n * rank];
        unsafe
        {
            fixed (float* pf = Yf) fixed (double* pd = Y)
                DynInterop.F32ToF64(pf, (nuint)((long)n * rank), pd);
        }
        ScaleColsD(Y, n, rank, S);
        NormRows(Y, n, rank);
        _log.LogInformation("phase=edges: SVD reduced {N:N0} tokens d={D}->rank {R} (tol 1%), {Sec:F1}s; "
            + "folding SIMILAR_TO (every pair, no floor)", n, d, rank, sw.Elapsed.TotalSeconds);

        var typeId = RelationTypeRegistry.Resolve("SIMILAR_TO").Id;
        double weight = WeightFor("SIMILAR_TO");
        await foreach (var change in EmitBilinearPairs(
            Y, Y, n, rank, typeId, weight, ents, ents, commitEpoch,
            new CircuitDescriptor(Layer: -1, Head: -1, Plane: "similarity", RelationName: "SIMILAR_TO"),
            reader, options, ct))
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




        var Uf = new float[(long)n * d];
        for (int i = 0; i < n; i++)
            Array.Copy(Ufull, (long)rowOfToken[i] * d, Uf, (long)i * d, d);
        var Ed = new double[(long)n * d];
        var Ud = new double[(long)n * d];
        unsafe
        {
            fixed (float* pa = Af) fixed (double* pe = Ed)
                DynInterop.F32ToF64(pa, (nuint)((long)n * d), pe);
            fixed (float* pu = Uf) fixed (double* po = Ud)
                DynInterop.F32ToF64(pu, (nuint)((long)n * d), po);
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
            reader, options, ct))
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
        var salQ = StructureMode ? new double[n] : null;
        var salK = StructureMode ? new double[n] : null;
        for (int h = 0; h < H; h++)
        {
            ct.ThrowIfCancellationRequested();
            SliceHead(Q, Qh, n, attn, h, hd);
            SliceHead(Kexp, Kh, n, attn, h, hd);
            if (gQ is not null) ScaleColsD(Qh, n, hd, gQ);
            if (gK is not null) ScaleColsD(Kh, n, hd, gK);
            if (StructureMode)
            {
                // Salience = the token's magnitude in the head's QK subspace:
                // sqrt(||q_h(t)||² + ||k_h(t)||²) — same law as the OV/MLP planes,
                // read straight off the native projections. The n×n pair tile is
                // the analyzer's business, not the recorder's (VTune: the per-row
                // managed sort it forced was 83% of the ingest's CPU).
                RowNorms(Qh, n, hd, salQ!);
                RowNorms(Kh, n, hd, salK!);
                unsafe { fixed (double* pa = salQ) fixed (double* pb = salK)
                    DynInterop.HypotRowsD(pa, pb, (nuint)n, pa); }
                await foreach (var change in EmitNormOccurrences(
                    new CircuitDescriptor(L, h, "attention", "ATTENDS"), salQ!, ents, n,
                    commitEpoch, reader, options, ct))
                    yield return change;
                continue;
            }
            await foreach (var change in EmitBilinearPairs(
                Qh, Kh, n, hd, typeId, weight, ents, ents, commitEpoch,
                new CircuitDescriptor(L, h, "attention", "ATTENDS"),
                reader, options, ct))
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
        int rcv, rce, rco;
        unsafe
        {
            fixed (float* pa = Af) fixed (float* pw = Wv) fixed (double* pv = Vraw)
                rcv = DynInterop.ProjectEmbedding(pa, (nuint)n, (nuint)d, pw, (nuint)kvDim, pv);
            fixed (double* pv = Vraw) fixed (double* pe = Vexp)
                rce = DynInterop.ExpandKvHeadsD(pv, (nuint)n, (nuint)H, (nuint)Hkv, (nuint)hd, pe);
        }
        if (rcv != 0 || rce != 0)
        { _log.LogWarning("phase=edges L{L}: OV projection rc=({A},{B}); skipping", L, rcv, rce); yield break; }

        if (StructureMode)
        {
            // Per-head OV occurrences: each head is its own circuit coordinate, and
            // its salience for token t is the write magnitude ||Wo_h·v_h(t)|| — a
            // direct read of what the head moves, from the native GEMM output. No
            // n×n pair tile at rank d (that is the analyzer's petaflop, not the
            // recorder's). Scores go through the native score law (arena = RMS).
            var Vh = new double[(long)n * hd];
            var WoH = new float[(long)d * hd];
            var OVh = new double[(long)n * d];
            var sal = new double[n];
            for (int h = 0; h < H; h++)
            {
                ct.ThrowIfCancellationRequested();
                SliceHead(Vexp, Vh, n, attn, h, hd);
                for (int row = 0; row < d; row++)
                    Array.Copy(Wo, (long)row * attn + (long)h * hd, WoH, (long)row * hd, hd);
                int rcoH;
                unsafe
                {
                    fixed (double* pv = Vh) fixed (float* pw = WoH) fixed (double* po = OVh)
                        rcoH = DynInterop.ProjectEmbeddingD(pv, (nuint)n, (nuint)hd, pw, (nuint)d, po);
                }
                if (rcoH != 0)
                { _log.LogWarning("phase=edges L{L}.H{H2}: OV head projection rc={Rc}; skipping", L, h, rcoH); continue; }
                RowNorms(OVh, n, d, sal);
                await foreach (var change in EmitNormOccurrences(
                    new CircuitDescriptor(L, h, "ov", "OV_RELATES"), sal, ents, n,
                    commitEpoch, reader, options, ct))
                    yield return change;
            }
            yield break;
        }

        var En = (double[])Ad.Clone();
        NormRows(En, n, d);
        var typeId = RelationTypeRegistry.Resolve("OV_RELATES").Id;
        double weight = WeightFor("OV_RELATES");

        var OVfull = new double[(long)n * d];
        unsafe
        {
            fixed (double* pv = Vexp) fixed (float* pw = Wo) fixed (double* po = OVfull)
                rco = DynInterop.ProjectEmbeddingD(pv, (nuint)n, (nuint)attn, pw, (nuint)d, po);
        }
        if (rco != 0)
        { _log.LogWarning("phase=edges L{L}: OV projection rc={Rc}; skipping", L, rco); yield break; }

        NormRows(OVfull, n, d);
        await foreach (var change in EmitBilinearPairs(
            OVfull, En, n, d, typeId, weight, ents, ents, commitEpoch,
            new CircuitDescriptor(L, -1, "ov", "OV_RELATES"),
            reader, options, ct))
            yield return change;
    }


    private async IAsyncEnumerable<SubstrateChange> EmitMlpLayer(
        int L, double[] Ad, List<Hash128> ents, int n, int d, int commitEpoch,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        ISubstrateReader? reader, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _manifest.Config;

        // MoE layer: the router tensor IS the model's literal token→expert routing
        // map. One GEMM per layer; each expert is its own coordinate
        // [expert-plane, layer, expert] taking its top tokens by routing logit.
        // Expert FFN internals are the analyzer's business.
        if (StructureMode && cfg.NumExperts > 0)
        {
            var routerRole = _manifest.Single(L, TensorRoleKind.MoeRouter);
            if (routerRole is null)
            { _log.LogWarning("phase=edges L{L}: MoE layer without router tensor; skipping", L); yield break; }
            int E = cfg.NumExperts;
            float[] router;
            try { router = WeightTensorETL.LoadTensorF32(refMap, routerRole.Name, (long)E * d); }
            catch (Exception ex)
            { _log.LogWarning("phase=edges L{L}: router load failed: {Msg}", L, ex.Message); yield break; }

            if (NormFold)
            {
                var gPostR = LoadGain(refMap, _manifest.PostAttnNorm(L), d);
                if (gPostR is not null) ScaleCols(router, E, d, gPostR);
            }

            var proj = new double[(long)n * E];
            int rcR;
            unsafe
            {
                fixed (double* pa = Ad) fixed (float* pw = router) fixed (double* po = proj)
                    rcR = DynInterop.ProjectEmbeddingD(pa, (nuint)n, (nuint)d, pw, (nuint)E, po);
            }
            if (rcR != 0)
            { _log.LogWarning("phase=edges L{L}: router projection rc={Rc}; skipping", L, rcR); yield break; }

            var salE = new double[n];
            for (int e = 0; e < E; e++)
            {
                ct.ThrowIfCancellationRequested();
                for (int i = 0; i < n; i++) salE[i] = proj[(long)i * E + e];
                await foreach (var change in EmitNormOccurrences(
                    new CircuitDescriptor(L, e, "expert", "APPEARS_IN"), salE, ents, n,
                    commitEpoch, reader, options, ct))
                    yield return change;
            }
            yield break;
        }

        var upRole = _manifest.Single(L, TensorRoleKind.MlpUp);
        var downRole = _manifest.Single(L, TensorRoleKind.MlpDown);
        var gateRole = _manifest.Single(L, TensorRoleKind.MlpGate);
        if (upRole is null || downRole is null) yield break;
        int I = cfg.IntermediateSize;
        if (I <= 0) yield break;

        var descriptor = new CircuitDescriptor(L, -1, "mlp", "COMPLETES_TO");

        if (StructureMode)
        {
            // MLP occurrences from the token's gated-activation magnitude
            // ||silu(gate·x)⊙(up·x)|| — native GEMM tiles + the native score law.
            // The n×n COMPLETES_TO pair tile is the analyzer's business.
            float[] upF; float[]? gateF = null;
            try
            {
                upF = WeightTensorETL.LoadTensorF32(refMap, upRole.Name, (long)I * d);
                if (gateRole is not null)
                    gateF = WeightTensorETL.LoadTensorF32(refMap, gateRole.Name, (long)I * d);
            }
            catch (Exception ex)
            { _log.LogWarning("phase=edges L{L}: MLP load failed: {Msg}", L, ex.Message); yield break; }

            if (NormFold)
            {
                var gPostF = LoadGain(refMap, _manifest.PostAttnNorm(L), d);
                if (gPostF is not null)
                {
                    ScaleCols(upF, I, d, gPostF);
                    if (gateF is not null) ScaleCols(gateF, I, d, gPostF);
                }
            }

            var sal = ComputeMlpActivationNorms(Ad, n, d, upF, gateF, I, L);
            if (sal is null) yield break;
            await foreach (var change in EmitNormOccurrences(descriptor, sal, ents, n, commitEpoch, reader, options, ct))
                yield return change;
            yield break;
        }

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

        var collector = _classifier is null ? null : new TopPairCollector(TopPairsPerCircuit);

        long edges = 0;
        await foreach (var batch in IngestComposePipeline.RunAsync(
                           EnumerateFfnEdgeChunks(Ad, n, d, gate, up, down, I, _rowsPerChange, ents, typeId, _source,
                               weight, collector, ct),
                           (chunk, b) => StageEdgeChunk(b, chunk),
                           _source, $"model/edges/mlp/L{L}", 1,
                           reader, options, ct, commitEpoch, _rowsPerChange))
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
        CircuitDescriptor descriptor,
        ISubstrateReader? reader, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        bool canonicalize = RelationTypeRegistry.Resolve(descriptor.RelationName).Symmetry
                            == RelationTypeRegistry.Symmetry.Symmetric;
        var collector = _classifier is null ? null : new TopPairCollector(TopPairsPerCircuit);
        string label = EdgeBatchLabel(descriptor);

        await foreach (var batch in IngestComposePipeline.RunAsync(
                           EnumerateBilinearEdgeChunks(left, right, n, r, _rowsPerChange, typeId, _source, weight,
                               rowEnts, colEnts, canonicalize, Theta, collector, ct),
                           (chunk, b) => StageEdgeChunk(b, chunk),
                           _source, label, 1,
                           reader, options, ct, commitEpoch, _rowsPerChange))
            yield return batch;

        await foreach (var change in EmitClassifyChange(descriptor, collector, commitEpoch, reader, options, ct))
            yield return change;
    }

    private static double VectorNorm(double[] v, int n)
    {
        var nrm = new double[1];
        unsafe
        {
            fixed (double* ps = v) fixed (double* po = nrm)
                if (DynInterop.RowNormsOutD(ps, 1, (nuint)n, po) != 0)
                    throw new InvalidOperationException("row_norms_out_d failed");
        }
        return nrm[0];
    }

    private static void RowNorms(double[] m, int n, int dim, double[] outNorms)
    {
        unsafe
        {
            fixed (double* pm = m) fixed (double* po = outNorms)
                if (DynInterop.RowNormsOutD(pm, (nuint)n, (nuint)dim, po) != 0)
                    throw new InvalidOperationException("row_norms_out_d failed");
        }
    }

    // Salience norms → occurrence rows through the NATIVE score law
    // (NativeAttestation.ScoreFp → score.c); arena = the RMS of the circuit's
    // salience distribution — the MERGES_WITH arena convention. Every token is
    // recorded; the score carries the circuit's own weighting and the fold rates it.
    private async IAsyncEnumerable<SubstrateChange> EmitNormOccurrences(
        CircuitDescriptor descriptor, double[] sal, List<Hash128> ents, int n, int commitEpoch,
        ISubstrateReader? reader, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        double arena = VectorNorm(sal, n) / Math.Sqrt(Math.Max(1, n));
        if (arena <= 0) yield break;

        var scores = new Dictionary<Hash128, long>(n);
        for (int i = 0; i < n; i++)
            scores[ents[i]] = NativeAttestation.ScoreFp(sal[i], arena);

        await foreach (var change in EmitOccurrenceChange(descriptor, scores, commitEpoch, reader, options, ct))
            yield return change;
    }

    private double[]? ComputeMlpActivationNorms(
        double[] Ad, int n, int d, float[] upF, float[]? gateF, int I, int L)
    {
        var sal = new double[n];
        int rc;
        unsafe
        {
            fixed (double* px = Ad) fixed (float* pu = upF) fixed (float* pg = gateF)
            fixed (double* po = sal)
                rc = DynInterop.FfnActivationNorms(px, (nuint)n, (nuint)d, pu,
                    gateF is null ? null : pg, (nuint)I, po);
        }
        if (rc != 0)
        {
            _log.LogWarning("phase=edges L{L}: ffn_activation_norms rc={Rc}; skipping", L, rc);
            return null;
        }
        return sal;
    }


    private async IAsyncEnumerable<SubstrateChange> EmitOccurrenceChange(
        CircuitDescriptor descriptor, Dictionary<Hash128, long> salience, int commitEpoch,
        ISubstrateReader? reader, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (salience.Count == 0) yield break;
        var coord = ModelCoordinates.CoordinateId(descriptor);
        var top = salience.ToList();
        double weight = WeightFor("APPEARS_IN");
        bool metaStaged = false;

        // THE CIRCUIT IS THE GAME (chess trajectory parity): one testimony-packed
        // linestring on the coordinate entity carrying the circuit's ENTIRE
        // assertion — every token, its score — exactly as a game's trajectory
        // carries its whole move sequence. Calculated payload (Projection class,
        // doc 08: versioned, evictable); anatomy queries read 1 row per circuit.
        var trajTokens = new Hash128[top.Count];
        var trajScores = new long[top.Count];
        for (int i = 0; i < top.Count; i++)
        {
            trajTokens[i] = top[i].Key;
            trajScores[i] = top[i].Value;
        }
        byte[] packed = TestimonyWalk.Pack(trajTokens, trajScores);
        double[] trajXyzm = System.Runtime.InteropServices.MemoryMarshal
            .Cast<byte, double>(packed).ToArray();
        var circuitPhys = new PhysicalityRow(
            Id: PhysicalityId.Compute(coord, PhysicalityType.Projection),
            EntityId: coord, SourceId: _source,
            Type: PhysicalityType.Projection,
            CoordX: trajXyzm[0], CoordY: trajXyzm[1], CoordZ: trajXyzm[2], CoordM: trajXyzm[3],
            HilbertIndex: default,
            TrajectoryXyzm: trajXyzm, NConstituents: top.Count,
            AlignmentResidual: null, SourceDim: null,
            ObservedAtUnixUs: IngestClock.NowUnixUs());

        // Same batch door as the pair planes: fill machine-sized cell chunks,
        // ONE AggregatedBatch P/Invoke per chunk — never one per token.
        var cells = new AttestationAggregatedCellNative[Math.Min(_rowsPerChange, top.Count)];
        var staged = new AttestationStagedNative[cells.Length];
        var chunks = new List<EdgeRowChunk>((top.Count + cells.Length - 1) / cells.Length);
        int filled = 0;
        foreach (var kv in top)
        {
            cells[filled] = new AttestationAggregatedCellNative
            {
                Subject = kv.Key, Object = coord, ObjectIsNull = 0,
                Games = 1, SumScoreFp1e9 = kv.Value,
            };
            if (++filled == cells.Length)
            {
                chunks.Add(BuildChunk(cells, filled, ModelCoordinates.AppearsInTypeId, _source, weight, staged));
                filled = 0;
            }
        }
        if (filled > 0)
            chunks.Add(BuildChunk(cells, filled, ModelCoordinates.AppearsInTypeId, _source, weight, staged));

        await foreach (var batch in IngestComposePipeline.RunAsync(
                           EnumerateChunksAsync(chunks, ct),
                           (chunk, b) =>
                           {
                               if (!metaStaged)
                               {
                                   ModelCoordinates.StageCoordinate(b, descriptor, _source);
                                   b.AddPhysicality(circuitPhys);
                                   metaStaged = true;
                               }
                               StageEdgeChunk(b, chunk);
                           },
                           _source, EdgeBatchLabel(descriptor) + "/occ", 1,
                           reader, options, ct, commitEpoch, _rowsPerChange))
            yield return batch;
    }

    private static async IAsyncEnumerable<EdgeRowChunk> EnumerateChunksAsync(
        List<EdgeRowChunk> chunks, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var c in chunks)
        {
            ct.ThrowIfCancellationRequested();
            yield return c;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<OccurrenceRecord> EnumerateOccurrenceRecords(
        List<KeyValuePair<Hash128, long>> top, Hash128 coord,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var kv in top)
        {
            ct.ThrowIfCancellationRequested();
            yield return new OccurrenceRecord(kv.Key, coord, kv.Value);
        }
        await Task.CompletedTask;
    }

    private static string EdgeBatchLabel(CircuitDescriptor descriptor)
    {
        var label = $"model/edges/{descriptor.Plane}";
        if (descriptor.Layer >= 0) label += $"/L{descriptor.Layer}";
        if (descriptor.Head >= 0) label += $".H{descriptor.Head}";
        return label;
    }

    // Double-buffered: tile t+1's native GEMM/scan runs on a worker while tile
    // t's pairs are cell-filled and batch-staged — compute and staging overlap
    // instead of strictly alternating.
    private static async IAsyncEnumerable<EdgeRowChunk> EnumerateBilinearEdgeChunks(
        double[] left, double[] right, int n, int r, int rowsPerChange,
        Hash128 typeId, Hash128 source, double weight,
        List<Hash128> rowEnts, List<Hash128> colEnts,
        bool canonicalize, double theta,
        TopPairCollector? collector,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long cap = (long)RowTile * n;
        var bufA = (R: new int[cap], C: new int[cap], V: new double[cap], S: new long[cap]);
        var bufB = (R: new int[cap], C: new int[cap], V: new double[cap], S: new long[cap]);
        var cells = new AttestationAggregatedCellNative[rowsPerChange];
        var staged = new AttestationStagedNative[rowsPerChange];

        var cur = bufA;
        var spare = bufB;
        {
            var b0 = cur;
            int re0 = Math.Min(RowTile, n);
            var pending = Task.Run(() => RunBilinearTile(left, 0, re0, right, n, r,
                b0.R, b0.C, b0.V, b0.S, cap, theta), ct);

            for (int rb = 0; rb < n; rb += RowTile)
            {
                ct.ThrowIfCancellationRequested();
                int cnt = await pending.ConfigureAwait(false);
                if (cnt < 0) yield break;
                var tile = cur;

                int rbNext = rb + RowTile;
                if (rbNext < n)
                {
                    cur = spare;
                    spare = tile;
                    var bn = cur;
                    int reNext = Math.Min(rbNext + RowTile, n);
                    pending = Task.Run(() => RunBilinearTile(left, rbNext, reNext, right, n, r,
                        bn.R, bn.C, bn.V, bn.S, cap, theta), ct);
                }

                int filled = 0;
                for (int e = 0; e < cnt; e++)
                {
                    if (tile.C[e] == tile.R[e]) continue;
                    if (canonicalize && tile.C[e] < tile.R[e]) continue;
                    var subject = rowEnts[tile.R[e]];
                    var obj = colEnts[tile.C[e]];
                    collector?.Offer(subject, obj, tile.S[e]);
                    cells[filled] = new AttestationAggregatedCellNative
                    {
                        Subject = subject,
                        Object = obj,
                        ObjectIsNull = 0,
                        Games = 1,
                        SumScoreFp1e9 = tile.S[e],
                    };
                    if (++filled == rowsPerChange)
                    {
                        yield return BuildChunk(cells, filled, typeId, source, weight, staged);
                        filled = 0;
                    }
                }
                if (filled > 0)
                    yield return BuildChunk(cells, filled, typeId, source, weight, staged);
            }
        }
    }

    private static async IAsyncEnumerable<EdgeRowChunk> EnumerateFfnEdgeChunks(
        double[] Ad, int n, int d, double[]? gate, double[] up, double[] down, int I, int rowsPerChange,
        List<Hash128> ents, Hash128 typeId, Hash128 source, double weight,
        TopPairCollector? collector,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long cap = (long)RowTile * n;
        var bufA = (R: new int[cap], C: new int[cap], V: new double[cap], S: new long[cap]);
        var bufB = (R: new int[cap], C: new int[cap], V: new double[cap], S: new long[cap]);
        var cells = new AttestationAggregatedCellNative[rowsPerChange];
        var staged = new AttestationStagedNative[rowsPerChange];

        var cur = bufA;
        var spare = bufB;
        var b0 = cur;
        int re0 = Math.Min(RowTile, n);
        var pending = Task.Run(() => RunFfnTile(Ad, n, d, gate, up, down, I, 0, re0,
            b0.R, b0.C, b0.V, b0.S, cap), ct);

        for (int rb = 0; rb < n; rb += RowTile)
        {
            ct.ThrowIfCancellationRequested();
            int cnt = await pending.ConfigureAwait(false);
            if (cnt < 0) yield break;
            var tile = cur;

            int rbNext = rb + RowTile;
            if (rbNext < n)
            {
                cur = spare;
                spare = tile;
                var bn = cur;
                int reNext = Math.Min(rbNext + RowTile, n);
                pending = Task.Run(() => RunFfnTile(Ad, n, d, gate, up, down, I, rbNext, reNext,
                    bn.R, bn.C, bn.V, bn.S, cap), ct);
            }

            int filled = 0;
            for (int e = 0; e < cnt; e++)
            {
                if (tile.C[e] == tile.R[e]) continue;
                var subject = ents[tile.R[e]];
                var obj = ents[tile.C[e]];
                collector?.Offer(subject, obj, tile.S[e]);
                cells[filled] = new AttestationAggregatedCellNative
                {
                    Subject = subject,
                    Object = obj,
                    ObjectIsNull = 0,
                    Games = 1,
                    SumScoreFp1e9 = tile.S[e],
                };
                if (++filled == rowsPerChange)
                {
                    yield return BuildChunk(cells, filled, typeId, source, weight, staged);
                    filled = 0;
                }
            }
            if (filled > 0)
                yield return BuildChunk(cells, filled, typeId, source, weight, staged);
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
        unsafe { fixed (double* pm = m)
            if (DynInterop.CenterColumnsD(pm, (nuint)n, (nuint)d) != 0)
                throw new InvalidOperationException("center_columns_d failed"); }
    }
    private static float[] CenteredCopyF(float[] a, long n, int d)
    {
        var c = new float[n * d];
        Array.Copy(a, c, n * d);
        unsafe { fixed (float* pc = c)
            if (DynInterop.CenterColumnsF(pc, (nuint)n, (nuint)d) != 0)
                throw new InvalidOperationException("center_columns_f failed"); }
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
                        $"ffn_token_pairs_tile overflow at cap {cap}: cap is RowTile*n, the tile's "
                        + "maximum possible emission, so overflow means a caller-side sizing bug.");
                return rc != 0 ? -1 : (int)count;
            }
        }
    }

    private static int RunBilinearTile(double[] left, int rb, int re, double[] right, int n, int dim,
        int[] oR, int[] oC, double[] oV, long[] oS, long cap, double theta = Theta)
    {
        unsafe
        {
            nuint count = 0; int overflow = 0;
            fixed (double* pl = left) fixed (double* pr = right)
            fixed (int* pR = oR) fixed (int* pC = oC) fixed (double* pV = oV) fixed (long* pS = oS)
            {
                int rc = DynInterop.BilinearEdgesTile(pl, (nuint)rb, (nuint)re, pr, (nuint)n,
                    (nuint)dim, theta, pR, pC, pV, pS, (nuint)cap, &count, &overflow);
                if (rc == 0 && overflow != 0)
                    throw new InvalidOperationException(
                        $"bilinear_edges_tile overflow at cap {cap}: cap is RowTile*n, the tile's "
                        + "maximum possible emission, so overflow means a caller-side sizing bug.");
                return rc != 0 ? -1 : (int)count;
            }
        }
    }

    private static void SliceHead(double[] full, double[] head, int n, int fullDim, int h, int hd)
    {
        unsafe { fixed (double* pf = full) fixed (double* ph = head)
            if (DynInterop.SliceHeadD(pf, ph, (nuint)n, (nuint)fullDim, (nuint)h, (nuint)hd) != 0)
                throw new InvalidOperationException("slice_head_d failed"); }
    }

    private static double[] LoadDouble(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string name, long elems)
    {
        float[] f = WeightTensorETL.LoadTensorF32(refMap, name, elems);
        var dbl = new double[elems];
        unsafe { fixed (float* pf = f) fixed (double* pd = dbl)
            DynInterop.F32ToF64(pf, (nuint)elems, pd); }
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
        unsafe { fixed (float* pm = M) fixed (float* pg = g)
            if (DynInterop.ScaleColsF(pm, (nuint)rows, (nuint)dim, pg) != 0)
                throw new InvalidOperationException("scale_cols_f failed"); }
    }
    private static void ScaleColsD(double[] M, long rows, int dim, float[] g)
    {
        unsafe { fixed (double* pm = M) fixed (float* pg = g)
            if (DynInterop.ScaleColsD(pm, (nuint)rows, (nuint)dim, pg) != 0)
                throw new InvalidOperationException("scale_cols_d failed"); }
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
