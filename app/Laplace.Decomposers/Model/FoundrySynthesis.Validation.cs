using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Code;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.ISO;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.OMW;
using Laplace.Decomposers.Tatoeba;
using Laplace.Decomposers.OpenSubtitles;
using Laplace.Decomposers.SemLink;
using Laplace.Decomposers.Unicode;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Dynamics;
using DynamicsInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

public static partial class FoundrySynthesis
{
    private static async Task<int> SynthesizeFromSubstrateAsync(string recipePath, string outputPath, bool grapheme = false, string? scopeSource = null, bool faithful = false, string? format = null)
    {
        if (string.IsNullOrEmpty(recipePath) || !File.Exists(recipePath))
            return Fail(
                "usage: laplace-mono synthesize substrate <recipe.json> [output.gguf]\n"
                + $"  (recipe not found: {recipePath})");



        string recipeText = await File.ReadAllTextAsync(recipePath);
        if (recipeText.Contains("\"laplace.recipe\"", StringComparison.Ordinal))
        {
            if(format is not null) {
                using var recipeDocument=JsonAstDocument.Parse(recipeText);
                if(!recipeDocument.Root.IsObject)throw new InvalidDataException("Mold-a-Model recipe must be a JSON object");
                using var buffer=new MemoryStream();
                using(var writer=new Utf8JsonWriter(buffer)) {
                    writer.WriteStartObject();
                    foreach(var (key,value) in recipeDocument.Root.Pairs()) {
                        if(key=="format")continue;
                        writer.WritePropertyName(key);value.WriteTo(writer);
                    }
                    writer.WriteString("format",format);writer.WriteEndObject();
                }
                recipeText=Encoding.UTF8.GetString(buffer.ToArray());
            }
            var moldDesc = RecipeDescriptor.Parse(recipeText);
            string moldDir = Path.GetDirectoryName(recipePath) ?? ".";
            return await SynthesizeMoldAModelAsync(moldDesc, moldDir, outputPath, scopeSource);
        }
        if (scopeSource is not null)
            return Fail("--scope-source is supported for laplace.recipe (Mold-A-Model) synthesis runs only");

        Console.WriteLine($"synthesize substrate (foundry) → {outputPath}");
        CodepointPerfcache.Load(ResolveBlob());

        string modelDir = Path.GetDirectoryName(recipePath) ?? ".";
        string tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        if (!File.Exists(tokenizerPath))
            return Fail($"tokenizer.json not found alongside recipe: {tokenizerPath}");

        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        var recipe = LlamaRecipeExtractor.Parse(recipePath);
        int vocab = recipe.VocabSize;
        int dModel = recipe.HiddenSize;

        var tokenSlots = new Dictionary<Hash128, List<int>>(tokens.Count);
        foreach (var t in tokens)
        {
            if (t.TokenId < 0 || t.TokenId >= vocab) continue;





            if (grapheme && t.IsByteLevel) continue;
            if (!tokenSlots.TryGetValue(t.EntityId, out var slots))
                tokenSlots[t.EntityId] = slots = new List<int>(1);
            slots.Add(t.TokenId);
        }
        var (_, moldName) = ModelDecomposer.SourceForModel(modelDir);
        Console.WriteLine($"  mold: {moldName}");
        int nHeadsR = recipe.NumHeads, nKvR = recipe.NumKvHeads;
        int headDimR = dModel / Math.Max(1, nHeadsR);
        int attnOutR = nHeadsR * headDimR, kvDimR = nKvR * headDimR;
        int intermR = recipe.IntermediateSize;
        int nLayers = recipe.NumLayers;

        byte[] configJson = File.ReadAllBytes(recipePath);
        using var manifest = SynthInterop.ArchTemplateManifestMaterialize(configJson, "llama", out var manifestError);
        if (manifest is null) return Fail(manifestError ?? "architecture manifest unavailable");
        TensorSpec[] specs = manifest.Specs;
        int tensorCount = manifest.Count;
        Console.WriteLine($"  recipe + arch template: {tensorCount} tensor slots, vocab={vocab}, hidden={dModel}, "
            + $"layers={nLayers}, heads={nHeadsR}/{nKvR}, ffn={intermR}");

        if (RejectRetiredFoundryEnvVars() is { } retired)
            return Fail(retired);

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);

        if (faithful)
        {
            int faithfulRc = await WriteFaithfulGgufAsync(
                ds, recipe, tokens, tokenSlots, vocab, dModel, modelDir, outputPath,
                specs, tensorCount, grapheme);
            return faithfulRc == 0 ? 0 : Fail($"faithful foundry write failed (status {faithfulRc})");
        }



        int degreeCap = FoundryDefaults.LeDegree;
        var swPour = Stopwatch.StartNew();











        Task<FoundryExport.PlaneCoo> LayerMaskedAsync(Mask256 bandMask)
            => FoundryExport.ReadLayerPlaneMaskedAsync(ds, bandMask, tokenSlots, degreeCap);




        var simMask = HighwayPerfcache.BandMask(3);
        var relMask = HighwayPerfcache.BandMask(0) | HighwayPerfcache.BandMask(1)
                    | HighwayPerfcache.BandMask(2) | HighwayPerfcache.BandMask(4);
        var preMask = HighwayPerfcache.BandMask(5);
        var attMask = HighwayPerfcache.BandMask(6) | HighwayPerfcache.BandMask(7);

        Task<FoundryExport.PlaneCoo> simTask, relTask, preTask, attTask;
        if (!simMask.IsZero)
        {
            simTask = LayerMaskedAsync(simMask);
            relTask = LayerMaskedAsync(relMask);
            preTask = LayerMaskedAsync(preMask);
            attTask = LayerMaskedAsync(attMask);
        }
        else
        {

            Task<FoundryExport.PlaneCoo> LayerAsync(double lo, double hi)
                => FoundryExport.ReadLayerPlaneAsync(ds, lo, hi, tokenSlots, degreeCap);
            simTask = LayerAsync(0.78, 0.87);
            relTask = LayerAsync(0.70, 1.001);
            preTask = LayerAsync(0.55, 0.70);
            attTask = LayerAsync(0.30, 0.52);
        }
        await Task.WhenAll(simTask, relTask, preTask, attTask);
        var sim = FoundryExport.Normalize(simTask.Result);
        var rel = FoundryExport.Normalize(relTask.Result);
        var pre = FoundryExport.Normalize(preTask.Result);
        var att = FoundryExport.Normalize(attTask.Result);
        long planeEdges = (long)sim.Nnz + rel.Nnz + pre.Nnz + att.Nnz;
        Console.WriteLine($"  full rank-grouped consensus read in {swPour.Elapsed.TotalSeconds:F1}s "
            + $"(vocab {tokenSlots.Count:N0}, cap {degreeCap}): equivalence(embed)={sim.Nnz:N0} "
            + $"taxonomic+partitive(V/O)={rel.Nnz:N0} causal+seq(FFN)={pre.Nnz:N0} associative(attn)={att.Nnz:N0}");
        if (planeEdges == 0)
            return Fail("no entity→entity consensus over this vocab — ingest text first");





        int trajGap = FoundryDefaults.TrajGap(nLayers);
        var swTraj = Stopwatch.StartNew();
        var traj = FoundryExport.Normalize(
            await FoundryExport.ReadTrajectoryStrideAsync(ds, trajGap, tokenSlots, degreeCap));
        Console.WriteLine($"  trajectory order ladder read in {swTraj.Elapsed.TotalSeconds:F1}s "
            + $"(gap≤{trajGap}): {traj.Nnz:N0} ordered continuation edges");




        var adjacency = FoundryExport.Normalize(
            await FoundryExport.ReadAdjacencyAsync(ds, tokenSlots, degreeCap));
        Console.WriteLine($"  rank-weighted adjacency read: {adjacency.Nnz:N0} content consensus.edges(banked relation_rank)");



        var anchors = new double[vocab][];
        foreach (var t in tokens)
        {
            if (t.TokenId < 0 || t.TokenId >= vocab || !t.HasContentCoord) continue;
            anchors[t.TokenId] = [t.ContentX, t.ContentY, t.ContentZ, t.ContentM];
        }
        var basisSeed = Hash128.Blake3(recipe.CanonicalJson);









        string attnMetric = FoundryDefaults.AttnMetric;
        var attnPlane = att;
        if (attnMetric is "frechet" or "hausdorff" or "angular")
        {
            int mK = FoundryDefaults.MetricK;
            int mProbe = FoundryDefaults.MetricProbe;
            var swMetric = Stopwatch.StartNew();
            attnPlane = FoundryExport.Normalize(
                await FoundryExport.ReadMetricEdgesAsync(ds, tokenSlots, attnMetric, mK, mProbe, degreeCap));
            Console.WriteLine($"  METRIC HEAD ({attnMetric}): {attnPlane.Nnz:N0} trajectory-metric edges "
                + $"in {swMetric.Elapsed.TotalSeconds:F1}s — a head transcribes laplace_{attnMetric}_4d, not a learned pattern");


            int coordFilled = await FoundryExport.FillCoordAnchorsAsync(ds, tokenSlots, anchors);
            Console.WriteLine($"  S³ frame: {coordFilled:N0} tokens placed at their native coordinate verbatim (no LE/Procrustes)");
        }



        if (FoundryDefaults.CoordOnly && attnMetric == "")
        {
            int coordOnlyFilled = await FoundryExport.FillCoordAnchorsAsync(ds, tokenSlots, anchors);
            Console.WriteLine($"  S³ COORD-ONLY: {coordOnlyFilled:N0} tokens placed at native coordinate (NO Lanczos eigensolve)");
        }
        var swBasis = Stopwatch.StartNew();




        double metricBasisGain = FoundryDefaults.MetricBasisGain;
        var metricForBasis = attnMetric != ""
            ? attnPlane with { Vals = Array.ConvertAll(attnPlane.Vals, v => v * metricBasisGain) }
            : attnPlane;
        // The consensus planes are signed (walk_edge_weight carries the sign of
        // rating − neutral). The eigenmap affinity takes only their positive part; the
        // operator planes projected below stay signed, so refutation reaches attention
        // as negative weight.
        var unionGraph = attnMetric != ""
            ? FoundryExport.Union(
                FoundryExport.PositivePart(sim), FoundryExport.PositivePart(rel),
                FoundryExport.PositivePart(pre), FoundryExport.PositivePart(att),
                FoundryExport.PositivePart(metricForBasis))
            : FoundryExport.Union(
                FoundryExport.PositivePart(sim), FoundryExport.PositivePart(rel),
                FoundryExport.PositivePart(pre), FoundryExport.PositivePart(att));




        string? affRaw = null;


        double[] E;
        FoundryExport.BasisStats basisStats;
        if (FoundryDefaults.CoordOnly)
        {




            double cs = FoundryDefaults.CoordScale;
            E = new double[(long)vocab * dModel];
            int placed = 0;
            for (int t = 0; t < vocab; t++)
            {
                if (anchors[t] is not { } a) continue;
                for (int d = 0; d < 4 && d < dModel; d++) E[(long)t * dModel + d] = a[d] * cs;
                placed++;
            }
            basisStats = new FoundryExport.BasisStats(4, vocab - placed, 0.0);
            Console.WriteLine($"  EXACT S³ EMBED: {placed:N0} tokens = verbatim coordinate ×{cs} (no LE/GSO/Procrustes/Lanczos/SVD)");
        }
        else
        {
            bool affBasis = attnMetric == "" && affRaw == "1";
            Console.WriteLine($"  basis path: {(affBasis ? "AFFINITY-SVD (token = SVD of its relational row)" : "Laplacian-eigenmaps")} (vocab {vocab})");
            E = affBasis
                ? FoundryExport.BuildBasisAffinity(vocab, dModel, unionGraph, anchors, basisSeed, out basisStats)
                : FoundryExport.BuildBasis(vocab, dModel, unionGraph, anchors, basisSeed, out basisStats,
                    coordDirect: attnMetric != "", coordScale: attnMetric != "" ? FoundryDefaults.CoordScale : null);
        }
        Console.WriteLine($"  basis generated in {swBasis.Elapsed.TotalSeconds:F1}s: "
            + $"spectral K={basisStats.SpectralRank}, {basisStats.ZeroSpectralTokens:N0} tokens off-graph (capacity-only rows), "
            + $"procrustes residual={basisStats.ProcrustesResidual:F4}");
        MirrorDualFormEmbeds(tokens, E, vocab, dModel);


        double relTol = FoundryDefaults.RelErrTol;
        int kAttn = Math.Min(kvDimR, dModel);
        int kFfn = Math.Min(intermR, dModel);











        var completion = FoundryExport.Normalize(FoundryExport.Union(pre, traj));
        var rankPlanes = new[] { attnPlane, rel, completion, sim };
        var rankNames = new[] { attnMetric != "" ? $"metric:{attnMetric}" : "associative", "taxo+part", "causal+seq", "equivalence" };
        int nOps = rankPlanes.Length;
        var fOvR = new FoundryExport.Factors[nOps];
        var fFfnR = new FoundryExport.Factors[nOps];
        var fAttnR = new FoundryExport.Factors[nOps];
        var swOps = Stopwatch.StartNew();
        for (int r = 0; r < nOps; r++)
        {
            var lowered = FoundryExport.LowerOperator(E, vocab, dModel, rankPlanes[r]);
            var m = lowered.Values;








            var mResid = (double[])m.Clone();
            for (int d = 0; d < dModel; d++) mResid[(long)d * dModel + d] -= 1.0;
            fOvR[r] = FoundryExport.Factor(mResid, dModel, kAttn, relTol, transpose: true);
            fFfnR[r] = FoundryExport.Factor(mResid, dModel, kFfn, relTol, transpose: true);
            fAttnR[r] = FoundryExport.Factor(lowered.Scores, dModel, kAttn, relTol, transpose: false);
        }
        if (attnMetric != "")
            FoundryExport.ReportMetricHeadFidelity(E, vocab, dModel, attnPlane, fAttnR[0], attnMetric);












        var lmHead = new double[(long)vocab * dModel];
        {
            int dC = dModel - 1;
            var inDeg = new double[vocab];








            bool generative = FoundryDefaults.Generative;
            var roPlanes = generative ? new[] { traj } : new[] { traj, adjacency };
            var roW = generative ? new[] { 1.0 } : new[] { 1.0, 1.0 };
            for (int pi = 0; pi < roPlanes.Length; pi++)
            {
                var pl = roPlanes[pi]; double rw = roW[pi];
                for (long e2 = 0; e2 < pl.Nnz; e2++)
                {
                    int x = pl.Rows[e2], y = pl.Cols[e2];
                    if (x < 0 || x >= vocab || y < 0 || y >= vocab) continue;
                    double w = rw * pl.Vals[e2];
                    long yo = (long)y * dModel, xo = (long)x * dModel;
                    for (int c = 0; c < dC; c++) lmHead[yo + c] += w * E[xo + c];
                    inDeg[y] += Math.Abs(w);
                }
            }






            for (int v = 0; v < vocab; v++)
            {
                long off = (long)v * dModel;
                double idf = 1.0 / (inDeg[v] + 1.0);
                for (int c = 0; c < dC; c++) lmHead[off + c] *= idf;
                lmHead[off + dC] = 0.0;
            }






            {
                int suppressed = 0;
                foreach (var t in tokens)
                {
                    if (t.TokenId < 0 || t.TokenId >= vocab) continue;
                    if (!(t.IsByteLevel || !t.Role.HasFlag(TokenRole.LeadingSpace))) continue;
                    long o = (long)t.TokenId * dModel;
                    for (int c = 0; c < dModel; c++) lmHead[o + c] = 0.0;
                    suppressed++;
                }
                Console.WriteLine($"  lm_head: suppressed {suppressed:N0} byte + bare-alias tokens (space-led word continuations only)");
            }

            double meanSq = 0;
            for (int v = 0; v < vocab; v++)
            {
                long off = (long)v * dModel; double n2 = 0;
                for (int c = 0; c < dC; c++) { double t = lmHead[off + c]; n2 += t * t; }
                meanSq += n2;
            }
            meanSq /= Math.Max(1, vocab);
            double g = meanSq > 1e-24 ? 1.0 / Math.Sqrt(meanSq) : 1.0;
            for (long i = 0; i < (long)vocab * dModel; i++) lmHead[i] *= g;
        }
        Console.WriteLine($"  {nOps} per-rank operators projected + factored in {swOps.Elapsed.TotalSeconds:F1}s: "
            + string.Join("; ", Enumerable.Range(0, nOps).Select(r =>
                $"{rankNames[r]} OV r{fOvR[r].Rank}/FFN r{fFfnR[r].Rank}/attn r{fAttnR[r].Rank} (s0 {fOvR[r].SpectralNorm:F0})")));





        double attnGainEnv = FoundryDefaults.AttnGain;
        double residGainEnv = FoundryDefaults.ResidGain;



        double gateZ = FoundryDefaults.GateZ;
        double gateCol = gateZ / Math.Sqrt(dModel / 2.0);
        double upGain = 1.0 / FoundryExport.Silu(gateZ);







        int WriteCast(string outPath, double aGain, double rGain)
        {









            double split = Math.Pow(Math.Max(1, nLayers), -0.25);
            double attnScale = aGain * split;
            double layerScale = rGain * split;
            var gguf = SynthInterop.GgufWriterCreate(outPath);
            if (gguf == IntPtr.Zero) { Console.WriteLine($"  gguf_writer_create failed for {outPath}"); return 2; }
            WriteGgufMetadata(gguf, recipe, tokens, modelDir, byteBpe: true);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < tensorCount; i++)
            {
                string name; ulong rows, cols; int dtype;
                unsafe
                {
                    var sp = specs[i];
                    name = Marshal.PtrToStringUTF8((IntPtr)sp.Name) ?? "";
                    rows = sp.Rank >= 1 ? sp.Shape[0] : 1;
                    cols = sp.Rank >= 2 ? sp.Shape[1] : 1;
                    dtype = 0;
                }
                long nElem = (long)rows * (long)Math.Max(1UL, cols);
                var vals = new float[nElem];
                int tr = (int)rows, tc = (int)Math.Max(1UL, cols);



                if (name is "model.embed_tokens.weight" or "lm_head.weight")
                {


                    var src = name == "lm_head.weight" ? lmHead : E;
                    for (int r = 0; r < tr; r++)
                        for (int c = 0; c < tc; c++)
                            vals[(long)r * tc + c] = (float)src[(long)r * dModel + c];
                }
                else if (name == "model.norm.weight"
                         || name.EndsWith("input_layernorm.weight", StringComparison.Ordinal)
                         || name.EndsWith("post_attention_layernorm.weight", StringComparison.Ordinal))
                {
                    Array.Fill(vals, 1.0f);
                }
                else if (name.StartsWith("model.layers.", StringComparison.Ordinal))
                {
                    int layerDot = name.IndexOf('.', "model.layers.".Length);
                    int layerIdx = int.Parse(name["model.layers.".Length..layerDot]);
                    string rest = name[(layerDot + 1)..];









                    const int RANK_COMPLETION = 2, RANK_ASSOC = 0, RANK_TAXO = 1;
                    int last = nLayers - 1;
                    int aIdx, fIdx;
                    if (layerIdx == last) { aIdx = RANK_COMPLETION; fIdx = RANK_COMPLETION; }
                    else if (layerIdx == 0) { aIdx = RANK_ASSOC; fIdx = RANK_TAXO; }
                    else { aIdx = layerIdx % nOps; fIdx = (layerIdx + 1) % nOps; }
                    var fAttn = fAttnR[aIdx];
                    var fOv = fOvR[aIdx];
                    var fFfn = fFfnR[fIdx];



                    bool coordHead = attnMetric != "" && aIdx == 0;
                    double coordHeadScale = FoundryDefaults.CoordHeadScale(headDimR);
                    switch (rest)
                    {
                        case "self_attn.q_proj.weight":
                            if (coordHead) FoundryExport.FillCoordHead(vals, tr, tc, headDimR, 4, coordHeadScale);
                            else FoundryExport.FillRows(vals, tr, tc, fAttn, attnScale); break;
                        case "self_attn.k_proj.weight":
                            if (coordHead) FoundryExport.FillCoordHead(vals, tr, tc, headDimR, 4, coordHeadScale);
                            else FoundryExport.FillRowsRight(vals, tr, tc, fAttn, attnScale); break;
                        case "self_attn.v_proj.weight": FoundryExport.FillRowsRight(vals, tr, tc, fOv, layerScale); break;
                        case "self_attn.o_proj.weight": FoundryExport.FillCols(vals, tr, tc, fOv, layerScale); break;
                        case "mlp.gate_proj.weight": FoundryExport.FillGate(vals, tr, tc, gateCol); break;
                        case "mlp.up_proj.weight": FoundryExport.FillRowsRight(vals, tr, tc, fFfn, layerScale * upGain); break;
                        case "mlp.down_proj.weight": FoundryExport.FillCols(vals, tr, tc, fFfn, layerScale); break;
                        default:
                            Console.WriteLine($"  foundry does not define mold tensor '{name}'");
                            SynthInterop.GgufWriterFree(gguf); return 3;
                    }
                }
                else
                {
                    Console.WriteLine($"  foundry does not define mold tensor '{name}'");
                    SynthInterop.GgufWriterFree(gguf); return 3;
                }

                byte[] tensorBytes = dtype == 0
                    ? FoundryExport.ToF32Bytes(vals)
                    : FoundryExport.ToBf16Bytes(vals);

                nuint[] ggufDims = cols > 1 ? [(nuint)cols, (nuint)rows] : [(nuint)rows];
                unsafe
                {
                    fixed (nuint* dimsPtr = ggufDims)
                    fixed (byte* dataPtr = tensorBytes)
                        SynthInterop.GgufWriterAddTensor(gguf, HfToGgmlName(name), dtype, dimsPtr, (nuint)ggufDims.Length, dataPtr);
                }
            }
            int rcw = SynthInterop.GgufWriterFinalize(gguf);
            SynthInterop.GgufWriterFree(gguf);
            if (rcw != 0) { Console.WriteLine($"  gguf_writer_finalize failed (rc={rcw}) for {outPath}"); return 4; }
            long fsz = new FileInfo(outPath).Length;
            Console.WriteLine($"synthesis complete: {outPath} | recipe L={nLayers} H={nHeadsR} D={dModel} V={vocab} ({fsz / 1048576.0:F0} MB, attn={aGain} resid={rGain}) in {sw.Elapsed.TotalSeconds:F1}s");
            return 0;
        }

        int status = WriteCast(outputPath, attnGainEnv, residGainEnv);
        if (status == 0)
            MaybeEncryptExportedCartridge(outputPath);

        return status == 0 ? 0 : Fail($"foundry write failed (status {status})");
    }

}
