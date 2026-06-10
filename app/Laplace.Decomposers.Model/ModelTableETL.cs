using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

// Streams token→token (or element→element) Glicko-2 matchups for every path the architecture
// defines. One matchup per (source, target) pair whose path score exceeds the noise floor.
// The intermediate spaces (neuron, attn_dim, kv_dim) are contracted away inside the native
// tile kernels — they never surface as entities. The architecture profile owns the path list;
// adding a new modality or architecture means adding new PathSpec entries, not touching this class.
public sealed class ModelTableETL
{
    private const int RowsPerChange = 500_000;
    private const int RowTile = 256;
    private static readonly double ModelWeight =
        RelationTypeRegistry.Resolve("EMBEDS").Rank * SourceTrust.AiModelProbe;
    private static readonly double[] _one = new double[1];

    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _source;
    private readonly ArchitectureProfile _profile;
    private readonly ILogger _log;
    private readonly Dictionary<string, SafetensorsContainerParser.TensorReference> _refMap;
    private long _strands;
    private int _commitEpoch;

    public ModelTableETL(string modelDir, LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens, Hash128 sourceId,
        ILogger? log = null)
    {
        _recipe  = recipe;
        _tokens  = tokens;
        _source  = sourceId;
        _profile = ArchitectureProfile.For(recipe.ModelType);
        _log     = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var refs = SafetensorsContainerParser.ParseModel(modelDir);
        _refMap  = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
                       refs.Count, StringComparer.Ordinal);
        foreach (var r in refs) _refMap[r.Name] = r;
    }

    public async IAsyncEnumerable<SubstrateChange> EmitAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int d = _recipe.HiddenSize, vocab = _recipe.VocabSize;
        int nHeads  = _recipe.NumHeads;
        int nKv     = Math.Max(1, _recipe.NumKvHeads);
        int headDim = d / Math.Max(1, nHeads);
        int attnDim = nHeads * headDim;
        int kvDim   = nKv   * headDim;
        int interm  = _recipe.IntermediateSize;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Index tokens that have entities
        var ents   = new List<Hash128>();
        var entIdx = new int[vocab]; Array.Fill(entIdx, -1);
        var tier   = new byte[vocab];
        foreach (var rec in _tokens)
        {
            if (rec.TokenId < 0 || rec.TokenId >= vocab || entIdx[rec.TokenId] >= 0) continue;
            entIdx[rec.TokenId] = ents.Count;
            ents.Add(rec.EntityId);
            tier[rec.TokenId] = rec.Tier;
        }
        int n = ents.Count;
        if (n == 0 || !_refMap.ContainsKey(_profile.EmbedTokens)) yield break;

        _commitEpoch = 0;

        // Phase 1: emit token entities (epoch 0 — must commit before matchup attestations).
        {
            var eb = NewChunk("token"); int ec = 0;
            for (int t = 0; t < vocab; t++)
            {
                if (entIdx[t] < 0) continue;
                eb.AddEntity(new EntityRow(ents[entIdx[t]], tier[t], TextEntityBuilder.WordTypeId, _source));
                if (++ec >= RowsPerChange)
                {
                    yield return eb.SetInputUnitsConsumed(ec).Build();
                    eb = NewChunk("token"); ec = 0;
                }
            }
            if (ec > 0) yield return eb.SetInputUnitsConsumed(ec).Build();
            _log.LogInformation("phase=etl tokens: {N:N0} corners", n);
        }

        // Raw embed [n×d] double — source for all projection and FFN inputs
        var embRawD = GatherF32ToD(_profile.EmbedTokens, vocab, d, entIdx, n);

        // L2-normalized un-embedding [n×d] double — the scoring target for all path outputs
        string lmHead = _profile.LmHead ?? _profile.EmbedTokens;
        double[] unembN;
        if (lmHead == _profile.EmbedTokens)
        {
            unembN = (double[])embRawD.Clone();
            NormD(unembN, n, d);
        }
        else
        {
            unembN = GatherF32ToD(lmHead, vocab, d, entIdx, n);
            NormD(unembN, n, d);
        }

        // Phase 2: stream path matchups — attestations only; references token entities from epoch 0.
        _commitEpoch = 1;
        foreach (var path in _profile.Paths)
        {
            ct.ThrowIfCancellationRequested();

            if (!path.PerLayer)
            {
                await foreach (var c in EmitGlobalPath(path, embRawD, unembN, n, d,
                                   attnDim, kvDim, nHeads, nKv, headDim, interm, ents, sw, ct))
                    yield return c;
                _log.LogInformation("phase=etl {R}: {S:N0} cum matchups", path.RelationName, _strands);
            }
            else
            {
                for (int l = 0; l < _recipe.NumLayers; l++)
                {
                    ct.ThrowIfCancellationRequested();
                    var ctx = Hash128.OfCanonical($"substrate/layer/{_source}/L{l}/v1");
                    await foreach (var c in EmitLayerPath(path, l, embRawD, unembN,
                                       n, d, attnDim, kvDim, nHeads, nKv, headDim, interm,
                                       ents, ctx, sw, ct))
                        yield return c;
                }
                _log.LogInformation("phase=etl {R} (all layers): {S:N0} cum matchups, {Sec:F0}s",
                    path.RelationName, _strands, sw.Elapsed.TotalSeconds);
            }
        }

        _log.LogInformation("phase=etl COMPLETE: {S:N0} matchups in {Sec:F0}s",
            _strands, sw.Elapsed.TotalSeconds);
    }

    // ── global path dispatch ──────────────────────────────────────────────────

    private async IAsyncEnumerable<SubstrateChange> EmitGlobalPath(
        PathSpec path, double[] embRawD, double[] unembN,
        int n, int d, int attnDim, int kvDim, int nHeads, int nKv, int headDim, int interm,
        List<Hash128> ents, System.Diagnostics.Stopwatch sw,
        [EnumeratorCancellation] CancellationToken ct)
    {
        switch (path)
        {
            case SelfSimilarityPath sim:
            {
                if (!_refMap.ContainsKey(sim.EmbedPattern)) yield break;
                // Non-token entity sets (patches, audio frames, etc.) require their own
                // entity index and embedding gather — extend ArchitectureProfile with an
                // entity-set descriptor when adding those modalities.
                if (sim.EmbedPattern != _profile.EmbedTokens)
                {
                    _log.LogWarning("phase=etl SelfSimilarityPath on non-token tensor '{T}' not yet supported",
                        sim.EmbedPattern);
                    yield break;
                }
                var src = (double[])embRawD.Clone(); NormD(src, n, d);
                await foreach (var c in EmitEdges(src, n, src, n, d,
                                   sim.RelationName, ents, ents, null, sw, ct))
                    yield return c;
                break;
            }
            default:
                _log.LogWarning("phase=etl unsupported global path type {T}", path.GetType().Name);
                break;
        }
    }

    // ── per-layer path dispatch ───────────────────────────────────────────────

    private async IAsyncEnumerable<SubstrateChange> EmitLayerPath(
        PathSpec path, int layer, double[] embRawD, double[] unembN,
        int n, int d, int attnDim, int kvDim, int nHeads, int nKv, int headDim, int interm,
        List<Hash128> ents, Hash128 ctx, System.Diagnostics.Stopwatch sw,
        [EnumeratorCancellation] CancellationToken ct)
    {
        switch (path)
        {
            case BilinearPath bil:
            {
                string ln = ArchitectureProfile.Layer(bil.LeftPattern,  layer);
                string rn = ArchitectureProfile.Layer(bil.RightPattern, layer);
                if (!_refMap.ContainsKey(ln) || !_refMap.ContainsKey(rn)) yield break;
                int rDim = bil.RightIsKv ? kvDim : attnDim;
                var Wl = LoadF32(ln, (long)attnDim * d);
                var Wr = LoadF32(rn, (long)rDim    * d);
                var qAll = NormD(ProjectD(Wl, attnDim, d, embRawD, n), n, attnDim);
                var kRaw = ProjectD(Wr, rDim, d, embRawD, n);
                var kAll = NormD(bil.RightIsKv
                    ? ExpandKvHeads(kRaw, n, nHeads, nKv, headDim)
                    : kRaw, n, attnDim);
                await foreach (var c in EmitEdges(qAll, n, kAll, n, attnDim,
                                   bil.RelationName, ents, ents, ctx, sw, ct))
                    yield return c;
                break;
            }
            case ProjectionPath proj:
            {
                string vn = ArchitectureProfile.Layer(proj.VPattern, layer);
                string on = ArchitectureProfile.Layer(proj.OPattern, layer);
                if (!_refMap.ContainsKey(vn) || !_refMap.ContainsKey(on)) yield break;
                var Wv = LoadF32(vn, (long)kvDim * d);
                var Wo = LoadF32(on, (long)d * attnDim);
                for (int rb = 0; rb < n; rb += RowTile)
                {
                    ct.ThrowIfCancellationRequested();
                    int re = Math.Min(rb + RowTile, n), t = re - rb;
                    var tileD = SliceRowsD(embRawD, rb, re, d);
                    var v  = ExpandKvHeads(ProjectD(Wv, kvDim, d, tileD, t), t, nHeads, nKv, headDim);
                    var ov = NormD(ProjectD(Wo, d, attnDim, v, t), t, d);
                    await foreach (var c in EmitEdges(ov, t, unembN, n, d,
                                       proj.RelationName, ents, ents, ctx, sw, ct, leftBase: rb))
                        yield return c;
                }
                break;
            }
            case ContractionPath ctr:
            {
                string? gn = ctr.GatePattern is null ? null
                             : ArchitectureProfile.Layer(ctr.GatePattern, layer);
                string un = ArchitectureProfile.Layer(ctr.UpPattern,   layer);
                string dn = ArchitectureProfile.Layer(ctr.DownPattern, layer);
                if (!_refMap.ContainsKey(un) || !_refMap.ContainsKey(dn)) yield break;
                float[]? gate = gn is not null && _refMap.ContainsKey(gn)
                    ? LoadF32(gn, (long)interm * d) : null;
                var up   = LoadF32(un, (long)interm * d);
                var down = LoadF32(dn, (long)d * interm);
                var gateD = gate is null ? null : ToD(gate, interm, d);
                var upD   = ToD(up,   interm, d);
                var downD = ToD(down, d,      interm);
                double theta = NoiseFloor(d);
                long cap = (long)RowTile * n;
                var oR = new int[cap]; var oC = new int[cap];
                var oV = new double[cap]; var oS = new long[cap];
                var typeId = RelationTypeRegistry.RelationTypeId(ctr.RelationName);
                var b = NewChunk(ctr.RelationName); int inChunk = 0;
                for (int rb = 0; rb < n; rb += RowTile)
                {
                    ct.ThrowIfCancellationRequested();
                    int re = Math.Min(rb + RowTile, n);
                    int cnt = RunFfnTile(embRawD, n, d, unembN, gateD, upD, downD, interm,
                                         rb, re, theta, oR, oC, oV, oS, cap);
                    for (int e = 0; e < cnt; e++)
                    {
                        b.AddAttestation(NativeAttestation.Aggregated(
                            ents[oR[e]], typeId, ents[oC[e]], _source, ctx, 1, oS[e], ModelWeight));
                        _strands++;
                        if (++inChunk >= RowsPerChange)
                        {
                            yield return b.SetInputUnitsConsumed(inChunk).Build();
                            b = NewChunk(ctr.RelationName); inChunk = 0;
                            await Task.Yield();
                        }
                    }
                }
                if (inChunk > 0) yield return b.SetInputUnitsConsumed(inChunk).Build();
                break;
            }
            default:
                _log.LogWarning("phase=etl L{L} unsupported path type {T}", layer, path.GetType().Name);
                break;
        }
    }

    // ── edge emission ─────────────────────────────────────────────────────────

    private async IAsyncEnumerable<SubstrateChange> EmitEdges(
        double[] left, int nLeft, double[] right, int nRight, int dim, string typeName,
        List<Hash128> subj, List<Hash128> obj, Hash128? ctx,
        System.Diagnostics.Stopwatch sw, [EnumeratorCancellation] CancellationToken ct,
        int leftBase = 0)
    {
        var typeId  = RelationTypeRegistry.RelationTypeId(typeName);
        double theta = NoiseFloor(dim);
        long cap  = (long)RowTile * nRight;
        var oR = new int[cap]; var oC = new int[cap];
        var oV = new double[cap]; var oS = new long[cap];
        bool selfPair = ReferenceEquals(left, right);
        var b = NewChunk(typeName); int inChunk = 0;
        for (int rb = 0; rb < nLeft; rb += RowTile)
        {
            ct.ThrowIfCancellationRequested();
            int re  = Math.Min(rb + RowTile, nLeft);
            int cnt = RunTile(left, rb, re, right, nRight, dim, theta, oR, oC, oV, oS, cap);
            for (int e = 0; e < cnt; e++)
            {
                int i = oR[e], j = oC[e];
                if (selfPair && i == j) continue;
                b.AddAttestation(NativeAttestation.Aggregated(
                    subj[leftBase + i], typeId, obj[j], _source, ctx, 1, oS[e], ModelWeight));
                _strands++;
                if (++inChunk >= RowsPerChange)
                {
                    yield return b.SetInputUnitsConsumed(inChunk).Build();
                    b = NewChunk(typeName); inChunk = 0;
                    await Task.Yield();
                }
            }
        }
        if (inChunk > 0) yield return b.SetInputUnitsConsumed(inChunk).Build();
    }

    // ── native kernel wrappers ────────────────────────────────────────────────

    private static unsafe int RunTile(
        double[] left, int rb, int re, double[] right, int nRight, int dim,
        double theta, int[] oR, int[] oC, double[] oV, long[] oS, long cap)
    {
        nuint count = 0; int overflow = 0;
        fixed (double* pl = left) fixed (double* pr = right)
        fixed (int* pR = oR) fixed (int* pC = oC)
        fixed (double* pV = oV) fixed (long* pS = oS)
            DynInterop.BilinearEdgesTile(pl, (nuint)rb, (nuint)re, pr, (nuint)nRight,
                (nuint)dim, theta, pR, pC, pV, pS, (nuint)cap, &count, &overflow);
        return (int)count;
    }

    private static unsafe int RunFfnTile(
        double[] emb, int n, int d, double[] unemb,
        double[]? gate, double[]? up, double[] down, int interm,
        int rb, int re, double theta,
        int[] oR, int[] oC, double[] oV, long[] oS, long cap)
    {
        nuint count = 0; int overflow = 0;
        var gs = gate ?? _one;
        var us = up   ?? _one;
        fixed (double* pe = emb) fixed (double* pu = unemb) fixed (double* pd = down)
        fixed (double* pg = gs)  fixed (double* pp = us)
        fixed (int* pR = oR)     fixed (int* pC = oC)
        fixed (double* pV = oV)  fixed (long* pS = oS)
            DynInterop.FfnTokenPairsTile(pe, (nuint)n, (nuint)d, pu,
                gate is null ? null : pg,
                up   is null ? null : pp,
                pd, (nuint)interm,
                (nuint)rb, (nuint)re, theta,
                pR, pC, pV, pS, (nuint)cap, &count, &overflow);
        return (int)count;
    }

    // ── math helpers ─────────────────────────────────────────────────────────

    private static double NoiseFloor(int dim)
    {
        double sigma = double.TryParse(
            Environment.GetEnvironmentVariable("LAPLACE_MODEL_NOISE_SIGMA"), out var s) ? s : 5.0;
        return sigma / Math.Sqrt(dim);
    }

    private static double[] Project(float[] W, int outDim, int inDim, float[] src, int n)
    {
        var o = new double[(long)n * outDim];
        unsafe
        {
            fixed (float* ps = src) fixed (float* pw = W) fixed (double* po = o)
                DynInterop.ProjectEmbedding(ps, (nuint)n, (nuint)inDim, pw, (nuint)outDim, po);
        }
        return o;
    }

    private static double[] ProjectD(float[] W, int outDim, int inDim, double[] src, int n)
    {
        var o = new double[(long)n * outDim];
        unsafe
        {
            fixed (double* ps = src) fixed (float* pw = W) fixed (double* po = o)
                DynInterop.ProjectEmbeddingD(ps, (nuint)n, (nuint)inDim, pw, (nuint)outDim, po);
        }
        return o;
    }

    private static double[] ExpandKvHeads(double[] kv, int n, int nHeads, int nKv, int headDim)
    {
        int kvDim = nKv * headDim, attnDim = nHeads * headDim;
        if (kvDim == attnDim) return kv;
        var o = new double[(long)n * attnDim];
        unsafe
        {
            fixed (double* pk = kv) fixed (double* po = o)
            {
                int rc = DynInterop.ExpandKvHeadsD(
                    pk, (nuint)n, (nuint)nHeads, (nuint)nKv, (nuint)headDim, po);
                if (rc != 0) throw new InvalidOperationException($"expand_kv_heads_d returned {rc}");
            }
        }
        return o;
    }

    private static double[] NormD(double[] v, int n, int dim)
    {
        unsafe
        {
            fixed (double* pv = v)
            {
                int rc = DynInterop.NormRowsD(pv, (nuint)n, (nuint)dim);
                if (rc != 0) throw new InvalidOperationException($"norm_rows_d returned {rc}");
            }
        }
        return v;
    }

    private static double[] ToD(float[] m, int n, int d)
    {
        var o = new double[(long)n * d];
        var map = new int[n];
        for (int i = 0; i < n; i++) map[i] = i;
        unsafe
        {
            fixed (float* src = m)
            fixed (int* rm = map)
            fixed (double* dst = o)
            {
                int rc = SynInterop.F32GatherToF64(src, rm, (nuint)n, (nuint)d, dst);
                if (rc != 0) throw new InvalidOperationException($"f32_gather_to_f64 returned {rc}");
            }
        }
        return o;
    }

    private static double[] SliceRowsD(double[] src, int rb, int re, int d)
    {
        var o = new double[(long)(re - rb) * d];
        Array.Copy(src, (long)rb * d, o, 0, o.LongLength);
        return o;
    }

    // ── tensor loading / gather ───────────────────────────────────────────────

    private float[] LoadF32(string name, long elems) =>
        WeightTensorETL.LoadTensorF32(_refMap, name, elems);

    private double[] GatherF32ToD(string tensorName, int vocab, int d, int[] entIdx, int n)
    {
        var raw = LoadF32(tensorName, (long)vocab * d);
        var o   = new double[(long)n * d];
        var map = new int[n];
        Array.Fill(map, -1);
        for (int t = 0; t < vocab; t++)
        {
            int idx = entIdx[t];
            if (idx >= 0 && idx < n) map[idx] = t;
        }
        unsafe
        {
            fixed (float* src = raw)
            fixed (int* rm = map)
            fixed (double* dst = o)
            {
                int rc = SynInterop.F32GatherToF64(src, rm, (nuint)n, (nuint)d, dst);
                if (rc != 0) throw new InvalidOperationException($"f32_gather_to_f64 returned {rc}");
            }
        }
        return o;
    }

    private SubstrateChangeBuilder NewChunk(string role) =>
        new SubstrateChangeBuilder(_source, $"etl/{role}", null,
            entityCapacity: RowsPerChange, physicalityCapacity: 0, attestationCapacity: RowsPerChange)
            .SetCommitEpoch(_commitEpoch);
}
