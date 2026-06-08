using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model;

// Reads the whole model as its three native structures, content-addressed, at the calculated noise
// floor (theta = c/√dim). embed=lookup→token relations; FFN=key-value→neuron NODES + token↔neuron
// relations; attention=token relations folded across layers. Engine MKL computes (project + bilinear),
// C# marshals. No sampling, no n² noise, no morph.
public sealed class ModelTableETL
{
    private const int RowsPerChange = 500_000;
    private const int RowTile       = 128;
    private static readonly double ModelWeight =
        RelationTypeRegistry.Resolve("EMBEDS").Rank * SourceTrust.AiModelProbe;
    private static readonly Hash128 NeuronType = Hash128.OfCanonical("substrate/type/Neuron/v1");

    private static readonly double NoiseSigma =
        double.TryParse(Environment.GetEnvironmentVariable("LAPLACE_MODEL_NOISE_SIGMA"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var ns) && ns > 0 ? ns : 5.0;
    private static double NoiseFloor(int dim) => NoiseSigma / Math.Sqrt(Math.Max(1, dim));

    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _source;
    private readonly Hash128 _axisType;
    private readonly ILogger _log;
    private readonly Dictionary<string, SafetensorsContainerParser.TensorReference> _refMap;
    private long _strands;

    public ModelTableETL(string modelDir, LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens, Hash128 sourceId, Hash128 axisType,
        ILogger? log = null)
    {
        _recipe = recipe; _tokens = tokens; _source = sourceId; _axisType = axisType;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var refs = SafetensorsContainerParser.ParseModel(modelDir);
        _refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(refs.Count, StringComparer.Ordinal);
        foreach (var r in refs) _refMap[r.Name] = r;
    }

    public async IAsyncEnumerable<SubstrateChange> EmitAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        int d = _recipe.HiddenSize, vocab = _recipe.VocabSize;
        int nHeads = _recipe.NumHeads, nKv = Math.Max(1, _recipe.NumKvHeads), headDim = d / Math.Max(1, nHeads);
        int attnDim = nHeads * headDim, kvDim = nKv * headDim, interm = _recipe.IntermediateSize, layers = _recipe.NumLayers;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var ents = new List<Hash128>(); var ordToRow = new List<int>();
        { var seen = new Dictionary<Hash128, int>();
          foreach (var rec in _tokens) { if (rec.TokenId < 0 || rec.TokenId >= vocab) continue;
            if (seen.TryAdd(rec.EntityId, ents.Count)) { ents.Add(rec.EntityId); ordToRow.Add(rec.TokenId); } } }
        int n = ents.Count;
        _log.LogInformation("phase=etl tokens: {N:N0} distinct token entities", n);
        if (n == 0) yield break;

        var embAll = WeightTensorETL.LoadTensorF32(_refMap, "model.embed_tokens.weight", (long)vocab * d);
        var embF = Gather(embAll, ordToRow, n, d); embAll = null;
        var lmName = _refMap.ContainsKey("lm_head.weight") ? "lm_head.weight" : "model.embed_tokens.weight";
        var lmAll = WeightTensorETL.LoadTensorF32(_refMap, lmName, (long)vocab * d);
        var unembF = Gather(lmAll, ordToRow, n, d); lmAll = null;

        // ENTITIES FIRST. Deposit every token entity in entities-only batches — they carry no
        // relations, so they cannot fail the referential proof, and every later tier's endpoints are
        // guaranteed present. (Two-phase: all entities, then all relations.)
        { var eb = NewChunk("token"); int ec = 0;
          for (int i = 0; i < n; i++) { eb.AddEntity(new EntityRow(ents[i], 2, ModelDecomposer.TextTypeId, _source));
            if (++ec >= RowsPerChange) { yield return eb.Build(); eb = NewChunk("token"); ec = 0; } }
          if (ec > 0) yield return eb.Build(); }

        // TIER 1 — embed lookup → token↔token SIMILAR_TO.
        var embN = NormD(ToD(embF, n, d), n, d);
        await foreach (var c in EmitEdges(embN, n, embN, n, d, "SIMILAR_TO", ents, ents, null, sw, ct)) yield return c;
        var unembN = NormD(ToD(unembF, n, d), n, d);

        for (int l = 0; l < layers; l++)
        {
            ct.ThrowIfCancellationRequested();
            string p = $"model.layers.{l}.";
            var ctx = Hash128.OfCanonical($"substrate/context/{_sourceHex()}/L{l}/v1");
            // ROOT CAUSE of the C506D2D1 abort: every relation's context_id is itself a referenced
            // entity. Deposit the layer-context entity before any relation that rides on it.
            { var eb = NewChunk("ctx"); eb.AddEntity(new EntityRow(ctx, (byte)MetaTier.Meta, _axisType, _source)); yield return eb.Build(); }

            // TIER 3 — attention → token↔token, folded (same type, layer = context).
            var Wq = Get(p + "self_attn.q_proj.weight", attnDim, d);
            if (Wq != null) { var q = NormD(Project(Wq, attnDim, d, embF, n), n, attnDim);
                await foreach (var c in EmitEdges(q, n, q, n, attnDim, "ATTENDS", ents, ents, ctx, sw, ct)) yield return c; }
            var Wv = Get(p + "self_attn.v_proj.weight", kvDim, d);
            if (Wv != null) { var v = NormD(Project(Wv, kvDim, d, embF, n), n, kvDim);
                await foreach (var c in EmitEdges(v, n, v, n, kvDim, "OV_RELATES", ents, ents, ctx, sw, ct)) yield return c; }

            // TIER 2 — FFN key-value: neurons are NODES (content-addressed by their gate key),
            // token→neuron DETECTS (gate·embed), neuron→token WRITES (down·unembed).
            var gate = Get(p + "mlp.gate_proj.weight", interm, d);
            var down = Get(p + "mlp.down_proj.weight", d, interm);
            if (gate != null)
            {
                // Entities first: deposit this layer's neuron entities (entities-only) before any
                // DETECTS/WRITES that reference them.
                var neuronIds = new List<Hash128>(interm);
                { var eb = NewChunk("neuron"); int ec = 0;
                  for (int j = 0; j < interm; j++) {
                      var id = Hash128.Blake3(RowBytes(gate, j, d));
                      neuronIds.Add(id);
                      eb.AddEntity(new EntityRow(id, (byte)MetaTier.Meta, NeuronType, _source));
                      if (++ec >= RowsPerChange) { yield return eb.Build(); eb = NewChunk("neuron"); ec = 0; } }
                  if (ec > 0) yield return eb.Build(); }
                var gateN = NormD(ToD(gate, interm, d), interm, d);
                await foreach (var c in EmitEdges(embN, n, gateN, interm, d, "DETECTS", ents, neuronIds, ctx, sw, ct)) yield return c;
                if (down != null) {
                    var downT = Transpose(down, d, interm);          // [interm × d] neuron outputs in channel space
                    var downN = NormD(ToD(downT, interm, d), interm, d);
                    await foreach (var c in EmitEdges(downN, interm, unembN, n, d, "WRITES", neuronIds, ents, ctx, sw, ct)) yield return c;
                }
            }
            _log.LogInformation("phase=etl layer {L}/{Layers} (cum strands {S:N0}, {Sec:F0}s)", l + 1, layers, _strands, sw.Elapsed.TotalSeconds);
        }
        _log.LogInformation("phase=etl WHOLE MODEL done: {S:N0} strands (noise floor {Sig}σ/√dim) in {Sec:F0}s", _strands, NoiseSigma, sw.Elapsed.TotalSeconds);
    }

    private string _sourceHex() { Span<byte> b = stackalloc byte[16]; _source.WriteBytes(b); return Convert.ToHexString(b); }

    private float[]? Get(string name, int outDim, int inDim) =>
        _refMap.ContainsKey(name) ? WeightTensorETL.LoadTensorF32(_refMap, name, (long)outDim * inDim) : null;

    private static float[] Gather(float[] all, List<int> rows, int n, int d) {
        var o = new float[(long)n * d];
        System.Threading.Tasks.Parallel.For(0, n, i => Array.Copy(all, (long)rows[i] * d, o, (long)i * d, d));
        return o;
    }
    private static byte[] RowBytes(float[] m, int row, int d) {
        var b = new byte[d * 4]; Buffer.BlockCopy(m, row * d * 4, b, 0, d * 4); return b;
    }
    private static float[] Transpose(float[] m, int rows, int cols) {  // [rows×cols] → [cols×rows]
        var o = new float[(long)rows * cols];
        System.Threading.Tasks.Parallel.For(0, rows, r => { for (int c = 0; c < cols; c++) o[(long)c * rows + r] = m[(long)r * cols + c]; });
        return o;
    }
    private static double[] ToD(float[] m, int n, int d) {
        var o = new double[(long)n * d];
        System.Threading.Tasks.Parallel.For(0, n, i => { long e = (long)i * d; for (int c = 0; c < d; c++) o[e + c] = m[e + c]; });
        return o;
    }
    private static double[] Project(float[] W, int outDim, int inDim, float[] src, int n) {
        var outp = new double[(long)n * outDim];
        unsafe { fixed (float* ps = src) fixed (float* pw = W) fixed (double* po = outp)
            DynInterop.ProjectEmbedding(ps, (nuint)n, (nuint)inDim, pw, (nuint)outDim, po); }
        return outp;
    }
    private static double[] NormD(double[] v, int n, int dim) {
        System.Threading.Tasks.Parallel.For(0, n, i => { long e = (long)i * dim; double ss = 0;
            for (int c = 0; c < dim; c++) ss += v[e + c] * v[e + c];
            double inv = ss > 0 ? 1.0 / Math.Sqrt(ss) : 0; for (int c = 0; c < dim; c++) v[e + c] *= inv; });
        return v;
    }

    private async IAsyncEnumerable<SubstrateChange> EmitEdges(
        double[] left, int nLeft, double[] right, int nRight, int dim, string kindName,
        List<Hash128> subj, List<Hash128> obj, Hash128? ctx,
        System.Diagnostics.Stopwatch sw, [EnumeratorCancellation] CancellationToken ct,
        List<EntityRow>? seed = null)
    {
        var kind = RelationTypeRegistry.RelationTypeId(kindName);
        double theta = NoiseFloor(dim);
        long cap = (long)RowTile * nRight;
        var oR = new int[cap]; var oC = new int[cap]; var oV = new double[cap];
        var b = NewChunk(kindName); int inChunk = 0;
        if (seed != null) foreach (var e in seed) b.AddEntity(e);   // fence: referenced entities in-batch
        bool selfPair = ReferenceEquals(left, right);
        for (int rb = 0; rb < nLeft; rb += RowTile)
        {
            int re = Math.Min(rb + RowTile, nLeft);
            int count = RunTile(left, rb, re, right, nRight, dim, theta, oR, oC, oV, cap);
            for (int e = 0; e < count; e++)
            {
                int i = oR[e], j = oC[e];
                if (selfPair && i == j) continue;
                long s = (long)(AttestationFactory.Score(oV[e], 1.0) * Glicko2.FpScale);
                b.AddAttestation(AttestationFactory.CreateAggregated(subj[i], kind, obj[j], _source, ctx, 1, s, ModelWeight));
                _strands++;
                if (++inChunk >= RowsPerChange) { yield return b.Build(); b = NewChunk(kindName); inChunk = 0; await Task.Yield(); }
            }
        }
        if (inChunk > 0) yield return b.Build();
    }

    private static unsafe int RunTile(double[] left, int rb, int re, double[] right, int nRight, int dim,
        double theta, int[] oR, int[] oC, double[] oV, long cap) {
        nuint count = 0; int overflow = 0;
        fixed (double* pl = left) fixed (double* pr = right) fixed (int* pR = oR) fixed (int* pC = oC) fixed (double* pV = oV)
            DynInterop.BilinearEdgesTile(pl, (nuint)rb, (nuint)re, pr, (nuint)nRight, (nuint)dim, theta, pR, pC, pV, (nuint)cap, &count, &overflow);
        return (int)count;
    }

    private SubstrateChangeBuilder NewChunk(string role) =>
        new(_source, $"etl/{role}", null, entityCapacity: RowsPerChange, physicalityCapacity: 0, attestationCapacity: RowsPerChange);
}
