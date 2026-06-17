using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.Code;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.ISO;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.OMW;
using Laplace.Decomposers.Tatoeba;
using Laplace.Decomposers.UD;
using Laplace.Decomposers.Wiktionary;
using Laplace.Decomposers.FrameNet;
using Laplace.Decomposers.OpenSubtitles;
using Laplace.Decomposers.VerbNet;
using Laplace.Decomposers.PropBank;
using Laplace.Decomposers.SemLink;
using Laplace.Decomposers.Unicode;
using Laplace.Decomposers.WordNet;
using Laplace.Decomposers.Image;
using Laplace.Decomposers.Audio;
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
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

internal static class FoundryCommands
{
    public static async Task<int> SynthesizeAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (sub == "substrate")
        {
            string? recipeFrom = null, tokenizerDir = null;
            
            
            
            
            int nativeVocab = 0, nativeDim = 0, nativeLayers = 0, nativeHeads = 0, nativeKv = 0, nativeFfn = 0;
            
            
            
            string? crawlSeeds = null; int crawlHops = 3, crawlFanout = 64;
            
            
            
            
            bool grapheme = false;
            var positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--grapheme-floor": grapheme = true; break;
                    case "--recipe-from" when i + 1 < args.Length: recipeFrom = args[++i]; break;
                    case "--tokenizer"   when i + 1 < args.Length: tokenizerDir = args[++i]; break;
                    case "--native-vocab" when i + 1 < args.Length: nativeVocab = int.Parse(args[++i]); break;
                    case "--dim"          when i + 1 < args.Length: nativeDim = int.Parse(args[++i]); break;
                    case "--layers"       when i + 1 < args.Length: nativeLayers = int.Parse(args[++i]); break;
                    case "--heads"        when i + 1 < args.Length: nativeHeads = int.Parse(args[++i]); break;
                    case "--kv-heads"     when i + 1 < args.Length: nativeKv = int.Parse(args[++i]); break;
                    case "--ffn"          when i + 1 < args.Length: nativeFfn = int.Parse(args[++i]); break;
                    case "--crawl"        when i + 1 < args.Length: crawlSeeds = args[++i]; break;
                    case "--hops"         when i + 1 < args.Length: crawlHops = int.Parse(args[++i]); break;
                    case "--fanout"       when i + 1 < args.Length: crawlFanout = int.Parse(args[++i]); break;
                    default: positional.Add(args[i]); break;
                }
            }
            string? outEnv = Environment.GetEnvironmentVariable("LAPLACE_GGUF_OUT");
            string recipePath = (recipeFrom is null && nativeVocab == 0) ? (positional.Count > 0 ? positional[0] : "") : "";
            string outputPath = ((recipeFrom is null && nativeVocab == 0) ? positional.ElementAtOrDefault(1) : positional.ElementAtOrDefault(0))
                ?? (!string.IsNullOrEmpty(outEnv) ? outEnv : "");
            if (string.IsNullOrEmpty(outputPath))
                return Fail("usage: laplace synthesize substrate <recipe.json> <output.gguf>\n"
                          + "   or: laplace synthesize substrate --recipe-from <recipe-id-prefix> --tokenizer <dir> <output.gguf>\n"
                          + "   or: laplace synthesize substrate --native-vocab <N> --dim <D> [--layers L --heads H --kv-heads K --ffn F] <output.gguf>\n"
                          + "  (or set LAPLACE_GGUF_OUT; no temp-dir default)");

            if (nativeVocab > 0)
            {
                if (nativeDim <= 0) return Fail("--native-vocab needs --dim <D> (the hidden size the foundry casts to)");
                var nativeMold = await MaterializeNativeMoldAsync(nativeVocab, nativeDim, nativeLayers, nativeHeads, nativeKv, nativeFfn, crawlSeeds, crawlHops, crawlFanout, grapheme);
                if (nativeMold is null) return 2;
                recipePath = nativeMold;
            }
            else if (recipeFrom is not null)
            {
                if (string.IsNullOrEmpty(tokenizerDir) || !File.Exists(Path.Combine(tokenizerDir, "tokenizer.json")))
                    return Fail("--recipe-from needs --tokenizer <dir> containing tokenizer.json "
                              + "(the vocab — gguf slots mapped onto the substrate's content entities)");
                var molded = await MaterializeDiscoveredMoldAsync(recipeFrom, tokenizerDir);
                if (molded is null) return 2;
                recipePath = molded;
            }
            return await SynthesizeFromSubstrateAsync(recipePath, outputPath, grapheme);
        }

        return Fail(
            "usage: laplace synthesize <subcommand> [args]\n"
            + "  substrate <recipe.json> [output.gguf]                        pour consensus into a recipe-file mold\n"
            + "  substrate --recipe-from <id-prefix> --tokenizer <dir> [out]  pour a mold discovered from a deposed model ('*' = the only one)\n");
    }

    
    
    
    private static async Task<string?> MaterializeDiscoveredMoldAsync(string recipeIdPrefix, string tokenizerDir)
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT recipe_id, recipe_json FROM laplace.model_recipes()";
        var hits = new List<(string Hex, string Json)>();
        await using (var rdr = await cmd.ExecuteReaderAsync())
            while (await rdr.ReadAsync())
            {
                string hex = Convert.ToHexString((byte[])rdr[0]).ToLowerInvariant();
                if (recipeIdPrefix == "*" || hex.StartsWith(recipeIdPrefix.ToLowerInvariant(), StringComparison.Ordinal))
                    hits.Add((hex, rdr.GetString(1)));
            }
        if (hits.Count == 0) { Fail($"no deposed recipe matches '{recipeIdPrefix}' — list them: SELECT * FROM laplace.model_recipes()"); return null; }
        if (hits.Count > 1) { Fail($"recipe prefix '{recipeIdPrefix}' is ambiguous ({hits.Count} matches) — extend the prefix"); return null; }

        string moldDir = Path.Combine(Path.GetTempPath(), $"laplace-foundry-mold-{hits[0].Hex[..12]}");
        Directory.CreateDirectory(moldDir);
        await File.WriteAllTextAsync(Path.Combine(moldDir, "config.json"), hits[0].Json);
        foreach (var f in new[] { "tokenizer.json", "tokenizer.model", "tokenizer_config.json", "generation_config.json" })
        {
            string src = Path.Combine(tokenizerDir, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(moldDir, f), overwrite: true);
        }
        Console.WriteLine($"  discovered mold {hits[0].Hex} → {moldDir}");
        return Path.Combine(moldDir, "config.json");
    }

    
    
    
    
    
    
    
    
    private static async Task<string?> MaterializeNativeMoldAsync(
        int vocabN, int dim, int layers, int heads, int kvHeads, int ffn,
        string? crawlSeeds = null, int crawlHops = 3, int crawlFanout = 64, bool grapheme = false)
    {
        if (dim % 64 != 0 && heads <= 0)
            { Fail($"--dim {dim} is not a multiple of 64 — pass --heads explicitly so dim/heads is the head size"); return null; }
        if (heads   <= 0) heads   = Math.Max(1, dim / 64);
        if (dim % heads != 0)
            { Fail($"--dim {dim} not divisible by --heads {heads} (head size must be integral)"); return null; }
        if (kvHeads <= 0) kvHeads = heads;
        if (kvHeads <= 0 || heads % kvHeads != 0)
            { Fail($"--heads {heads} not divisible by --kv-heads {kvHeads}"); return null; }
        if (layers  <= 0) layers  = 12;
        if (9 * layers + 3 > 300)
            { Fail($"--layers {layers} exceeds the tensor-slot budget (9·L+3 ≤ 300 → L ≤ 33)"); return null; }
        if (ffn     <= 0) ffn = ((8 * dim / 3 + 255) / 256) * 256;   

        CodepointPerfcache.Load(ResolveBlob());   

        
        
        
        
        
        
        
        var sel = new List<(string surface, long weight)>(vocabN);
        string[]? seeds = crawlSeeds
            ?.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] crawlS = [];
        await using (var ds = new NpgsqlDataSourceBuilder(ConnString).Build())
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 0;   
            if (grapheme)
            {
                cmd.CommandText = "SELECT surface, weight FROM laplace.grapheme_floor_vocab($1)";
                cmd.Parameters.AddWithValue(vocabN);
                Console.WriteLine($"  vocab: GRAPHEME FLOOR ({vocabN} codepoint/grapheme atoms — "
                    + "tokenizes any text char-by-char in-engine, no merge path)");
            }
            else
            {
                
                
                
                
                
                
                crawlS = seeds is { Length: > 0 } ? seeds : [];
                if (crawlS.Length == 0)
                {
                    int seedN = Math.Min(vocabN, FoundryExport.EnvInt("LAPLACE_FOUNDRY_CRAWL_SEEDS", 1000));
                    var ss = new List<string>(seedN);
                    await using (var sc = conn.CreateCommand())
                    {
                        sc.CommandTimeout = 0;
                        sc.CommandText = "SELECT surface FROM laplace.corpus_word_vocab($1, $2)";
                        sc.Parameters.AddWithValue(seedN);
                        sc.Parameters.AddWithValue(FoundryExport.EnvInt("LAPLACE_FOUNDRY_WORD_TRAJS", 400000));
                        await using var sr = await sc.ExecuteReaderAsync();
                        while (await sr.ReadAsync()) ss.Add(sr.GetString(0));
                    }
                    crawlS = ss.ToArray();
                    Console.WriteLine($"  vocab: corpus-seeded relation-closed crawl ({crawlS.Length} corpus seeds → {vocabN}, hops {crawlHops}, fanout {crawlFanout})");
                }
                else
                    Console.WriteLine($"  vocab: seeded crawl from [{string.Join(", ", seeds!)}] (hops {crawlHops}, fanout {crawlFanout})");
                cmd.CommandText = "SELECT surface, weight FROM laplace.foundry_vocab_crawl($1, $2, $3, $4)";
                cmd.Parameters.AddWithValue(crawlS);
                cmd.Parameters.AddWithValue(vocabN);
                cmd.Parameters.AddWithValue(crawlHops);
                cmd.Parameters.AddWithValue(crawlFanout);
            }
            await using (var rdr = await cmd.ExecuteReaderAsync())
            {
                while (await rdr.ReadAsync())
                    sel.Add((rdr.GetString(0), rdr.GetInt64(1)));
            }
            
            
            
            if (!grapheme && crawlS.Length > 0)
                await PinCrawlSeedsInPlaceAsync(conn, crawlS, sel, vocabN);
        }
        if (sel.Count == 0)
        {
            Fail(seeds is { Length: > 0 }
                ? "laplace.foundry_vocab_crawl returned nothing — none of the seed words resolve (try other seeds / more hops)"
                : "laplace.foundry_vocab returned nothing — ingest text first");
            return null;
        }

        
        
        
        
        var pieces = new List<(string piece, float score, int type)>(3 + 256 + sel.Count);
        pieces.Add(("<unk>", 0f, 2));
        pieces.Add(("<s>",   0f, 3));
        pieces.Add(("</s>",  0f, 3));
        for (int b = 0; b < 256; b++) pieces.Add(($"<0x{b:X2}>", -20f, 6));
        if (grapheme)
        {
            
            
            
            pieces.Add(("▁", 1f, 1));
            foreach (var (surface, weight) in sel)
                pieces.Add((surface, (float)(Math.Log(weight + 1.0) + 1.0), 1));
        }
        else
        {
            
            
            
            
            
            
            
            
            int aliases = 0;
            foreach (var (surface, weight) in sel)
            {
                float sc = (float)(Math.Log(weight + 1.0) + 1.0);
                pieces.Add(("▁" + surface, sc, 1));
                if (!(surface.Length == 1 && surface[0] < 128)) { pieces.Add((surface, sc, 1)); aliases++; }
            }
            Console.WriteLine($"  dual-form: +{aliases:N0} bare-word aliases (sentence-initial match; input-only)");
        }
        int vocabSize = pieces.Count;
        Console.WriteLine($"  native vocab: {sel.Count:N0} substrate word entities + 256 byte floor + 3 specials = {vocabSize:N0}");

        string moldDir = Path.Combine(Path.GetTempPath(), $"laplace-native-mold-d{dim}-v{vocabSize}");
        Directory.CreateDirectory(moldDir);

        
        
        await using (var fs = File.Create(Path.Combine(moldDir, "tokenizer.json")))
        await using (var w = new System.Text.Json.Utf8JsonWriter(fs))
        {
            w.WriteStartObject();
            w.WriteString("version", "1.0");
            w.WriteStartArray("added_tokens");
            for (int i = 0; i < 3; i++)   
            {
                w.WriteStartObject();
                w.WriteNumber("id", i);
                w.WriteString("content", pieces[i].piece);
                w.WriteBoolean("special", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("model");
            w.WriteString("type", "WordLevel");
            w.WriteString("unk_token", "<unk>");
            w.WriteStartObject("vocab");
            for (int i = 0; i < pieces.Count; i++) w.WriteNumber(pieces[i].piece, i);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        
        
        WriteSentencePieceModel(Path.Combine(moldDir, "tokenizer.model"), pieces);

        
        
        await using (var fs = File.Create(Path.Combine(moldDir, "config.json")))
        await using (var w = new System.Text.Json.Utf8JsonWriter(fs, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("architectures"); w.WriteStringValue("LlamaForCausalLM"); w.WriteEndArray();
            w.WriteString("model_type", "llama");
            w.WriteNumber("hidden_size", dim);
            w.WriteNumber("num_hidden_layers", layers);
            w.WriteNumber("num_attention_heads", heads);
            w.WriteNumber("num_key_value_heads", kvHeads);
            w.WriteNumber("intermediate_size", ffn);
            w.WriteNumber("vocab_size", vocabSize);
            w.WriteNumber("max_position_embeddings", 2048);
            w.WriteNumber("rope_theta", 10000.0);
            w.WriteNumber("rms_norm_eps", 1e-5);
            w.WriteString("hidden_act", "silu");
            w.WriteString("torch_dtype", "float32");
            w.WriteBoolean("tie_word_embeddings", false);
            w.WriteNumber("bos_token_id", 1);
            w.WriteNumber("eos_token_id", 2);
            w.WriteEndObject();
        }

        Console.WriteLine($"  native mold → {moldDir} (vocab {vocabSize:N0}, dim {dim}, layers {layers}, heads {heads}/{kvHeads}, ffn {ffn})");
        return Path.Combine(moldDir, "config.json");
    }

    
    
    private static void WriteSentencePieceModel(string path, IReadOnlyList<(string piece, float score, int type)> pieces)
    {
        static void Varint(System.IO.Stream s, ulong v) { while (v >= 0x80) { s.WriteByte((byte)(v | 0x80UL)); v >>= 7; } s.WriteByte((byte)v); }
        static void Tag(System.IO.Stream s, int field, int wire) => Varint(s, ((ulong)(uint)field << 3) | (uint)wire);
        using var ms = new System.IO.MemoryStream();
        foreach (var (piece, score, type) in pieces)
        {
            using var inner = new System.IO.MemoryStream();
            byte[] pb = Encoding.UTF8.GetBytes(piece);
            Tag(inner, 1, 2); Varint(inner, (ulong)pb.Length); inner.Write(pb);   
            Tag(inner, 2, 5); inner.Write(BitConverter.GetBytes(score));          
            Tag(inner, 3, 0); Varint(inner, (ulong)type);                         
            byte[] ib = inner.ToArray();
            Tag(ms, 1, 2); Varint(ms, (ulong)ib.Length); ms.Write(ib);            
        }
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static async Task<int> SynthesizeFromSubstrateAsync(string recipePath, string outputPath, bool grapheme = false)
    {
        if (string.IsNullOrEmpty(recipePath) || !File.Exists(recipePath))
            return Fail(
                "usage: laplace synthesize substrate <recipe.json> [output.gguf]\n"
                + $"  (recipe not found: {recipePath})");

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
        int intermR  = recipe.IntermediateSize;
        int nLayers  = recipe.NumLayers;

        byte[] configJson = File.ReadAllBytes(recipePath);
        IntPtr recipeHandle, tmplHandle;
        var specs = new TensorSpec[300];
        int tensorCount;
        unsafe
        {
            fixed (byte* jp = configJson) recipeHandle = SynthInterop.RecipeParse(jp, (nuint)configJson.Length);
            if (recipeHandle == IntPtr.Zero) return Fail("recipe_parse returned null");
            tmplHandle = SynthInterop.ArchTemplateLoad("llama");
            if (tmplHandle == IntPtr.Zero) return Fail("arch_template_load returned null");
            fixed (TensorSpec* sp = specs)
                tensorCount = SynthInterop.ArchTemplateRequiredTensors(tmplHandle, recipeHandle, sp, (nuint)specs.Length);
        }
        if (tensorCount <= 0) return Fail($"arch_template_required_tensors returned {tensorCount}");
        Console.WriteLine($"  recipe + arch template: {tensorCount} tensor slots, vocab={vocab}, hidden={dModel}, "
            + $"layers={nLayers}, heads={nHeadsR}/{nKvR}, ffn={intermR}");

        if (RejectRetiredFoundryEnvVars() is { } retired)
            return Fail(retired);

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();

        
        
        
        
        
        int degreeCap = FoundryExport.EnvInt("LAPLACE_FOUNDRY_LE_DEGREE", 48);
        var swPour = Stopwatch.StartNew();
        
        
        
        
        
        
        
        
        
        
        
        Task<FoundryExport.PlaneCoo> LayerAsync(double lo, double hi)
            => FoundryExport.ReadLayerPlaneAsync(ds, lo, hi, tokenSlots, degreeCap);
        var simTask = LayerAsync(0.50, 0.60);   
        var relTask = LayerAsync(0.68, 0.86);   
        var preTask = LayerAsync(0.60, 0.68);   
        var attTask = LayerAsync(0.30, 0.50);   
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

        
        
        
        
        int trajGap = FoundryExport.EnvInt("LAPLACE_FOUNDRY_TRAJ_GAP", Math.Max(2, Math.Min(nLayers, 8)));
        var swTraj = Stopwatch.StartNew();
        var traj = FoundryExport.Normalize(
            await FoundryExport.ReadTrajectoryStrideAsync(ds, trajGap, tokenSlots, degreeCap));
        Console.WriteLine($"  trajectory order ladder read in {swTraj.Elapsed.TotalSeconds:F1}s "
            + $"(gap≤{trajGap}): {traj.Nnz:N0} ordered continuation edges");

        
        
        
        var adjacency = FoundryExport.Normalize(
            await FoundryExport.ReadAdjacencyAsync(ds, tokenSlots, degreeCap));
        Console.WriteLine($"  rank-weighted adjacency read: {adjacency.Nnz:N0} content edges (banked relation_rank)");

        
        
        var anchors = new double[vocab][];
        foreach (var t in tokens)
        {
            if (t.TokenId < 0 || t.TokenId >= vocab || !t.HasContentCoord) continue;
            anchors[t.TokenId] = [t.ContentX, t.ContentY, t.ContentZ, t.ContentM];
        }
        var basisSeed = Hash128.Blake3(recipe.CanonicalJson);

        
        
        
        
        
        
        
        
        string attnMetric = Environment.GetEnvironmentVariable("LAPLACE_FOUNDRY_ATTN_METRIC") ?? "";
        var attnPlane = att;
        if (attnMetric is "frechet" or "hausdorff" or "angular")
        {
            int mK = FoundryExport.EnvInt("LAPLACE_FOUNDRY_METRIC_K", 16);
            int mProbe = FoundryExport.EnvInt("LAPLACE_FOUNDRY_METRIC_PROBE", 64);
            var swMetric = Stopwatch.StartNew();
            attnPlane = FoundryExport.Normalize(
                await FoundryExport.ReadMetricEdgesAsync(ds, tokenSlots, attnMetric, mK, mProbe, degreeCap));
            Console.WriteLine($"  METRIC HEAD ({attnMetric}): {attnPlane.Nnz:N0} trajectory-metric edges "
                + $"in {swMetric.Elapsed.TotalSeconds:F1}s — a head transcribes laplace_{attnMetric}_4d, not a learned pattern");
            
            
            int coordFilled = await FoundryExport.FillCoordAnchorsAsync(ds, tokenSlots, anchors);
            if (Environment.GetEnvironmentVariable("LAPLACE_FOUNDRY_COORD_DIRECT") is null)
                Environment.SetEnvironmentVariable("LAPLACE_FOUNDRY_COORD_DIRECT", "1");
            if (Environment.GetEnvironmentVariable("LAPLACE_FOUNDRY_COORD_SCALE") is null)
                Environment.SetEnvironmentVariable("LAPLACE_FOUNDRY_COORD_SCALE", "20");   
            Console.WriteLine($"  S³ frame: {coordFilled:N0} tokens placed at their native coordinate verbatim (no LE/Procrustes)");
        }
        
        
        
        if (FoundryExport.EnvInt("LAPLACE_FOUNDRY_COORD_ONLY", 0) != 0 && attnMetric == "")
        {
            int coordOnlyFilled = await FoundryExport.FillCoordAnchorsAsync(ds, tokenSlots, anchors);
            Console.WriteLine($"  S³ COORD-ONLY: {coordOnlyFilled:N0} tokens placed at native coordinate (NO Lanczos eigensolve)");
        }
        var swBasis = Stopwatch.StartNew();
        
        
        
        
        double metricBasisGain = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_METRIC_BASIS_GAIN", 4.0);
        var metricForBasis = attnMetric != ""
            ? attnPlane with { Vals = Array.ConvertAll(attnPlane.Vals, v => v * metricBasisGain) }
            : attnPlane;
        var unionGraph = attnMetric != ""
            ? FoundryExport.Union(sim, rel, pre, att, metricForBasis)
            : FoundryExport.Union(sim, rel, pre, att);
        
        
        
        
        string? affRaw = Environment.GetEnvironmentVariable("LAPLACE_FOUNDRY_EMBED_AFFINITY");
        
        
        double[] E;
        FoundryExport.BasisStats basisStats;
        if (FoundryExport.EnvInt("LAPLACE_FOUNDRY_COORD_ONLY", 0) != 0)
        {
            
            
            
            
            double cs = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_COORD_SCALE", 20.0);
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
            bool affBasis = attnMetric == "" && (affRaw == "1" || (affRaw != "0" && vocab <= 3000));
            Console.WriteLine($"  basis path: {(affBasis ? "AFFINITY-SVD (token = SVD of its relational row)" : "Laplacian-eigenmaps")} (vocab {vocab})");
            E = affBasis
                ? FoundryExport.BuildBasisAffinity(vocab, dModel, unionGraph, anchors, basisSeed, out basisStats)
                : FoundryExport.BuildBasis(vocab, dModel, unionGraph, anchors, basisSeed, out basisStats);
        }
        Console.WriteLine($"  basis generated in {swBasis.Elapsed.TotalSeconds:F1}s: "
            + $"spectral K={basisStats.SpectralRank}, {basisStats.ZeroSpectralTokens:N0} tokens off-graph (capacity-only rows), "
            + $"procrustes residual={basisStats.ProcrustesResidual:F4}");
        MirrorDualFormEmbeds(tokens, E, vocab, dModel);

        
        double relTol = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_REL_ERR_TOL", 0.0);
        int kAttn = Math.Min(kvDimR, dModel);
        int kFfn  = Math.Min(intermR, dModel);
        
        
        
        
        
        
        
        
        
        
        
        var completion = FoundryExport.Normalize(FoundryExport.Union(pre, traj));
        var rankPlanes = new[] { attnPlane, rel, completion, sim };
        var rankNames  = new[] { attnMetric != "" ? $"metric:{attnMetric}" : "associative", "taxo+part", "causal+seq", "equivalence" };
        int nOps = rankPlanes.Length;
        var fOvR   = new FoundryExport.Factors[nOps];   
        var fFfnR  = new FoundryExport.Factors[nOps];   
        var fAttnR = new FoundryExport.Factors[nOps];   
        var swOps = Stopwatch.StartNew();
        for (int r = 0; r < nOps; r++)
        {
            var m = FoundryExport.ProjectOperator(E, vocab, dModel, rankPlanes[r]);
            
            
            
            
            
            
            
            
            var mResid = (double[])m.Clone();
            for (int d = 0; d < dModel; d++) mResid[(long)d * dModel + d] -= 1.0;
            fOvR[r]   = FoundryExport.Factor(mResid, dModel, kAttn, relTol, transpose: true);
            fFfnR[r]  = FoundryExport.Factor(mResid, dModel, kFfn,  relTol, transpose: true);
            fAttnR[r] = FoundryExport.Factor(m,      dModel, kAttn, relTol, transpose: false);
        }
        if (attnMetric != "")
            FoundryExport.ReportMetricHeadFidelity(E, vocab, dModel, attnPlane, fAttnR[0], attnMetric);

        
        
        
        
        
        
        
        
        
        
        
        var lmHead = new double[(long)vocab * dModel];
        {
            int dC = dModel - 1;
            var inDeg = new double[vocab];   
            
            
            
            
            
            
            
            
            bool generative = FoundryExport.EnvInt("LAPLACE_FOUNDRY_GENERATIVE", 1) != 0;
            var roPlanes = generative ? new[] { traj }      : new[] { traj, adjacency };
            var roW      = generative ? new[] { 1.0 }       : new[] { 1.0, 1.0 };
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

        
        
        
        
        double attnGainEnv  = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_ATTN_GAIN", 1.0);
        double residGainEnv = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_RESID_GAIN", 1.0);
        
        
        
        double gateZ = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_GATE_Z", 6.0);
        double gateCol = gateZ / Math.Sqrt(dModel / 2.0);
        double upGain = 1.0 / FoundryExport.Silu(gateZ);

        
        
        
        
        
        
        int WriteCast(string outPath, double aGain, double rGain)
        {
            
            
            
            
            
            
            
            
            
            double split = Math.Pow(Math.Max(1, nLayers), -0.25);
            double attnScale  = aGain * split;
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
                    name  = Marshal.PtrToStringUTF8((IntPtr)sp.Name) ?? "";
                    rows  = sp.Rank >= 1 ? sp.Shape[0] : 1;
                    cols  = sp.Rank >= 2 ? sp.Shape[1] : 1;
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
                    if (layerIdx == last)   { aIdx = RANK_COMPLETION; fIdx = RANK_COMPLETION; }
                    else if (layerIdx == 0) { aIdx = RANK_ASSOC;      fIdx = RANK_TAXO; }
                    else                    { aIdx = layerIdx % nOps; fIdx = (layerIdx + 1) % nOps; }
                    var fAttn = fAttnR[aIdx];
                    var fOv   = fOvR[aIdx];
                    var fFfn  = fFfnR[fIdx];
                    
                    
                    
                    bool coordHead = attnMetric != "" && aIdx == 0;
                    double coordHeadScale = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_COORD_HEAD_SCALE", Math.Sqrt(Math.Max(1, headDimR)));
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
                        case "mlp.gate_proj.weight":    FoundryExport.FillGate(vals, tr, tc, gateCol); break;
                        case "mlp.up_proj.weight":      FoundryExport.FillRowsRight(vals, tr, tc, fFfn, layerScale * upGain); break;
                        case "mlp.down_proj.weight":    FoundryExport.FillCols(vals, tr, tc, fFfn, layerScale); break;
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
                    fixed (byte*  dataPtr = tensorBytes)
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

        int status;
        string? sweep = Environment.GetEnvironmentVariable("LAPLACE_FOUNDRY_GAIN_SWEEP");
        if (!string.IsNullOrWhiteSpace(sweep))
        {
            status = 0;
            foreach (var pair in sweep.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var ar = pair.Split(':', StringSplitOptions.TrimEntries);
                double a = double.Parse(ar[0], System.Globalization.CultureInfo.InvariantCulture);
                double r = ar.Length > 1 ? double.Parse(ar[1], System.Globalization.CultureInfo.InvariantCulture) : a;
                string vpath = (outputPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                    ? outputPath[..^5] : outputPath) + $"_{pair.Replace(':', '-')}.gguf";
                int s = WriteCast(vpath, a, r);
                if (s != 0) status = s;
            }
        }
        else
        {
            status = WriteCast(outputPath, attnGainEnv, residGainEnv);
        }

        SynthInterop.ArchTemplateFree(tmplHandle);
        SynthInterop.RecipeFree(recipeHandle);
        return status == 0 ? 0 : Fail($"foundry write failed (status {status})");
    }

    
    
    private static string? RejectRetiredFoundryEnvVars()
    {
        string[] retired =
        [
            "LAPLACE_FOUNDRY_FAITHFUL", "LAPLACE_FOUNDRY_TOPIC", "LAPLACE_FOUNDRY_CONTENT",
            "LAPLACE_FOUNDRY_REPHEAD", "LAPLACE_FOUNDRY_NGRAM", "LAPLACE_FOUNDRY_MULTIHEAD",
        ];
        var set = new List<string>();
        foreach (var name in retired)
        {
            string? v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(v) && v is not "0" and not "false" and not "False")
                set.Add(name);
        }
        if (set.Count == 0) return null;
        return "retired foundry shortcut env vars are set (they bypass the recipe pour): "
            + string.Join(", ", set) + " — unset them and re-run; synthesis always uses the recipe's layers/heads/dim";
    }

    
    
    private static void MirrorDualFormEmbeds(
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens, double[] e, int vocab, int dModel)
    {
        var lead = new Dictionary<Hash128, int>();
        foreach (var t in tokens)
            if (t.TokenId >= 0 && t.TokenId < vocab && t.Role.HasFlag(TokenRole.LeadingSpace))
                lead[t.EntityId] = t.TokenId;
        int mirrored = 0;
        foreach (var t in tokens)
        {
            if (t.TokenId < 0 || t.TokenId >= vocab) continue;
            if (t.IsByteLevel || t.Role.HasFlag(TokenRole.Special) || t.Role.HasFlag(TokenRole.LeadingSpace)) continue;
            if (!lead.TryGetValue(t.EntityId, out int lid)) continue;
            Array.Copy(e, (long)lid * dModel, e, (long)t.TokenId * dModel, dModel);
            mirrored++;
        }
        if (mirrored > 0)
            Console.WriteLine($"  dual-form embed: mirrored {mirrored:N0} bare-alias rows from their space-led partners");
    }

    
    
    
    private static async Task PinCrawlSeedsInPlaceAsync(
        NpgsqlConnection conn, string[] seeds, List<(string surface, long weight)> sel, int budget)
    {
        var bySurf = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (s, w) in sel) bySurf[s] = w;
        long pin = bySurf.Count > 0 ? bySurf.Values.Max() + 1 : 1_000_000;
        int resolved = 0, pinned = 0;
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText =
            "SELECT render_text(word_id(s), 80) FROM unnest($1::text[]) AS s WHERE word_id(s) IS NOT NULL";
        cmd.Parameters.AddWithValue(seeds);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            string s = rdr.GetString(0);
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

    private static string HfToGgmlName(string hf)
    {
        if (hf == "model.embed_tokens.weight") return "token_embd.weight";
        if (hf == "model.norm.weight")         return "output_norm.weight";
        if (hf == "lm_head.weight")            return "output.weight";

        const string prefix = "model.layers.";
        if (hf.StartsWith(prefix, StringComparison.Ordinal))
        {
            int dot = hf.IndexOf('.', prefix.Length);
            if (dot > 0)
            {
                string idx  = hf.Substring(prefix.Length, dot - prefix.Length);
                string rest = hf.Substring(dot + 1);
                string g = rest switch
                {
                    "self_attn.q_proj.weight"          => "attn_q.weight",
                    "self_attn.k_proj.weight"          => "attn_k.weight",
                    "self_attn.v_proj.weight"          => "attn_v.weight",
                    "self_attn.o_proj.weight"          => "attn_output.weight",
                    "mlp.gate_proj.weight"             => "ffn_gate.weight",
                    "mlp.up_proj.weight"               => "ffn_up.weight",
                    "mlp.down_proj.weight"             => "ffn_down.weight",
                    "input_layernorm.weight"           => "attn_norm.weight",
                    "post_attention_layernorm.weight"  => "ffn_norm.weight",
                    _                                  => rest,
                };
                return $"blk.{idx}.{g}";
            }
        }
        return hf;
    }

    
    
    
    private static readonly char[] ByteToUnicode = BuildByteToUnicode();
    private static char[] BuildByteToUnicode()
    {
        var map = new char[256]; var self = new bool[256];
        void mark(int lo, int hi) { for (int b = lo; b <= hi; b++) { map[b] = (char)b; self[b] = true; } }
        mark('!', '~'); mark(0xA1, 0xAC); mark(0xAE, 0xFF);
        int k = 0; for (int b = 0; b < 256; b++) if (!self[b]) map[b] = (char)(256 + k++);
        return map;
    }
    private static string ByteEncode(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s)) sb.Append(ByteToUnicode[b]);
        return sb.ToString();
    }
    private static int ParseByteToken(string p) => Convert.ToInt32(p.Substring(3, 2), 16);   

    private static void WriteGgufMetadata(
        IntPtr gguf,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        string modelDir, bool byteBpe = false)
    {
        SynthInterop.GgufWriterAddMetadataStr(gguf, "general.architecture", "llama");
        SynthInterop.GgufWriterAddMetadataStr(gguf, "general.name", Path.GetFileName(modelDir.TrimEnd('/')));

        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.context_length",          2048);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.embedding_length",         (uint)recipe.HiddenSize);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.block_count",              (uint)recipe.NumLayers);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.feed_forward_length",      (uint)recipe.IntermediateSize);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.attention.head_count",     (uint)recipe.NumHeads);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.attention.head_count_kv",  (uint)recipe.NumKvHeads);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.vocab_size",               (uint)recipe.VocabSize);
        SynthInterop.GgufWriterAddMetadataF32(gguf, "llama.attention.layer_norm_rms_epsilon", (float)recipe.RmsNormEps);
        SynthInterop.GgufWriterAddMetadataF32(gguf, "llama.rope.freq_base",           (float)recipe.RopeTheta);

        
        uint bosId = 1, eosId = 2;
        string genCfgPath = Path.Combine(modelDir, "generation_config.json");
        if (File.Exists(genCfgPath))
        {
            using var gen = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(genCfgPath));
            if (gen.RootElement.TryGetProperty("bos_token_id", out var bos)
                && bos.ValueKind == System.Text.Json.JsonValueKind.Number)
                bosId = bos.GetUInt32();
            if (gen.RootElement.TryGetProperty("eos_token_id", out var eos)
                && eos.ValueKind == System.Text.Json.JsonValueKind.Number)
                eosId = eos.GetUInt32();
        }

        if (byteBpe)
        {
            
            
            
            
            SynthInterop.GgufWriterAddMetadataStr(gguf, "tokenizer.ggml.model", "gpt2");
            SynthInterop.GgufWriterAddMetadataStr(gguf, "tokenizer.ggml.pre",   "llama3");
        }
        else
            SynthInterop.GgufWriterAddMetadataStr(gguf, "tokenizer.ggml.model", "llama");
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.bos_token_id",     bosId);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.eos_token_id",     eosId);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.unknown_token_id", 0);
        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_bos_token",    1);
        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_eos_token",    0);
        
        
        
        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_space_prefix", 1);

        int n = tokens.Count;

        string spPath = Path.Combine(modelDir, "tokenizer.model");
        SpPiece[]? sp = File.Exists(spPath) ? ParseSentencePieceModel(spPath) : null;

        string[] pieces = new string[n];
        float[]  scores = new float[n];
        int[]    types  = new int[n];

        if (sp is not null && sp.Length == n)
        {
            for (int i = 0; i < n; i++) { pieces[i] = sp[i].Piece; scores[i] = sp[i].Score; types[i] = sp[i].Type; }
        }
        else
        {
            Console.WriteLine($"  WARN: tokenizer.model {(sp is null ? "missing" : $"has {sp.Length} pieces ≠ vocab {n}")} — "
                + "falling back to tokenizer.json strings + zero scores (tokenization will be degraded)");
            var sorted = tokens.OrderBy(t => t.TokenId).ToArray();
            for (int i = 0; i < n; i++) { pieces[i] = sorted[i].RawToken; scores[i] = 0f; types[i] = ClassifyTokenType(sorted[i].RawToken); }
        }

        if (byteBpe)
        {
            
            
            
            
            for (int i = 0; i < n; i++)
            {
                if (types[i] == 6) { pieces[i] = ByteToUnicode[ParseByteToken(pieces[i])].ToString(); types[i] = 1; }
                else if (types[i] == 1 && pieces[i].StartsWith("▁", StringComparison.Ordinal)) pieces[i] = ByteEncode(" " + pieces[i][1..]);   
                else if (types[i] == 1) pieces[i] = ByteEncode(pieces[i]);
                else types[i] = 3;   
                scores[i] = 0f;
            }
        }

        byte[] packed = PackStrings(pieces);
        unsafe
        {
            fixed (byte* p = packed)
                SynthInterop.GgufWriterAddMetadataStrArrayPacked(
                    gguf, "tokenizer.ggml.tokens", p, (nuint)packed.Length, (nuint)n);
            fixed (float* p = scores)
                SynthInterop.GgufWriterAddMetadataF32Array(gguf, "tokenizer.ggml.scores", p, (nuint)n);
            fixed (int* p = types)
                SynthInterop.GgufWriterAddMetadataI32Array(gguf, "tokenizer.ggml.token_type", p, (nuint)n);
        }
        if (byteBpe)
        {
            
            
            
            var mseen = new HashSet<string>(); var mlist = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if (types[i] != 1) continue;
                string pc = pieces[i];
                for (int k = 0; k + 1 < pc.Length; k++)
                {
                    string m = pc[k] + " " + pc[k + 1];
                    if (mseen.Add(m)) mlist.Add(m);
                }
            }
            byte[] mp = PackStrings(mlist.ToArray());
            unsafe { fixed (byte* p = mp) SynthInterop.GgufWriterAddMetadataStrArrayPacked(gguf, "tokenizer.ggml.merges", p, (nuint)mp.Length, (nuint)mlist.Count); }
            Console.WriteLine($"  byte-level BPE: {mlist.Count:N0} merges (ignore_merges: words tokenize direct)");
        }

        string cfgPath = Path.Combine(modelDir, "tokenizer_config.json");
        if (File.Exists(cfgPath))
        {
            using var cfg = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(cfgPath));
            if (cfg.RootElement.TryGetProperty("chat_template", out var ct)
                && ct.ValueKind == System.Text.Json.JsonValueKind.String)
                SynthInterop.GgufWriterAddMetadataStr(gguf, "tokenizer.chat_template", ct.GetString()!);
        }
    }

    private sealed record SpPiece(string Piece, float Score, int Type);

    private static SpPiece[] ParseSentencePieceModel(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        var pieces = new List<SpPiece>(32000);
        int pos = 0;
        while (pos < d.Length)
        {
            ulong key = ReadVarint(d, ref pos);
            int field = (int)(key >> 3), wt = (int)(key & 7);
            if (field == 1 && wt == 2)
            {
                int len = (int)ReadVarint(d, ref pos);
                int end = pos + len;
                string piece = ""; float score = 0f; int type = 1;
                while (pos < end)
                {
                    ulong k2 = ReadVarint(d, ref pos);
                    int f2 = (int)(k2 >> 3), w2 = (int)(k2 & 7);
                    if      (f2 == 1 && w2 == 2) { int l = (int)ReadVarint(d, ref pos); piece = Encoding.UTF8.GetString(d, pos, l); pos += l; }
                    else if (f2 == 2 && w2 == 5) { score = BitConverter.ToSingle(d, pos); pos += 4; }
                    else if (f2 == 3 && w2 == 0) { type = (int)ReadVarint(d, ref pos); }
                    else SkipField(d, ref pos, w2);
                }
                pieces.Add(new SpPiece(piece, score, type));
                pos = end;
            }
            else SkipField(d, ref pos, wt);
        }
        return pieces.ToArray();
    }

    private static ulong ReadVarint(byte[] d, ref int pos)
    {
        ulong v = 0; int shift = 0;
        while (pos < d.Length)
        {
            byte b = d[pos++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return v;
    }

    private static void SkipField(byte[] d, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(d, ref pos); break;
            case 1: pos += 8; break;
            case 2: { int l = (int)ReadVarint(d, ref pos); pos += l; break; }
            case 5: pos += 4; break;
            default: throw new InvalidDataException($"SP proto: unsupported wire type {wireType}");
        }
    }

    private static byte[] PackStrings(IReadOnlyList<string> strings)
    {
        using var ms = new System.IO.MemoryStream();
        Span<byte> lenBuf = stackalloc byte[8];
        foreach (var s in strings)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, (ulong)b.Length);
            ms.Write(lenBuf);
            ms.Write(b);
        }
        return ms.ToArray();
    }

    private static int ClassifyTokenType(string raw)
    {
        if (raw is "<unk>" or "<UNK>" or "<unknown>") return 1;
        if (raw is "<s>" or "</s>" or "<pad>" or "<bos>" or "<eos>") return 2;
        if (raw.Length == 6 && raw.StartsWith("<0x", StringComparison.Ordinal) && raw.EndsWith('>')) return 5;
        return 0;
    }
}
