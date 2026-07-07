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
                    case "--tokenizer" when i + 1 < args.Length: tokenizerDir = args[++i]; break;
                    case "--native-vocab" when i + 1 < args.Length: nativeVocab = int.Parse(args[++i]); break;
                    case "--dim" when i + 1 < args.Length: nativeDim = int.Parse(args[++i]); break;
                    case "--layers" when i + 1 < args.Length: nativeLayers = int.Parse(args[++i]); break;
                    case "--heads" when i + 1 < args.Length: nativeHeads = int.Parse(args[++i]); break;
                    case "--kv-heads" when i + 1 < args.Length: nativeKv = int.Parse(args[++i]); break;
                    case "--ffn" when i + 1 < args.Length: nativeFfn = int.Parse(args[++i]); break;
                    case "--crawl" when i + 1 < args.Length: crawlSeeds = args[++i]; break;
                    case "--hops" when i + 1 < args.Length: crawlHops = int.Parse(args[++i]); break;
                    case "--fanout" when i + 1 < args.Length: crawlFanout = int.Parse(args[++i]); break;
                    default: positional.Add(args[i]); break;
                }
            }
            string recipePath = (recipeFrom is null && nativeVocab == 0) ? (positional.Count > 0 ? positional[0] : "") : "";
            string outputPath = ((recipeFrom is null && nativeVocab == 0) ? positional.ElementAtOrDefault(1) : positional.ElementAtOrDefault(0))
                ?? Path.Combine(LaplaceInstall.ResolveGgufOutputDir(), "laplace-foundry.gguf");
            if (string.IsNullOrEmpty(outputPath))
                return Fail("usage: laplace synthesize substrate <recipe.json> <output.gguf>\n"
                          + "   or: laplace synthesize substrate --recipe-from <recipe-id-prefix> --tokenizer <dir> <output.gguf>\n"
                          + "   or: laplace synthesize substrate --native-vocab <N> --dim <D> [--layers L --heads H --kv-heads K --ffn F] <output.gguf>");

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
        if (heads <= 0) heads = Math.Max(1, dim / 64);
        if (dim % heads != 0)
        { Fail($"--dim {dim} not divisible by --heads {heads} (head size must be integral)"); return null; }
        if (kvHeads <= 0) kvHeads = heads;
        if (kvHeads <= 0 || heads % kvHeads != 0)
        { Fail($"--heads {heads} not divisible by --kv-heads {kvHeads}"); return null; }
        if (layers <= 0) layers = 12;
        if (9 * layers + 3 > 300)
        { Fail($"--layers {layers} exceeds the tensor-slot budget (9·L+3 ≤ 300 → L ≤ 33)"); return null; }
        if (ffn <= 0) ffn = ((8 * dim / 3 + 255) / 256) * 256;

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
                    int seedN = Math.Min(vocabN, FoundryDefaults.CrawlSeeds);
                    var ss = new List<string>(seedN);
                    await using (var sc = conn.CreateCommand())
                    {
                        sc.CommandTimeout = 0;
                        sc.CommandText = "SELECT surface FROM laplace.corpus_word_vocab($1, $2)";
                        sc.Parameters.AddWithValue(seedN);
                        sc.Parameters.AddWithValue(FoundryDefaults.WordTrajs);
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

        if (!grapheme)
        {
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            var (bpeM, bpeV) = TrainByteBpe(sel, vocabN, 3 + 256);
            sw0.Stop();
            Console.WriteLine($"  [BPE-TEST] {sel.Count:N0} words -> {bpeM.Count:N0} merges, {bpeV.Count:N0} learned pieces in {sw0.ElapsedMilliseconds}ms");
            Console.Write("  [BPE-TEST] first 30 merges: ");
            foreach (var m in bpeM.Take(30)) Console.Write($"({m}) ");
            Console.WriteLine();
            Console.Write("  [BPE-TEST] sample learned pieces: ");
            foreach (var (p, f) in bpeV.OrderByDescending(x => x.freq).Take(20)) Console.Write($"{p}={f} ");
            Console.WriteLine();
        }





        var pieces = new List<(string piece, float score, int type)>(3 + 256 + sel.Count);
        pieces.Add(("<unk>", 0f, 2));
        pieces.Add(("<s>", 0f, 3));
        pieces.Add(("</s>", 0f, 3));
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







    private static (List<string> merges, List<(string piece, long freq)> learned) TrainByteBpe(
        IReadOnlyList<(string surface, long weight)> words, int targetVocab, int reserved)
    {
        var wordSyms = new List<(List<string> syms, long freq)>(words.Count);
        var baseChars = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (surface, weight) in words)
        {
            if (string.IsNullOrEmpty(surface)) continue;
            string enc = ByteEncode(" " + surface);
            var syms = new List<string>(enc.Length);
            foreach (char c in enc) { string s = c.ToString(); syms.Add(s); baseChars.Add(s); }
            wordSyms.Add((syms, Math.Max(weight, 1)));
        }

        int budget = targetVocab - reserved - baseChars.Count;
        var merges = new List<string>(Math.Max(budget, 0));
        var learned = new List<(string piece, long freq)>(Math.Max(budget, 0));
        var pairCounts = new Dictionary<(string, string), long>(1 << 16);

        while (merges.Count < budget)
        {
            pairCounts.Clear();
            foreach (var (syms, freq) in wordSyms)
                for (int i = 0; i + 1 < syms.Count; i++)
                {
                    var key = (syms[i], syms[i + 1]);
                    pairCounts.TryGetValue(key, out long c); pairCounts[key] = c + freq;
                }
            if (pairCounts.Count == 0) break;

            (string, string) best = default; long bestC = -1;
            foreach (var kv in pairCounts) if (kv.Value > bestC) { bestC = kv.Value; best = kv.Key; }
            if (bestC <= 0) break;

            string merged = best.Item1 + best.Item2;
            merges.Add(best.Item1 + " " + best.Item2);
            learned.Add((merged, bestC));

            foreach (var (syms, _) in wordSyms)
                for (int i = 0; i + 1 < syms.Count; i++)
                    if (syms[i] == best.Item1 && syms[i + 1] == best.Item2)
                    { syms[i] = merged; syms.RemoveAt(i + 1); }
        }
        return (merges, learned);
    }

    private static async Task<int> SynthesizeFromSubstrateAsync(string recipePath, string outputPath, bool grapheme = false)
    {
        if (string.IsNullOrEmpty(recipePath) || !File.Exists(recipePath))
            return Fail(
                "usage: laplace synthesize substrate <recipe.json> [output.gguf]\n"
                + $"  (recipe not found: {recipePath})");



        string recipeText = await File.ReadAllTextAsync(recipePath);
        if (recipeText.Contains("\"laplace.recipe\"", StringComparison.Ordinal))
        {
            var moldDesc = RecipeDescriptor.Parse(recipeText);
            string moldDir = Path.GetDirectoryName(recipePath) ?? ".";
            return await SynthesizeMoldAModelAsync(moldDesc, moldDir, outputPath);
        }

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
        Console.WriteLine($"  rank-weighted adjacency read: {adjacency.Nnz:N0} content edges (banked relation_rank)");



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
        var unionGraph = attnMetric != ""
            ? FoundryExport.Union(sim, rel, pre, att, metricForBasis)
            : FoundryExport.Union(sim, rel, pre, att);




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
            var m = FoundryExport.ProjectOperator(E, vocab, dModel, rankPlanes[r]);








            var mResid = (double[])m.Clone();
            for (int d = 0; d < dModel; d++) mResid[(long)d * dModel + d] -= 1.0;
            fOvR[r] = FoundryExport.Factor(mResid, dModel, kAttn, relTol, transpose: true);
            fFfnR[r] = FoundryExport.Factor(mResid, dModel, kFfn, relTol, transpose: true);
            fAttnR[r] = FoundryExport.Factor(m, dModel, kAttn, relTol, transpose: false);
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

        SynthInterop.ArchTemplateFree(tmplHandle);
        SynthInterop.RecipeFree(recipeHandle);
        return status == 0 ? 0 : Fail($"foundry write failed (status {status})");
    }





    private static async Task<int> SynthesizeMoldAModelAsync(
        RecipeDescriptor desc, string modelDir, string outputPath)
    {
        Console.WriteLine($"synthesize Mold-A-Model: {desc.Name} ({desc.Structure}) → {outputPath}");
        CodepointPerfcache.Load(ResolveBlob());

        if (desc.HiddenSizeAuto)
            return Fail("hidden_size 'auto' (spectral rank) not yet wired for Mold-A-Model — set an integer for the spine");
        if (desc.Structure != "dense")
            return Fail($"Mold-A-Model spine supports 'dense' (got '{desc.Structure}'); MoE is Milestone B");

        int dModel = desc.HiddenSizeOr(0);
        int nLayers = desc.NumLayers;
        int intermR = desc.IntermediateSize;
        int nHeads = desc.Layers[0].Heads.Count;
        int nKv = desc.Layers[0].KvHeads;
        foreach (var L in desc.Layers)
            if (L.Heads.Count != nHeads || L.KvHeads != nKv)
                return Fail("Mold-A-Model spine requires uniform heads/kv per layer (variable-per-layer is Milestone B)");
        if (dModel <= 0 || dModel % nHeads != 0)
            return Fail($"hidden_size {dModel} must be a positive multiple of heads/layer {nHeads}");
        if (nKv != nHeads)
            return Fail($"Mold-A-Model spine requires MHA (kv_heads {nKv} == heads {nHeads}); GQA is Milestone B");
        int headDim = dModel / nHeads;




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
            string? moldCfg = await MaterializeNativeMoldAsync(
                desc.Vocab.Size > 0 ? desc.Vocab.Size : 2000,
                dModel, nLayers, nHeads, nKv, intermR,
                crawlSeeds: desc.Vocab.Seeds.Count > 0 ? string.Join(",", desc.Vocab.Seeds) : null,
                crawlHops: desc.Vocab.Hops, crawlFanout: desc.Vocab.Fanout,
                grapheme: desc.Vocab.Source == "grapheme");
            if (moldCfg is null) return Fail("substrate vocab generation failed");
            tokenizerPath = Path.Combine(Path.GetDirectoryName(moldCfg)!, "tokenizer.json");
        }
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


        byte[] configJson = BuildHfConfigJson(dModel, nLayers, nHeads, nKv, intermR, vocab);
        string bridgePath = Path.Combine(Path.GetTempPath(),
            $"laplace-bab-{Convert.ToHexString(desc.RecipeId.ToBytes())[..12]}-config.json");
        await File.WriteAllBytesAsync(bridgePath, configJson);
        var recipe = LlamaRecipeExtractor.Parse(bridgePath);

        IntPtr recipeHandle, tmplHandle;
        var specs = new TensorSpec[300];
        int tensorCount;
        unsafe
        {
            fixed (byte* jp = configJson) recipeHandle = SynthInterop.RecipeParse(jp, (nuint)configJson.Length);
            if (recipeHandle == IntPtr.Zero) return Fail("recipe_parse(bridge) returned null");
            tmplHandle = SynthInterop.ArchTemplateLoad("llama");
            if (tmplHandle == IntPtr.Zero) return Fail("arch_template_load returned null");
            fixed (TensorSpec* sp = specs)
                tensorCount = SynthInterop.ArchTemplateRequiredTensors(tmplHandle, recipeHandle, sp, (nuint)specs.Length);
        }
        if (tensorCount <= 0) return Fail($"required_tensors returned {tensorCount}");
        Console.WriteLine($"  dims: vocab={vocab} hidden={dModel} layers={nLayers} heads={nHeads} headDim={headDim} ffn={intermR} | {tensorCount} tensors");

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        int degreeCap = FoundryDefaults.LeDegree;


        var opKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var L in desc.Layers) { foreach (var h in L.Heads) opKeys.Add(h.Key); opKeys.Add(L.Ffn.Key); }
        opKeys.Add(desc.LmHead.Key);



        var neededTypes = opKeys
            .Where(k => k.StartsWith("relation:", StringComparison.Ordinal))
            .Select(k => RelationTypeRegistry.RelationTypeId(k["relation:".Length..]))
            .ToList();
        var swRead = Stopwatch.StartNew();
        var typePlanes = await FoundryExport.ReadTypePlanesAsync(ds, tokenSlots, degreeCap, neededTypes);
        var planeByType = new Dictionary<Hash128, FoundryExport.PlaneCoo>();
        foreach (var tp in typePlanes) planeByType[tp.TypeId] = FoundryExport.Normalize(tp.Plane);

        var planeByOp = new Dictionary<string, FoundryExport.PlaneCoo>(StringComparer.Ordinal);
        var trajPlane = FoundryExport.PlaneCoo.Empty;
        foreach (var opKey in opKeys)
        {
            FoundryExport.PlaneCoo plane;
            if (opKey.StartsWith("relation:", StringComparison.Ordinal))
            {
                var tid = RelationTypeRegistry.RelationTypeId(opKey["relation:".Length..]);
                plane = planeByType.TryGetValue(tid, out var p) ? p : FoundryExport.PlaneCoo.Empty;
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
            else
                plane = FoundryExport.PlaneCoo.Empty;
            planeByOp[opKey] = plane;
            Console.WriteLine($"  operator {opKey}: {plane.Nnz:N0} edges");
        }
        Console.WriteLine($"  operator planes read in {swRead.Elapsed.TotalSeconds:F1}s");


        var anchors = new double[vocab][];
        foreach (var t in tokens)
        {
            if (t.TokenId < 0 || t.TokenId >= vocab || !t.HasContentCoord) continue;
            anchors[t.TokenId] = [t.ContentX, t.ContentY, t.ContentZ, t.ContentM];
        }
        var unionGraph = FoundryExport.Union(planeByOp.Values.ToArray());
        if (unionGraph.Nnz == 0) return Fail("no consensus over this vocab for any recipe operator — ingest content first");
        double[] E = FoundryExport.BuildBasis(vocab, dModel, unionGraph, anchors, desc.RecipeId, out var basisStats);
        MirrorDualFormEmbeds(tokens, E, vocab, dModel);
        Console.WriteLine($"  embed basis: spectral K={basisStats.SpectralRank}, {basisStats.ZeroSpectralTokens:N0} off-graph");







        double relTol = FoundryDefaults.RelErrTol;
        int kFfn = Math.Min(intermR, dModel);
        var emptyF = new FoundryExport.Factors(Array.Empty<float>(), Array.Empty<float>(), 0, dModel, 0, 1);
        double split = Math.Pow(Math.Max(1, nLayers), -0.25);
        double attnScale = FoundryDefaults.AttnGain * split;
        double layerScale = FoundryDefaults.ResidGain * split;
        bool contCompile = desc.ContinuationCompile;
        double ctxQk = FoundryDefaults.CtxQk * split;
        double OpAttnScale(string key) => (contCompile && !FoundryExport.IsContinuationOperator(key)) ? 0.0 : attnScale;
        double OpResidScale(string key) => (contCompile && !FoundryExport.IsContinuationOperator(key)) ? 0.0 : layerScale;

        var R = (double[])E.Clone();
        var fAttnL = new List<Dictionary<string, FoundryExport.Factors>>(nLayers);
        var fOvL = new List<Dictionary<string, FoundryExport.Factors>>(nLayers);
        var fFfnL = new List<Dictionary<string, FoundryExport.Factors>>(nLayers);
        const double normEps = 1e-6;
        for (int li = 0; li < nLayers; li++)
        {
            var lyr = desc.Layers[li];
            var fa = new Dictionary<string, FoundryExport.Factors>(StringComparer.Ordinal);
            var fo = new Dictionary<string, FoundryExport.Factors>(StringComparer.Ordinal);
            var ff = new Dictionary<string, FoundryExport.Factors>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in lyr.Heads) if (h.Key != "context") keys.Add(h.Key);
            keys.Add(lyr.Ffn.Key);

            var update = new double[(long)vocab * dModel];
            foreach (var opKey in keys)
            {
                if (!planeByOp.TryGetValue(opKey, out var plane) || plane.Nnz == 0)
                { fa[opKey] = emptyF; fo[opKey] = emptyF; ff[opKey] = emptyF; continue; }
                var m = FoundryExport.ProjectOperator(R, vocab, dModel, plane);
                var mResid = (double[])m.Clone();
                for (int d = 0; d < dModel; d++) mResid[(long)d * dModel + d] -= 1.0;
                fa[opKey] = FoundryExport.Factor(m, dModel, headDim, relTol, transpose: false);
                fo[opKey] = FoundryExport.Factor(mResid, dModel, headDim, relTol, transpose: true);
                ff[opKey] = FoundryExport.Factor(mResid, dModel, kFfn, relTol, transpose: true);

                double sc = OpResidScale(opKey);




                if (sc > 0)
                    System.Threading.Tasks.Parallel.For(0, vocab, t =>
                    {
                        long to = (long)t * dModel;
                        for (int i = 0; i < dModel; i++)
                        {
                            double acc = 0; long mi = (long)i * dModel;
                            for (int j = 0; j < dModel; j++) acc += mResid[mi + j] * R[to + j];
                            update[to + i] += sc * acc;
                        }
                    });
            }
            // Context head: trajectory plane for prefix/sequence mixing (doc 14 P7) when
            // available; identity QK/V remains the fallback when no trajectory edges exist.
            if (trajPlane.Nnz > 0)
            {
                var ctxM = FoundryExport.ProjectOperator(R, vocab, dModel, trajPlane);
                var ctxResid = (double[])ctxM.Clone();
                for (int d = 0; d < dModel; d++) ctxResid[(long)d * dModel + d] -= 1.0;
                fa["context"] = FoundryExport.Factor(ctxM, dModel, headDim, relTol, transpose: false);
                fo["context"] = FoundryExport.Factor(ctxResid, dModel, headDim, relTol, transpose: true);
            }

            fAttnL.Add(fa); fOvL.Add(fo); fFfnL.Add(ff);


            for (int t = 0; t < vocab; t++)
            {
                long to = (long)t * dModel;
                double ss = 0;
                for (int i = 0; i < dModel; i++) { double v = R[to + i] + update[to + i]; R[to + i] = v; ss += v * v; }
                double inv = 1.0 / Math.Sqrt(ss / dModel + normEps);
                for (int i = 0; i < dModel; i++) R[to + i] *= inv;
            }
        }


        var lmHead = new double[(long)vocab * dModel];
        {
            var pl = desc.LmHead.Key == "trajectory" ? trajPlane
                   : planeByOp.TryGetValue(desc.LmHead.Key, out var lp) ? lp : FoundryExport.PlaneCoo.Empty;
            int dC = dModel - 1;
            var inDeg = new double[vocab];
            for (long e2 = 0; e2 < pl.Nnz; e2++)
            {
                int x = pl.Rows[e2], y = pl.Cols[e2];
                if (x < 0 || x >= vocab || y < 0 || y >= vocab) continue;
                double w = pl.Vals[e2];
                long yo = (long)y * dModel, xo = (long)x * dModel;


                for (int c = 0; c < dC; c++) lmHead[yo + c] += w * R[xo + c];
                inDeg[y] += Math.Abs(w);
            }
            for (int v = 0; v < vocab; v++)
            {
                long off = (long)v * dModel;
                double idf = 1.0 / (inDeg[v] + 1.0);
                for (int c = 0; c < dC; c++) lmHead[off + c] *= idf;
                lmHead[off + dC] = 0.0;
            }



            int suppressed = 0;
            foreach (var t in tokens)
            {
                if (t.TokenId < 0 || t.TokenId >= vocab) continue;
                if (!(t.IsByteLevel || !t.Role.HasFlag(TokenRole.LeadingSpace))) continue;
                long o = (long)t.TokenId * dModel;
                for (int c = 0; c < dModel; c++) lmHead[o + c] = 0.0;
                suppressed++;
            }
            Console.WriteLine($"  lm_head: suppressed {suppressed:N0} byte + bare-alias rows (space-led word targets only)");





            for (int v = 0; v < vocab; v++)
            {
                long off = (long)v * dModel;
                double n2 = 0;
                for (int c = 0; c < dC; c++) { double t = lmHead[off + c]; n2 += t * t; }
                if (n2 <= 1e-24) continue;
                double inv = 1.0 / Math.Sqrt(n2);
                for (int c = 0; c < dC; c++) lmHead[off + c] *= inv;
            }
        }



        double gateZ = FoundryDefaults.GateZ;
        double gateCol = gateZ / Math.Sqrt(dModel / 2.0);
        double upGain = 1.0 / FoundryExport.Silu(gateZ);

        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero) return Fail($"gguf_writer_create failed for {outputPath}");


        WriteGgufMetadata(gguf, recipe, tokens, Path.GetDirectoryName(tokenizerPath) ?? modelDir, byteBpe: true);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < tensorCount; i++)
        {
            string name; ulong rows, cols;
            unsafe
            {
                var sp = specs[i];
                name = Marshal.PtrToStringUTF8((IntPtr)sp.Name) ?? "";
                rows = sp.Rank >= 1 ? sp.Shape[0] : 1;
                cols = sp.Rank >= 2 ? sp.Shape[1] : 1;
            }
            long nElem = (long)rows * (long)Math.Max(1UL, cols);
            var vals = new float[nElem];
            int tr = (int)rows, tc = (int)Math.Max(1UL, cols);

            if (name is "model.embed_tokens.weight" or "lm_head.weight")
            {
                var srcE = name == "lm_head.weight" ? lmHead : E;
                for (int r = 0; r < tr; r++)
                    for (int c = 0; c < tc; c++)
                        vals[(long)r * tc + c] = (float)srcE[(long)r * dModel + c];
            }
            else if (name == "model.norm.weight"
                     || name.EndsWith("input_layernorm.weight", StringComparison.Ordinal)
                     || name.EndsWith("post_attention_layernorm.weight", StringComparison.Ordinal))
                Array.Fill(vals, 1.0f);
            else if (name.StartsWith("model.layers.", StringComparison.Ordinal))
            {
                int layerDot = name.IndexOf('.', "model.layers.".Length);
                int layerIdx = int.Parse(name["model.layers.".Length..layerDot]);
                string rest = name[(layerDot + 1)..];
                var layer = desc.Layers[layerIdx];
                switch (rest)
                {
                    case "self_attn.q_proj.weight":
                        for (int h = 0; h < nHeads; h++)
                        {
                            var key = layer.Heads[h].Key;
                            if (key == "context" && fAttnL[layerIdx].TryGetValue("context", out var ctxQ) && ctxQ.Rank > 0)
                                FoundryExport.FillHead(vals, tr, tc, h, headDim, ctxQ, ctxQk);
                            else if (key == "context")
                                FoundryExport.FillHeadIdentityScaled(vals, tr, tc, h, headDim, (float)ctxQk);
                            else { double s = OpAttnScale(key); if (s > 0) FoundryExport.FillHead(vals, tr, tc, h, headDim, fAttnL[layerIdx][key], s); }
                        }
                        break;
                    case "self_attn.k_proj.weight":
                        for (int h = 0; h < nKv; h++)
                        {
                            var key = layer.Heads[h].Key;
                            if (key == "context" && fAttnL[layerIdx].TryGetValue("context", out var ctxK) && ctxK.Rank > 0)
                                FoundryExport.FillHeadRight(vals, tr, tc, h, headDim, ctxK, ctxQk);
                            else if (key == "context")
                                FoundryExport.FillHeadIdentityScaled(vals, tr, tc, h, headDim, (float)ctxQk);
                            else { double s = OpAttnScale(key); if (s > 0) FoundryExport.FillHeadRight(vals, tr, tc, h, headDim, fAttnL[layerIdx][key], s); }
                        }
                        break;
                    case "self_attn.v_proj.weight":
                        for (int h = 0; h < nKv; h++)
                        {
                            var key = layer.Heads[h].Key;
                            if (key == "context" && fOvL[layerIdx].TryGetValue("context", out var ctxV) && ctxV.Rank > 0)
                                FoundryExport.FillHeadRight(vals, tr, tc, h, headDim, ctxV, layerScale);
                            else if (key == "context")
                                FoundryExport.FillHeadIdentity(vals, tr, tc, h, headDim);
                            else { double s = OpResidScale(key); if (s > 0) FoundryExport.FillHeadRight(vals, tr, tc, h, headDim, fOvL[layerIdx][key], s); }
                        }
                        break;
                    case "self_attn.o_proj.weight":
                        for (int h = 0; h < nHeads; h++)
                        {
                            var key = layer.Heads[h].Key;
                            if (key == "context" && fOvL[layerIdx].TryGetValue("context", out var ctxO) && ctxO.Rank > 0)
                                FoundryExport.FillColsHead(vals, tr, tc, h, headDim, ctxO, layerScale);
                            else if (key == "context")
                                FoundryExport.FillColsHeadIdentity(vals, tr, tc, h, headDim);
                            else { double s = OpResidScale(key); if (s > 0) FoundryExport.FillColsHead(vals, tr, tc, h, headDim, fOvL[layerIdx][key], s); }
                        }
                        break;
                    case "mlp.gate_proj.weight": if (OpResidScale(layer.Ffn.Key) > 0) FoundryExport.FillGate(vals, tr, tc, gateCol); break;
                    case "mlp.up_proj.weight": { double s = OpResidScale(layer.Ffn.Key); if (s > 0) FoundryExport.FillRowsRight(vals, tr, tc, fFfnL[layerIdx][layer.Ffn.Key], s * upGain); } break;
                    case "mlp.down_proj.weight": { double s = OpResidScale(layer.Ffn.Key); if (s > 0) FoundryExport.FillCols(vals, tr, tc, fFfnL[layerIdx][layer.Ffn.Key], s); } break;
                    default: SynthInterop.GgufWriterFree(gguf); return Fail($"Mold-A-Model: undefined tensor '{name}'");
                }
            }
            else { SynthInterop.GgufWriterFree(gguf); return Fail($"Mold-A-Model: undefined tensor '{name}'"); }

            byte[] tensorBytes = FoundryExport.ToF32Bytes(vals);
            nuint[] ggufDims = cols > 1 ? [(nuint)cols, (nuint)rows] : [(nuint)rows];
            unsafe
            {
                fixed (nuint* dimsPtr = ggufDims)
                fixed (byte* dataPtr = tensorBytes)
                    SynthInterop.GgufWriterAddTensor(gguf, HfToGgmlName(name), 0, dimsPtr, (nuint)ggufDims.Length, dataPtr);
            }
        }
        int rcw = SynthInterop.GgufWriterFinalize(gguf);
        SynthInterop.GgufWriterFree(gguf);
        SynthInterop.ArchTemplateFree(tmplHandle);
        SynthInterop.RecipeFree(recipeHandle);
        if (rcw != 0) return Fail($"gguf_writer_finalize failed (rc={rcw}) for {outputPath}");
        long fsz = new FileInfo(outputPath).Length;
        Console.WriteLine($"Mold-A-Model complete: {outputPath} | {desc.Name} L={nLayers} H={nHeads} D={dModel} V={vocab} "
            + $"({fsz / 1048576.0:F0} MB) in {sw.Elapsed.TotalSeconds:F1}s — {opKeys.Count} distinct operators, per-head (no tiling)");
        return 0;
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



    private static string? RejectRetiredFoundryEnvVars() => null;



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
        if (hf == "model.norm.weight") return "output_norm.weight";
        if (hf == "lm_head.weight") return "output.weight";

        const string prefix = "model.layers.";
        if (hf.StartsWith(prefix, StringComparison.Ordinal))
        {
            int dot = hf.IndexOf('.', prefix.Length);
            if (dot > 0)
            {
                string idx = hf.Substring(prefix.Length, dot - prefix.Length);
                string rest = hf.Substring(dot + 1);
                string g = rest switch
                {
                    "self_attn.q_proj.weight" => "attn_q.weight",
                    "self_attn.k_proj.weight" => "attn_k.weight",
                    "self_attn.v_proj.weight" => "attn_v.weight",
                    "self_attn.o_proj.weight" => "attn_output.weight",
                    "mlp.gate_proj.weight" => "ffn_gate.weight",
                    "mlp.up_proj.weight" => "ffn_up.weight",
                    "mlp.down_proj.weight" => "ffn_down.weight",
                    "input_layernorm.weight" => "attn_norm.weight",
                    "post_attention_layernorm.weight" => "ffn_norm.weight",
                    _ => rest,
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

        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.context_length", 2048);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.embedding_length", (uint)recipe.HiddenSize);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.block_count", (uint)recipe.NumLayers);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.feed_forward_length", (uint)recipe.IntermediateSize);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.attention.head_count", (uint)recipe.NumHeads);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.attention.head_count_kv", (uint)recipe.NumKvHeads);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.vocab_size", (uint)recipe.VocabSize);
        SynthInterop.GgufWriterAddMetadataF32(gguf, "llama.attention.layer_norm_rms_epsilon", (float)recipe.RmsNormEps);
        SynthInterop.GgufWriterAddMetadataF32(gguf, "llama.rope.freq_base", (float)recipe.RopeTheta);


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
            SynthInterop.GgufWriterAddMetadataStr(gguf, "tokenizer.ggml.pre", "llama3");
        }
        else
            SynthInterop.GgufWriterAddMetadataStr(gguf, "tokenizer.ggml.model", "llama");
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.bos_token_id", bosId);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.eos_token_id", eosId);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.unknown_token_id", 0);
        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_bos_token", 1);
        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_eos_token", 0);



        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_space_prefix", 1);

        int n = tokens.Count;

        string spPath = Path.Combine(modelDir, "tokenizer.model");
        SpPiece[]? sp = File.Exists(spPath) ? ParseSentencePieceModel(spPath) : null;

        string[] pieces = new string[n];
        float[] scores = new float[n];
        int[] types = new int[n];

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
                    if (f2 == 1 && w2 == 2) { int l = (int)ReadVarint(d, ref pos); piece = Encoding.UTF8.GetString(d, pos, l); pos += l; }
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
