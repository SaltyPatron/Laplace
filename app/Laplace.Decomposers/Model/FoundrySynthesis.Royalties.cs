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
    private static async Task<int> SynthesizeMoldAModelAsync(
        RecipeDescriptor desc, string modelDir, string outputPath, string? scopeSource = null)
    {
        Console.WriteLine($"synthesize Mold-A-Model: {desc.Name} ({desc.Structure}) → {outputPath}");
        // Stage() prints each stage's wall time and the cumulative wall time.
        var swTotal = Stopwatch.StartNew();
        var swStage = Stopwatch.StartNew();
        void Stage(string label)
        {
            Console.WriteLine($"  [t] {label}: {swStage.Elapsed.TotalSeconds:F1}s (wall {swTotal.Elapsed.TotalSeconds:F1}s)");
            swStage.Restart();
        }
        CodepointPerfcache.Load(ResolveBlob());

        if (RejectRetiredFoundryEnvVars() is { } retired)
            return Fail(retired);

        if(desc.Structure is not ("dense" or "moe"))return Fail($"unknown structure {desc.Structure}");

        int nLayers = desc.NumLayers;
        int nHeads = desc.Layers[0].Heads.Count;
        int nKv = desc.Layers[0].KvHeads;
        foreach (var L in desc.Layers)
            if (L.Heads.Count != nHeads || L.KvHeads != nKv)
                return Fail("The selected Llama/Mixtral codec requires uniform heads and KV heads; the native program supports per-layer schedules.");
        if(nKv<1||nHeads%nKv!=0)return Fail("kv_heads must be a positive divisor of the layer head count");

        int moldDim = desc.HiddenSizeAuto
            ? Math.Max(nHeads * 64, FoundryDefaults.BasisRank)
            : desc.HiddenSizeOr(0);
        if (!desc.HiddenSizeAuto && moldDim <= 0)
            return Fail("hidden_size must be a positive integer or 'auto'");
        if (!desc.HiddenSizeAuto && moldDim % nHeads != 0)
            return Fail($"hidden_size {moldDim} must be a positive multiple of heads/layer {nHeads}");
        int intermR = desc.IntermediateSize > 0
            ? desc.IntermediateSize
            : RoundTo64(moldDim * 8 / 3);
        int headDim = moldDim / nHeads;




        // Source selection is carried into Native path and attribution calls.
        IReadOnlyList<byte[]>? scopeSourceIds = null;
        if (!string.IsNullOrWhiteSpace(scopeSource))
        {
            var names = scopeSource.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ids = new List<byte[]>(names.Length);
            await using (var probeDs = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString))
            {
                foreach (var name in names)
                {
                    var sourceIds = await NpgsqlFoundryReads.SourceIdsAsync(probeDs, name);
                    if (sourceIds.Count == 0)
                        return Fail($"--scope-source: unknown source '{name}' (use a registered source label or trunk ID)");
                    ids.AddRange(sourceIds);
                }
            }
            scopeSourceIds = ids;
            Console.WriteLine($"  scope: {names.Length} source(s) — Native paths and attribution select those registered witnesses ({scopeSource})");
        }
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        int degreeCap = FoundryDefaults.LeDegree;

        string tokenizerPath;
        if (desc.Vocab.Source == "tokenizer")
        {
            tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
            if (!File.Exists(tokenizerPath))
                return Fail($"vocab.source=tokenizer but no tokenizer.json in {modelDir}");
        }
        else
        {
            Console.WriteLine($"  vocab: substrate-native (source={desc.Vocab.Source}, size={desc.Vocab.Size}, "
                + $"seeds={desc.Vocab.Seeds.Count}, hops={desc.Vocab.Hops})");
            var selected = desc.Vocab.Source=="paths"
                ? await FoundryReadout.SelectVocabularyAsync(ds,desc,scopeSourceIds?.ToArray()) : null;
            string? moldCfg = await MaterializeNativeMoldAsync(
                desc.Vocab.Size > 0 ? desc.Vocab.Size : 2000,
                moldDim, nLayers, nHeads, nKv, intermR,
                crawlSeeds: desc.Vocab.Seeds.Count > 0 ? string.Join(",", desc.Vocab.Seeds) : null,
                crawlHops: desc.Vocab.Hops, crawlFanout: desc.Vocab.Fanout,
                grapheme: desc.Vocab.Source == "grapheme", selectedSurfaces: selected);
            if (moldCfg is null) return Fail("substrate vocab generation failed");
            tokenizerPath = Path.Combine(Path.GetDirectoryName(moldCfg)!, "tokenizer.json");
        }
        Stage("vocab crawl + mold materialize");
        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        int vocab = 0;
        foreach (var t in tokens) if (t.TokenId + 1 > vocab) vocab = t.TokenId + 1;
        if (vocab == 0) return Fail("tokenizer produced no tokens");

        var tokenSlots = new Dictionary<Hash128, List<int>>(tokens.Count);
        foreach (var t in tokens)
        {
            if (t.TokenId < 0 || t.TokenId >= vocab) continue;
            if (!tokenSlots.TryGetValue(t.EntityId, out var slots)) tokenSlots[t.EntityId] = slots = new List<int>(1);
            slots.Add(t.TokenId);
        }


        if (desc.Compile=="lookup") {
            int lookupDim=desc.HiddenSizeAuto
                ? Math.Max(moldDim,((vocab+nHeads*64-1)/(nHeads*64))*(nHeads*64)) : moldDim;
            if(lookupDim<vocab) return Fail($"lookup compile requires hidden_size >= vocab ({lookupDim} < {vocab})");
            var program=await FoundryReadout.ReadAsync(ds,desc,tokens,vocab,scopeSourceIds?.ToArray());
            byte[] lookupConfig=BuildHfConfigJson(lookupDim,nLayers,nHeads,nKv,intermR,vocab);
            var lookupRecipe=LlamaRecipeExtractor.ParseBytes(lookupConfig,"lookup architecture");
            using var lookupManifest=SynthInterop.ArchTemplateManifestMaterialize(lookupConfig,"llama",out var lookupError);
            if(lookupManifest is null) return Fail(lookupError ?? "lookup architecture manifest unavailable");
            return await WriteFaithfulGgufAsync(ds,lookupRecipe,tokens,program.Bindings.Operands,vocab,lookupDim,
                Path.GetDirectoryName(tokenizerPath)!,outputPath,lookupManifest.Specs,lookupManifest.Count,readout:program);
        }

        var opKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var L in desc.Layers) { foreach (var h in L.Heads) opKeys.Add(h.Key); opKeys.Add(L.Ffn.Key);foreach(var e in L.Experts)opKeys.Add(e.Key); }
        opKeys.Add(desc.LmHead.Key);
        if(desc.Embed.Op=="path")opKeys.Add(desc.Embed.Key);



        var neededTypes = opKeys
            .Where(k => k.StartsWith("relation:", StringComparison.Ordinal))
            .Select(k => RelationTypeRegistry.RelationTypeId(k["relation:".Length..]))
            .ToList();
        var swRead = Stopwatch.StartNew();
        var typePlanes = neededTypes.Count>0 ? await FoundryExport.ReadTypePlanesAsync(ds, tokenSlots, degreeCap, neededTypes) : [];
        var planeByType = new Dictionary<Hash128, FoundryExport.PlaneCoo>();
        var rankByType = new Dictionary<Hash128, double>();
        foreach (var tp in typePlanes)
        {
            planeByType[tp.TypeId] = FoundryExport.Normalize(tp.Plane);
            rankByType[tp.TypeId] = tp.Rank;
        }

        var pathPrograms=new Dictionary<string,FoundryReadout.Program>();
        var planeByOp = new Dictionary<string, FoundryExport.PlaneCoo>(StringComparer.Ordinal);
        // Relation-type rank from the manifest, normalized to the strongest rank in use;
        // it scales a non-continuation head's o_proj write-back.
        var opSalience = new Dictionary<string, double>(StringComparer.Ordinal);
        var trajPlane = FoundryExport.PlaneCoo.Empty;
        foreach (var opKey in opKeys)
        {
            FoundryExport.PlaneCoo plane;
            if(opKey.StartsWith("path:",StringComparison.Ordinal)) {
                var spec=desc.Layers.SelectMany(l=>l.Heads.Append(l.Ffn).Concat(l.Experts)).Append(desc.LmHead).Append(desc.Embed).First(op=>op.Key==opKey);
                var program=await FoundryReadout.ReadAsync(ds,desc with {LmHead=spec},tokens,vocab,scopeSourceIds?.ToArray());
                pathPrograms[opKey]=program;
                var rr=new List<int>(); var cc=new List<int>(); var ww=new List<double>();
                var aliases=Enumerable.Range(0,vocab).GroupBy(i=>program.Bindings.InputOperand[i]).ToDictionary(g=>g.Key,g=>g.ToArray());
                for(int edge=0;edge<program.Plane.Nnz;edge++)
                    foreach(int x in aliases[program.Plane.Rows[edge]]) foreach(int y in aliases[program.Plane.Cols[edge]]) {
                        rr.Add(x);cc.Add(y);ww.Add(program.Plane.Vals[edge]);
                    }
                plane=new(rr.ToArray(),cc.ToArray(),ww.ToArray());
            }
            else if (opKey.StartsWith("relation:", StringComparison.Ordinal))
            {
                var tid = RelationTypeRegistry.RelationTypeId(opKey["relation:".Length..]);
                plane = planeByType.TryGetValue(tid, out var p) ? p : FoundryExport.PlaneCoo.Empty;
                if (rankByType.TryGetValue(tid, out var rank) && rank > 0) opSalience[opKey] = rank;
            }
            else if (opKey.StartsWith("metric:", StringComparison.Ordinal))
                plane = FoundryExport.Normalize(await FoundryExport.ReadMetricEdgesAsync(
                    ds, tokenSlots, opKey["metric:".Length..], 16, 64, degreeCap));
            else if (opKey == "trajectory")
            {
                int gap = FoundryDefaults.TrajGap(nLayers);
                trajPlane = FoundryExport.Normalize(await FoundryExport.ReadTrajectoryStrideAsync(ds, gap, tokenSlots, degreeCap));
                plane = trajPlane;
            }
            else if (opKey == "unary")
                plane = FoundryExport.Normalize(await FoundryExport.ReadAdjacencyAsync(ds, tokenSlots, degreeCap));
            else if (opKey == "sentence_order")
                plane = FoundryExport.Normalize(await FoundryExport.ReadSentenceOrderAsync(ds, tokenSlots, cap: degreeCap));
            else if (opKey == "context")
                plane = FoundryExport.PlaneCoo.Empty; // special-cased at head fill: trajectory plane or identity QK
            else if (opKey is "conditional" or "conditional_pos")
                plane = FoundryExport.PlaneCoo.Empty; // floor mode: handled at embed/lm_head construction
            else
                return Fail($"recipe operator '{opKey}' has no plane reader — supported: relation:<TYPE>, metric:<name>, trajectory, unary, sentence_order, context, conditional, conditional_pos. A silently-empty plane here synthesized dead tensors before 2026-07-08.");
            planeByOp[opKey] = plane;
            Console.WriteLine($"  operator {opKey}: {plane.Nnz:N0} edges");
        }
        {
            double maxRank = opSalience.Count > 0 ? opSalience.Values.Max() : 1.0;
            foreach (var k in opSalience.Keys.ToList()) opSalience[k] /= maxRank;
        }
        Console.WriteLine($"  operator planes read in {swRead.Elapsed.TotalSeconds:F1}s");
        Stage("operator plane reads");


        var anchors = new double[vocab][];
        foreach (var t in tokens)
        {
            if (t.TokenId < 0 || t.TokenId >= vocab || !t.HasContentCoord) continue;
            anchors[t.TokenId] = [t.ContentX, t.ContentY, t.ContentZ, t.ContentM];
        }
        // Basis affinity is the positive part (Laplacian eigenmap); planeByOp stays
        // signed so refutation reaches attention as negative weight.
        var unionGraph = FoundryExport.Union(planeByOp.Values.Select(FoundryExport.PositivePart).ToArray());
        if (unionGraph.Nnz == 0) return Fail("no consensus over this vocab for any recipe operator — ingest content first");

        int dModel = moldDim;
        if (desc.HiddenSizeAuto)
        {
            int probeDim = Math.Min(FoundryDefaults.BasisRank + 1, vocab);
            probeDim = Math.Max(nHeads, ((probeDim + nHeads - 1) / nHeads) * nHeads);
            FoundryBasis.Spectral(vocab,probeDim,tokens,unionGraph,out int probeFeatures,out _);
            int spectral = Math.Max(nHeads, probeFeatures);
            dModel = Math.Max(nHeads, ((spectral + nHeads - 1) / nHeads) * nHeads);
            intermR = desc.IntermediateSize > 0 ? desc.IntermediateSize : RoundTo64(dModel * 8 / 3);
            headDim = dModel / nHeads;
            Console.WriteLine($"  hidden_size auto → native coordinate/spectral features {probeFeatures} → dModel={dModel}, ffn={intermR}");
        }

        bool coordEmbed = desc.Embed.Key == "coord";
        bool pathEmbed=desc.Embed.Op=="path";
        bool conditionalFloor = desc.Embed.Key is "conditional" or "conditional_pos";
        bool posFloor = desc.Embed.Key == "conditional_pos";
        double[]? lmHeadCond = null; // V·√Σ from the conditional SVD, used when lm_head op == "conditional"
        double[] E;
        object? basisReceipt=null;
        FoundryExport.BasisStats basisStats;
        if(pathEmbed) {
            var policy=desc.Embed.Key==desc.LmHead.Key?desc.LmHead:desc.Embed;
            E=pathPrograms[desc.Embed.Key].Embed(vocab,dModel,tokens,out int features,out basisReceipt,policy.MaxOutsideProbability,policy.EqualCandidates);
            basisStats=new FoundryExport.BasisStats(features,0,0);
            Console.WriteLine($"  embed: {features} selected-operator/coordinate features; constant RMS; no capacity noise");
        }
        else if (conditionalFloor)
        {
            // embed/lm_head are the two √Σ-weighted factors of the truncated SVD of the
            // log-conditional table M[x,y] = log P̂(y|x), so h(x)·lm(y) ≈ M[x,y] at
            // rank-d optimal error. The rows are left unnormalized and uncentered.
            if (desc.HiddenSizeAuto)
                return Fail("embed op 'conditional' requires an explicit hidden_size (the floor's rank IS the budget)");
            var (condPlane, rowDefault) = await FoundryExport.ReadConditionalPlaneAsync(
                ds, tokenSlots, vocab, trajs: FoundryDefaults.CorpusMax, cap: 512);
            Console.WriteLine($"  conditional plane: {condPlane.Nnz:N0} log-conditional entries");
            int kF = dModel - 1;
            double globalDefault = Math.Log(1.0 / Math.Max(2, vocab));
            var M = new float[(long)vocab * vocab];
            for (int x = 0; x < vocab; x++)
            {
                float def = (float)(double.IsNaN(rowDefault[x]) ? globalDefault : rowDefault[x]);
                long off = (long)x * vocab;
                for (int y = 0; y < vocab; y++) M[off + y] = def;
            }
            var seen = new System.Collections.BitArray(vocab * vocab);
            for (long e = 0; e < condPlane.Nnz; e++)
            {
                long idx = (long)condPlane.Rows[e] * vocab + condPlane.Cols[e];
                M[idx] = (float)condPlane.Vals[e];
                seen[(int)idx] = true;
            }

            if (posFloor)
            {
                // Class correction touches only unseen (backed-off) cells, centered per
                // source class so the row default's mean is unchanged; witnessed
                // log-conditionals keep their values.
                var (tokenClass, T, nClasses) = await FoundryExport.ReadPosCorrectionAsync(ds, tokenSlots, vocab);
                int known = tokenClass.Count(c => c >= 0);
                double g = FoundryDefaults.PosFloorGain;
                var classCount = new int[nClasses];
                foreach (int c in tokenClass) if (c >= 0) classCount[c]++;
                var rowMean = new double[nClasses];
                for (int cx = 0; cx < nClasses; cx++)
                {
                    double s = 0; long n = 0;
                    for (int cy = 0; cy < nClasses; cy++) { s += T[cx, cy] * classCount[cy]; n += classCount[cy]; }
                    rowMean[cx] = n > 0 ? s / n : 0.0;
                }
                for (int x = 0; x < vocab; x++)
                {
                    int cx = tokenClass[x];
                    if (cx < 0) continue;
                    long off = (long)x * vocab;
                    for (int y = 0; y < vocab; y++)
                    {
                        int cy = tokenClass[y];
                        if (cy >= 0 && !seen[(int)(off + y)])
                            M[off + y] += (float)(g * (T[cx, cy] - rowMean[cx]));
                    }
                }
                Console.WriteLine($"  pos floor correction (unseen-mass backoff): {nClasses} classes over {known:N0}/{vocab:N0} tokens, gain {g}");
            }

            var U = new float[(long)vocab * vocab];
            var S = new float[vocab];
            var Vt = new float[(long)vocab * vocab];
            int rcSvd = SvdTruncate(M, U, S, Vt, vocab, out nuint outRank);

            static unsafe int SvdTruncate(float[] M, float[] U, float[] S, float[] Vt, int vocab, out nuint outRank)
            {
                nuint rank = 0;
                int rc;
                fixed (float* pa = M) fixed (float* pu = U) fixed (float* ps = S) fixed (float* pvt = Vt)
                    rc = SynthInterop.TensorSvdTruncate(pa, (nuint)vocab, (nuint)vocab, 0.0, &rank, pu, ps, pvt, (nuint)vocab);
                outRank = rank;
                return rc;
            }
            if (rcSvd != 0) return Fail($"conditional-floor SVD failed rc={rcSvd} (vocab={vocab})");
            int kEff = Math.Min(kF, (int)outRank);
            E = new double[(long)vocab * dModel];
            lmHeadCond = new double[(long)vocab * dModel];
            for (int r = 0; r < kEff; r++)
            {
                double sq = Math.Sqrt(Math.Max(0f, S[r]));
                for (int t = 0; t < vocab; t++)
                {
                    E[(long)t * dModel + r] = sq * U[(long)t * vocab + r];
                    lmHeadCond[(long)t * dModel + r] = sq * Vt[(long)r * vocab + t];
                }
            }
            for (int t = 0; t < vocab; t++) E[(long)t * dModel + dModel - 1] = 1.0;
            basisStats = new FoundryExport.BasisStats(kEff, 0, 0.0);
            Console.WriteLine($"  conditional floor: rank {kEff}/{vocab} factorization of log P̂(y|x) "
                + $"(top σ {S[0]:F2}, σ[{kEff - 1}] {S[Math.Max(0, kEff - 1)]:F4})");
        }
        else if (coordEmbed)
        {
            await FoundryExport.FillCoordAnchorsAsync(ds, tokenSlots, anchors);
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
            Console.WriteLine($"  embed coord: {placed:N0} tokens = verbatim S³ coordinate ×{cs} (no Lanczos)");
        }
        else
        {
            E=FoundryBasis.Spectral(vocab,dModel,tokens,unionGraph,out int features,out basisReceipt);
            basisStats=new FoundryExport.BasisStats(features-2,0,0);
        }
        // Mean-center the content dims over live rows (the last dim is the bias dim the
        // gate calibration reads, left untouched), then rescale each row to RMS 1 so token
        // identity sits on the scale of block outputs and dot products carry the
        // differential signal. The conditional floor is skipped: its rows are
        // log-probability factors.
        if (!conditionalFloor && !pathEmbed && basisReceipt is null)
        {
            int dC0 = dModel - 1;
            var meanRow = new double[dC0];
            int live = 0;
            for (int t = 0; t < vocab; t++)
            {
                long off = (long)t * dModel;
                double n2 = 0;
                for (int c = 0; c < dC0; c++) n2 += E[off + c] * E[off + c];
                if (n2 <= 1e-24) continue;
                for (int c = 0; c < dC0; c++) meanRow[c] += E[off + c];
                live++;
            }
            if (live > 1) for (int c = 0; c < dC0; c++) meanRow[c] /= live;
            for (int t = 0; t < vocab; t++)
            {
                long off = (long)t * dModel;
                double n2 = 0;
                for (int c = 0; c < dC0; c++) n2 += E[off + c] * E[off + c];
                if (n2 <= 1e-24) continue;
                for (int c = 0; c < dC0; c++) E[off + c] -= meanRow[c];
            }
            for (int t = 0; t < vocab; t++)
            {
                long off = (long)t * dModel;
                double n2 = 0;
                for (int c = 0; c < dModel; c++) { double v = E[off + c]; n2 += v * v; }
                if (n2 <= 1e-24) continue;
                double scale = Math.Sqrt(dModel / n2);
                for (int c = 0; c < dModel; c++) E[off + c] *= scale;
            }
        }
        MirrorDualFormEmbeds(tokens, E, vocab, dModel);
        if(!pathEmbed) Console.WriteLine($"  embed basis: spectral K={basisStats.SpectralRank}, {basisStats.ZeroSpectralTokens:N0} off-graph");
        Stage("basis (eigensolve + discipline)");

        var attribution=await FoundrySourceAttribution.ReadAsync(ds,pathPrograms,scopeSourceIds?.ToArray());
        var sequences=new List<(NpgsqlFoundryReads.OrderedSequenceRow Evidence,int[] Slots)>();
        if(opKeys.Overlaps(["context","trajectory","sentence_order"])){
            if(desc.SequenceLimit<=0||desc.ContextLength<2)throw new InvalidOperationException("ordered operators require positive sequence_limit and context_length >= 2");
            var binding=FoundryReadout.Bind(tokens,vocab);
            var witnessed=await NpgsqlFoundryReads.ExportSequencesAsync(ds,binding.Operands.Keys.Select(h=>h.ToBytes()).ToArray(),desc.SequenceLimit,desc.ContextLength,scopeSourceIds?.ToArray());
            foreach(var sequence in witnessed){
                var slots=sequence.Constituents.Select(id=>binding.Operands[Hash128.FromBytes(id)][0]).ToArray();
                if(slots.Length>1)sequences.Add((sequence,slots));
            }
            if(sequences.Count==0)throw new InvalidOperationException("ordered operators have no complete witnessed trajectories in the selected vocabulary and source scope");
            Console.WriteLine($"  ordered observations: {sequences.Count} complete content trajectories");
        }
        return await FoundryNativeProgramHost.WriteAsync(desc,outputPath,Path.GetDirectoryName(tokenizerPath)!,tokens,vocab,dModel,intermR,E,planeByOp,pathPrograms,scopeSource,WriteGgufMetadata,sequences,attribution,basisReceipt);
    }

    private static byte[] BuildHfConfigJson(int hidden, int layers, int heads, int kv, int interm, int vocab)
    {
        string cfg = "{"
            + "\"architectures\":[\"LlamaForCausalLM\"],\"model_type\":\"llama\","
            + $"\"hidden_size\":{hidden},\"num_hidden_layers\":{layers},"
            + $"\"num_attention_heads\":{heads},\"num_key_value_heads\":{kv},"
            + $"\"intermediate_size\":{interm},\"vocab_size\":{vocab},"
            + "\"hidden_act\":\"silu\",\"rms_norm_eps\":1e-05,\"rope_theta\":10000.0,\"torch_dtype\":\"float32\""
            + "}";
        return System.Text.Encoding.UTF8.GetBytes(cfg);
    }



    private static int RoundTo64(int x) => Math.Max(64, ((x + 63) / 64) * 64);

    private static string? RejectRetiredFoundryEnvVars()
    {
        var retired = new List<string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && k.StartsWith("LAPLACE_FOUNDRY_", StringComparison.OrdinalIgnoreCase))
                retired.Add(k);
        }
        if (retired.Count == 0) return null;
        retired.Sort(StringComparer.OrdinalIgnoreCase);
        return "retired LAPLACE_FOUNDRY_* env vars are ignored — knobs live in FoundryDefaults: "
               + string.Join(", ", retired);
    }



    private static void MirrorDualFormEmbeds(IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,double[] e,int vocab,int width)
    {
        var binding=FoundryReadout.Bind(tokens,vocab);
        foreach(var t in tokens) {
            int operand=binding.InputOperand[t.TokenId];
            if(operand!=t.TokenId) Array.Copy(e,(long)operand*width,e,(long)t.TokenId*width,width);
        }
    }

    private static async Task PinCrawlSeedsInPlaceAsync(
        NpgsqlDataSource ds, string[] seeds, List<(string surface, long weight)> sel, int budget)
    {
        var bySurf = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (s, w) in sel) bySurf[s] = w;
        long pin = bySurf.Count > 0 ? bySurf.Values.Max() + 1 : 1_000_000;
        int resolved = 0, pinned = 0;
        foreach (var s in await NpgsqlFoundryReads.RenderResolvedWordSurfacesAsync(ds, seeds))
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            resolved++;
            if (!bySurf.ContainsKey(s)) pinned++;
            bySurf[s] = pin++;
        }
        sel.Clear();
        foreach (var kv in bySurf.OrderByDescending(x => x.Value).Take(budget))
            sel.Add((kv.Key, kv.Value));
        int unresolved = seeds.Length - resolved;
        if (unresolved > 0)
            Console.WriteLine($"  vocab pin: {resolved}/{seeds.Length} seeds in substrate ({unresolved} unresolved — not ingested); "
                + $"{pinned} newly pinned, {sel.Count:N0} surfaces in mold");
        else
            Console.WriteLine($"  vocab pin: all {resolved} crawl seeds reserved ({pinned} newly pinned), {sel.Count:N0} surfaces in mold");
    }

}
