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

public static class FoundrySynthesis
{
    private static string ConnString => LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest);
    private static string ResolveBlob()=>LaplaceInstall.ResolveT0Perfcache();
    private static int Fail(string message){Console.Error.WriteLine(message);return 2;}

    public static async Task<int> SynthesizeAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (sub == "substrate")
        {
            string? recipeFrom = null, tokenizerDir = null, format = null;




            int nativeVocab = 0, nativeDim = 0, nativeLayers = 0, nativeHeads = 0, nativeKv = 0, nativeFfn = 0;



            string? crawlSeeds = null; int crawlHops = 3, crawlFanout = 64;




            bool grapheme = false;
            bool faithful = false;   // --faithful: adjacency-factor/lookup write (WriteFaithfulGgufAsync) instead of operator projection
            string? scopeSource = null; // comma-separated source short names; synthesis reads consensus re-folded over only their attestations
            var positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--format" when i + 1 < args.Length: format=args[++i]; break;
                    case "--grapheme-floor": grapheme = true; break;
                    case "--faithful": faithful = true; break;
                    case "--scope-source" when i + 1 < args.Length: scopeSource = args[++i]; break;
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
                return Fail("usage: laplace-mono synthesize substrate <recipe.json> <output.gguf>\n"
                          + "   or: laplace synthesize substrate --recipe-from <recipe-id-prefix> --tokenizer <dir> <output.gguf>\n"
                          + "   or: laplace synthesize substrate --native-vocab <N> --dim <D> [--layers L --heads H --kv-heads K --ffn F] <output.gguf>\n"
                          + "  [--scope-source <name,name,...>]  Mold-A-Model: synthesis ONLY the named sources' re-folded consensus (laplace.recipe synthesis runs)");

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
            return await SynthesizeFromSubstrateAsync(recipePath, outputPath, grapheme, scopeSource, faithful, format);
        }

        return Fail(
            "usage: laplace-mono synthesize <subcommand> [args]\n"
            + "  substrate <recipe.json> [output.gguf]                        synthesis consensus into a recipe-file mold\n"
            + "  substrate --recipe-from <id-prefix> --tokenizer <dir> [out]  synthesis a mold discovered from a deposed model ('*' = the only one)\n");
    }




    private static async Task<string?> MaterializeDiscoveredMoldAsync(string recipeIdPrefix, string tokenizerDir)
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        var recipes = await NpgsqlSubstrateReads.ModelRecipesAsync(ds, CancellationToken.None);
        var hits = new List<(string Hex, string Json)>();
        foreach (var recipe in recipes)
        {
            string hex = Convert.ToHexString(recipe.RecipeId).ToLowerInvariant();
            if (recipeIdPrefix == "*" || hex.StartsWith(recipeIdPrefix.ToLowerInvariant(), StringComparison.Ordinal))
                hits.Add((hex, recipe.RecipeJson));
        }
        if (hits.Count == 0) { Fail($"no deposed recipe matches '{recipeIdPrefix}' — list them via structural.model_recipes()"); return null; }
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
        string? crawlSeeds = null, int crawlHops = 3, int crawlFanout = 64, bool grapheme = false,
        IReadOnlyList<(string surface,long weight)>? selectedSurfaces = null)
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
        if (ffn <= 0) ffn = ((8 * dim / 3 + 255) / 256) * 256;

        CodepointPerfcache.Load(ResolveBlob());








        var sel = new List<(string surface, long weight)>(vocabN);
        string[]? seeds = crawlSeeds
            ?.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] crawlS = [];
        if (selectedSurfaces is not null) sel.AddRange(selectedSurfaces);
        else await using (var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString))
        {
            if (grapheme)
            {
                Console.WriteLine($"  vocab: GRAPHEME FLOOR ({vocabN} codepoint/grapheme atoms — "
                    + "tokenizes any text char-by-char in-engine, no merge path)");
                foreach (var r in await NpgsqlFoundryReads.GraphemeFloorVocabAsync(ds, vocabN))
                    sel.Add((r.Surface, r.Weight));
            }
            else
            {
                crawlS = seeds is { Length: > 0 } ? seeds : [];
                if (crawlS.Length == 0)
                {
                    int seedN = Math.Min(vocabN, FoundryDefaults.CrawlSeeds);
                    crawlS = (await NpgsqlFoundryReads.CorpusWordVocabAsync(
                        ds, seedN, FoundryDefaults.WordTrajs)).ToArray();
                    Console.WriteLine($"  vocab: corpus-seeded relation-closed crawl ({crawlS.Length} corpus seeds → {vocabN}, hops {crawlHops}, fanout {crawlFanout})");
                }
                else
                    Console.WriteLine($"  vocab: seeded crawl from [{string.Join(", ", seeds!)}] (hops {crawlHops}, fanout {crawlFanout})");
                foreach (var r in await NpgsqlFoundryReads.FoundryVocabCrawlAsync(
                    ds, crawlS, vocabN, crawlHops, crawlFanout))
                    sel.Add((r.Surface, r.Weight));

                if (crawlS.Length > 0)
                    await PinCrawlSeedsInPlaceAsync(ds, crawlS, sel, vocabN);
            }
        }
        if (sel.Count == 0)
        {
            Fail(seeds is { Length: > 0 }
                ? "generation.foundry_vocab_crawl returned nothing — none of the seed words converse.resolve(try other seeds / more hops)"
                : "generation.foundry_vocab returned nothing — ingest text first");
            return null;
        }

        // Byte-level BPE trained over the selected words (weighted by standing). The
        // merges ship in tokenizer.json: they rebuild in-vocab words exactly and carry
        // out-of-vocab text onto learned pieces before the byte floor.
        var bpeMerges = new List<string>();
        var bpeLearned = new List<(string piece, long freq)>();
        if (!grapheme)
        {
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            (bpeMerges, bpeLearned) = TrainByteBpe(sel, vocabN, 3 + 256);
            sw0.Stop();
            Console.WriteLine($"  bpe: {sel.Count:N0} words -> {bpeMerges.Count:N0} merges, {bpeLearned.Count:N0} learned pieces in {sw0.ElapsedMilliseconds}ms");
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
                // A piece with no letter or digit is emitted bare only; a "▁"-led form
                // would require a preceding space.
                bool punctLike = surface.Length > 0
                    && !surface.Any(ch => char.IsLetterOrDigit(ch));
                if (punctLike) { pieces.Add((surface, sc, 1)); continue; }
                pieces.Add(("▁" + surface, sc, 1));
                if (!(surface.Length == 1 && surface[0] < 128)) { pieces.Add((surface, sc, 1)); aliases++; }
            }
            Console.WriteLine($"  dual-form: +{aliases:N0} bare-word aliases (sentence-initial match; input-only)");

            // Learned BPE pieces enter the vocab so merge chains resolve to real tokens.
            // Stored in ▁/plain form; the GGUF writer re-encodes to the byte alphabet.
            var present = new HashSet<string>(pieces.Select(p => p.piece), StringComparer.Ordinal);
            int learnedAdded = 0;
            foreach (var (piece, freq) in bpeLearned)
            {
                string dec = ByteDecode(piece);
                string form = dec.StartsWith(' ') ? "▁" + dec[1..] : dec;
                if (form.Length == 0 || !present.Add(form)) continue;
                pieces.Add((form, (float)Math.Log(freq + 1.0), 1));
                learnedAdded++;
            }
            Console.WriteLine($"  bpe: +{learnedAdded:N0} learned pieces added to vocab");
        }
        // Deduplicate in the target encoding before assigning model ordinals: a
        // selected '!' and the byte-floor <0x21> realize the same GGUF token.
        if (!grapheme) pieces = DeduplicateNativePieces(pieces);
        int vocabSize = pieces.Count;
        Console.WriteLine($"  native vocab: {sel.Count:N0} substrate word entities + 256 byte floor + 3 specials = {vocabSize:N0}");

        string moldDir = Path.Combine(Path.GetTempPath(), $"laplace-native-mold-d{dim}-v{vocabSize}-{Guid.NewGuid():N}");
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
            w.WriteString("type", grapheme ? "WordLevel" : "BPE");
            w.WriteString("unk_token", "<unk>");
            if (!grapheme) w.WriteBoolean("ignore_merges", true);
            w.WriteStartObject("vocab");
            for (int i = 0; i < pieces.Count; i++) {
                var piece=pieces[i];
                string token=!grapheme && piece.type==1
                    ? ByteEncode(piece.piece.StartsWith('▁') ? " "+piece.piece[1..] : piece.piece) : piece.piece;
                w.WriteNumber(token,i);
            }
            w.WriteEndObject();
            if (!grapheme)
            {
                // merges stay in the byte-encoded (Ġ) alphabet — identical to the GGUF
                // token encoding; LlamaTokenizerParser.Canonicalize handles both forms.
                w.WriteStartArray("merges");
                foreach (var m in bpeMerges) w.WriteStringValue(m);
                w.WriteEndArray();
            }
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

    internal static List<(string piece, float score, int type)> DeduplicateNativePieces(
        IReadOnlyList<(string piece, float score, int type)> pieces)
    {
        var unique = new List<(string piece, float score, int type)>(pieces.Count);
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var piece in pieces)
        {
            string encoded = piece.type == 6
                ? ByteToUnicode[ParseByteToken(piece.piece)].ToString()
                : piece.type == 1
                    ? ByteEncode(piece.piece.StartsWith('▁') ? " " + piece.piece[1..] : piece.piece)
                    : piece.piece;
            if (slots.TryGetValue(encoded, out int slot))
            {
                // Preserve the selected content role and score at the byte's
                // existing slot. Its bytes and canonical BLAKE3 identity agree;
                // suppressing it as fallback would discard a selected output.
                if (unique[slot].type == 6 && piece.type == 1)
                    unique[slot] = piece;
                continue;
            }
            slots.Add(encoded, unique.Count);
            unique.Add(piece);
        }
        return unique;
    }



    /// <summary>
    /// Serializes a SentencePiece ModelProto through the NATIVE writer, the inverse of
    /// <see cref="ParseSentencePieceModel"/>. Round-trip equality is pinned by
    /// LaplaceSentencePiece.WriteThenParseRoundTrips in the engine test suite.
    /// </summary>
    private static void WriteSentencePieceModel(string path, IReadOnlyList<(string piece, float score, int type)> pieces)
    {
        int count = pieces.Count;
        var blobs = new byte[count][];
        var lens = new nuint[count];
        var scores = new float[count];
        var types = new int[count];
        for (int i = 0; i < count; i++)
        {
            blobs[i] = System.Text.Encoding.UTF8.GetBytes(pieces[i].piece);
            lens[i] = (nuint)blobs[i].Length;
            scores[i] = pieces[i].score;
            types[i] = pieces[i].type;
        }

        unsafe
        {
            var handles = new System.Runtime.InteropServices.GCHandle[count];
            var ptrs = new byte*[count == 0 ? 1 : count];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    handles[i] = System.Runtime.InteropServices.GCHandle.Alloc(
                        blobs[i], System.Runtime.InteropServices.GCHandleType.Pinned);
                    ptrs[i] = (byte*)handles[i].AddrOfPinnedObject();
                }

                byte* outBuf = null;
                nuint outLen = 0;
                int rc;
                fixed (byte** pp = ptrs)
                fixed (nuint* pl = lens)
                fixed (float* ps = scores)
                fixed (int* pt = types)
                {
                    rc = SynthInterop.SpModelWrite(pp, pl, ps, pt, count, &outBuf, &outLen);
                }
                if (rc != 0)
                    throw new InvalidOperationException($"sp_model_write returned {rc}");

                try
                {
                    var span = new ReadOnlySpan<byte>(outBuf, (int)outLen);
                    File.WriteAllBytes(path, span.ToArray());
                }
                finally
                {
                    SynthInterop.SpModelBufferFree(outBuf);
                }
            }
            finally
            {
                for (int i = 0; i < count; i++)
                    if (handles[i].IsAllocated) handles[i].Free();
            }
        }
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

    // Writes a cast whose embed/lm_head carry one adjacency readout and whose layers are
    // zero (no-op) with all-ones norms, so logits depend on the last token only. Readout
    // selection comes from FoundryDefaults (adjacency, relations band, trajectory order,
    // lookup).
    private static async Task<int> WriteFaithfulGgufAsync(
        NpgsqlDataSource ds,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Dictionary<Hash128, List<int>> tokenSlots,
        int vocab, int dModel, string modelDir, string outputPath,
        TensorSpec[] specs, int tensorCount, bool grapheme = false, FoundryReadout.Program? readout = null)
    {
        int cap = FoundryDefaults.FaithfulCap;
        var sw = Stopwatch.StartNew();
        // Readout plane A:
        //   grapheme  → letter-bigram order read off the trajectories;
        //   default   → consensus_adjacency: every in-vocab content edge, rank-weighted,
        //               including PRECEDES order edges;
        //   relations → consensus_layer_plane limited to relation rank [lo,hi], which excludes
        //               the low-rank order/metadata types (PRECEDES, HAS_DOMAIN, HAS_EXAMPLE).
        bool relations = !grapheme && FoundryDefaults.FaithfulRelations;
        double rkLo = FoundryDefaults.FaithfulRankLo;
        double rkHi = FoundryDefaults.FaithfulRankHi;
        // trajOrder: the relations band unioned with word continuation read directly off the
        // witnessed trajectories (word_order) rather than folded PRECEDES consensus; both
        // planes are normalized before the union.
        bool trajOrder = !grapheme && FoundryDefaults.FaithfulTrajOrder;
        FoundryExport.PlaneCoo A;
        if (readout is not null) A=readout.Plane;
        else if (grapheme)
            A = await FoundryExport.ReadGraphemeOrderAsync(ds, tokenSlots);
        else if (trajOrder)
        {
            var know  = FoundryExport.Normalize(await FoundryExport.ReadLayerPlaneAsync(ds, rkLo, rkHi, tokenSlots, cap));
            var order = FoundryExport.Normalize(await FoundryExport.ReadWordOrderAsync(
                ds, tokenSlots, 1, FoundryDefaults.WordTrajs, cap));
            A = FoundryExport.Union(know, order);
            Console.WriteLine($"  TRAJORDER readout: relations band rank∈[{rkLo},{rkHi}] ∪ exact trajectory continuation "
                + $"(word_order off LineStrings; {know.Nnz:N0} relations + {order.Nnz:N0} order edges)");
        }
        else if (relations)
        {
            A = await FoundryExport.ReadLayerPlaneAsync(ds, rkLo, rkHi, tokenSlots, cap);
            Console.WriteLine($"  RELATIONS readout: consensus content band rank∈[{rkLo},{rkHi}] (IS_A/property/synonym, no PRECEDES glue)");
        }
        else
            A = await FoundryExport.ReadAdjacencyAsync(ds, tokenSlots, cap);
        if (A.Nnz == 0)
            return Fail((grapheme ? "grapheme_order" : "consensus_adjacency")
                + " returned no edges over this vocab — ingest/seed first");
        var subjects = new HashSet<int>();
        foreach (var r in A.Rows) subjects.Add(r);
        Console.WriteLine($"  FAITHFUL adjacency read in {sw.Elapsed.TotalSeconds:F1}s: {A.Nnz:N0} rank-weighted edges "
            + $"over {subjects.Count:N0}/{vocab:N0} tokens (cap {cap})");

        double[] embed,lmHead;
        if(readout is not null) {
            readout.Compile(vocab,dModel,recipe.RmsNormEps,out embed,out lmHead);
            Console.WriteLine("  native lookup: canonical operand basis; explicit input/output realization; no factorization");
        } else {
        // Factor A at rank dModel into embed (U) and lm_head (V·S) over the conditional
        // log-odds of continuation P(Y|X), a generative readout rather than a similarity
        // transform. suppressSelf drops self-continuation for the word model.
        var swSvd = Stopwatch.StartNew();
        FoundryExport.FactorAdjacency(A, vocab, dModel, out embed, out lmHead, out int usedRank, conditional: true, suppressSelf: !grapheme);
        Console.WriteLine($"  FAITHFUL factorization in {swSvd.Elapsed.TotalSeconds:F1}s: "
            + $"log-odds-SVD rank {usedRank}/{dModel} → embed=U, lm_head=V·S, no-op layers");

        // Lookup (relations readout, dModel ≥ vocab): embed = I and lm_head[Y][X] = log-odds(X→Y),
        // so logits[Y] = lm_head[Y]·RMSNorm(e_X) is the standing lookup itself, with no SVD. When
        // dModel < vocab the SVD factors above are kept.
        bool lookup = relations && FoundryDefaults.FaithfulLookup && dModel >= vocab;
        if (lookup)
        {
            Array.Clear(embed); Array.Clear(lmHead);
            var rowSum = new double[vocab];
            for (long e = 0; e < A.Nnz; e++)
            {
                int x = A.Rows[e], y = A.Cols[e];
                if (x >= 0 && x < vocab && y >= 0 && y < vocab && x != y) rowSum[x] += A.Vals[e];
            }
            for (int i = 0; i < vocab && i < dModel; i++) embed[(long)i * dModel + i] = 1.0;   // embed = I
            double invScale = 1.0 / Math.Sqrt(dModel);   // cancels RMSNorm(e_X)=√dModel·e_X
            for (long e = 0; e < A.Nnz; e++)
            {
                int x = A.Rows[e], y = A.Cols[e];
                if (x < 0 || x >= vocab || y < 0 || y >= vocab || x == y || x >= dModel) continue;
                if (rowSum[x] <= 0) continue;
                lmHead[(long)y * dModel + x] = Math.Log(A.Vals[e] / rowSum[x] * vocab) * invScale;
            }
            Console.WriteLine("  LOOKUP: embed=I, lm_head=log-odds(A) — the GEMM IS the attestation lookup (no SVD, no global hub)");
        }
        else if (relations)
            Console.WriteLine($"  (lookup needs dModel≥vocab; dModel={dModel} vocab={vocab} → SVD fallback, may re-introduce the hub)");

        // A token with no incoming edge in A is never a continuation; its lm_head row would be an
        // arbitrary SVD null-space direction. Zero it so only witnessed continuations are emitted.
        // embed is left intact, so these tokens still read as inputs.
        {
            var isContinuation = new bool[vocab];
            foreach (var y in A.Cols) if (y >= 0 && y < vocab) isContinuation[y] = true;
            // Word model: byte-level tokens are inputs only and are also zeroed in lm_head.
            var isByte = new bool[vocab];
            if (!grapheme) foreach (var t in tokens) if (t.TokenId >= 0 && t.TokenId < vocab && t.IsByteLevel) isByte[t.TokenId] = true;
            int suppressed = 0;
            for (int y = 0; y < vocab; y++)
                if (!isContinuation[y] || isByte[y]) { for (int c = 0; c < dModel; c++) lmHead[(long)y * dModel + c] = 0; suppressed++; }
            Console.WriteLine($"  suppressed {suppressed:N0}/{vocab:N0} non-continuation + byte tokens from lm_head (word continuations only)");
        }

        // In-process check before the cast: with no-op layers, logits[Y|X] rank exactly as
        // lm_head[Y]·embed[X] (the final RMSNorm is a positive per-X scalar). For each probe token
        // the reconstructed top-5 is compared against the top edges of A[X,·]; the verdict is
        // printed, not enforced.
        {
            var id2surf = new string[vocab];
            var surf2id = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in tokens)
            {
                if (t.TokenId < 0 || t.TokenId >= vocab) continue;
                string s = t.RawToken.StartsWith('▁') ? t.RawToken[1..] : t.RawToken;
                id2surf[t.TokenId] = s;
                if (s.Length > 0 && !surf2id.ContainsKey(s)) surf2id[s] = t.TokenId;
            }
            // raw-A ground truth: subject ordinal → top objects by weight
            var rawTop = new Dictionary<int, List<int>>();
            {
                var byX = new Dictionary<int, List<(int Y, double W)>>();
                for (long e = 0; e < A.Nnz; e++)
                {
                    int x = A.Rows[e];
                    if (!byX.TryGetValue(x, out var l)) byX[x] = l = new();
                    l.Add((A.Cols[e], A.Vals[e]));
                }
                foreach (var (x, l) in byX)
                {
                    l.Sort((a, b) => b.W.CompareTo(a.W));
                    rawTop[x] = l.Take(10).Select(p => p.Y).ToList();
                }
            }
            string[] probes = { "dog", "king", "water", "fire", "man", "city", "tree", "food", "house", "river" };
            int gatePass = 0, gateTotal = 0;
            Console.WriteLine("  ── acceptance gate: reconstructed continuation vs raw adjacency (ground truth) ──");
            foreach (var p in probes)
            {
                if (!surf2id.TryGetValue(p, out int x) || !rawTop.TryGetValue(x, out var truth) || truth.Count == 0)
                    continue;
                gateTotal++;
                // reconstructed top-5: argmax_Y lm_head[Y]·embed[X]
                long xo = (long)x * dModel;
                var scored = new (int Y, double L)[vocab];
                for (int y = 0; y < vocab; y++)
                {
                    long yo = (long)y * dModel; double dot = 0;
                    for (int c = 0; c < dModel; c++) dot += embed[xo + c] * lmHead[yo + c];
                    scored[y] = (y, dot);
                }
                Array.Sort(scored, (a, b) => b.L.CompareTo(a.L));
                var recon = scored.Take(5).Select(s => s.Y).ToList();
                int overlap = recon.Count(y => truth.Contains(y));
                if (overlap >= 1) gatePass++;
                string Render(IEnumerable<int> ids) => string.Join(", ",
                    ids.Select(y => (y >= 0 && y < vocab ? id2surf[y] : null) ?? $"#{y}"));
                Console.WriteLine($"    {p,-8} recon[{Render(recon)}]  vs truth[{Render(truth.Take(5))}]  overlap={overlap}/5");
            }
            string verdict = gateTotal == 0 ? "NO PROBES IN VOCAB"
                : gatePass >= (gateTotal + 1) / 2 ? $"PASS ({gatePass}/{gateTotal} probes reproduce ≥1 top edge)"
                : $"WEAK ({gatePass}/{gateTotal}) — rank {dModel} under-captures the adjacency; raise --dim";
            Console.WriteLine($"  ── gate verdict: {verdict} ──");
        }

        }

        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero) return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens, modelDir, byteBpe: !grapheme);
        readout?.WriteReceipt(gguf,outputPath,tokens);
        var swW = Stopwatch.StartNew();
        for (int i = 0; i < tensorCount; i++)
        {
            string name; ulong rows, cols; int dtype;
            unsafe
            {
                var sp = specs[i];
                name  = Marshal.PtrToStringUTF8((IntPtr)sp.Name) ?? "";
                rows  = sp.Rank >= 1 ? sp.Shape[0] : 1;
                cols  = sp.Rank >= 2 ? sp.Shape[1] : 1;
                dtype = 0;   // F32: bf16 rounding erases the small embed deltas
            }
            int tr = (int)rows, tc = (int)Math.Max(1UL, cols);
            var vals = new float[(long)tr * tc];   // zero-initialized = the no-op layer fill

            if (name == "model.embed_tokens.weight")
            {
                // embed[X,c] = U[X,c]·√S  (rows = vocab, cols = dim)
                for (int r = 0; r < tr; r++)
                    for (int c = 0; c < tc; c++)
                        vals[(long)r * tc + c] = (float)embed[(long)r * dModel + c];
            }
            else if (name == "lm_head.weight")
            {
                // lm_head[Y,c] = V[Y,c]·√S  (rows = vocab, cols = dim)
                for (int r = 0; r < tr; r++)
                    for (int c = 0; c < tc; c++)
                        vals[(long)r * tc + c] = (float)lmHead[(long)r * dModel + c];
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
                string rest = name[(layerDot + 1)..];
                // recognized no-op slots stay zero; an unknown layer tensor is a hard error
                switch (rest)
                {
                    case "self_attn.q_proj.weight": case "self_attn.k_proj.weight":
                    case "self_attn.v_proj.weight": case "self_attn.o_proj.weight":
                    case "mlp.gate_proj.weight":    case "mlp.up_proj.weight":
                    case "mlp.down_proj.weight":    break;   // zero (no-op)
                    default:
                        Console.WriteLine($"  faithful foundry does not define mold tensor '{name}'");
                        SynthInterop.GgufWriterFree(gguf); return 3;
                }
            }
            else
            {
                Console.WriteLine($"  faithful foundry does not define mold tensor '{name}'");
                SynthInterop.GgufWriterFree(gguf); return 3;
            }

            byte[] tensorBytes = dtype == 0 ? FoundryExport.ToF32Bytes(vals) : FoundryExport.ToBf16Bytes(vals);
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
        if (rcw != 0) return Fail($"gguf_writer_finalize failed (rc={rcw}) for {outputPath}");
        MaybeEncryptExportedCartridge(outputPath);
        long fsz = new FileInfo(outputPath).Length;
        Console.WriteLine($"FAITHFUL synthesis complete: {outputPath} ({fsz / 1048576.0:F0} MB) in {swW.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

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

    /// <summary>
    /// HuggingFace tensor name -> GGML/GGUF name, resolved by the engine
    /// (gguf_tensor_name_hf_to_ggml) so the mapping lives with the format it serves.
    /// Unknown names pass through unchanged.
    /// </summary>
    private static string HfToGgmlName(string hf)
    {
        unsafe
        {
            const int cap = 512;
            byte* buf = stackalloc byte[cap];
            int n = SynthInterop.GgufTensorNameHfToGgml(hf, buf, cap);
            if (n < 0)
                throw new InvalidOperationException(
                    $"gguf_tensor_name_hf_to_ggml failed for '{hf}' (name longer than {cap} bytes?)");
            return System.Text.Encoding.UTF8.GetString(buf, n);
        }
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

    /// Raw model.merges strings from a tokenizer.json ("A B" pairs, byte-encoded
    /// alphabet, order = merge rank). Empty list when absent.
    private static List<string> ReadTokenizerMerges(string tokenizerJsonPath)
    {
        var merges = new List<string>();
        if (!File.Exists(tokenizerJsonPath)) return merges;
        using var doc = JsonAstDocument.Parse(File.ReadAllBytes(tokenizerJsonPath));
        var arr = doc.Root.Property("model").Property("merges");
        if (!arr.IsArray) return merges;
        foreach (var el in arr.Items())
        {
            if (el.Kind == JsonAstKind.String)
            {
                var s = el.AsString();
                if (!string.IsNullOrEmpty(s)) merges.Add(s);
            }
            else if (el.IsArray)
            {
                var pair = el.Items().ToArray();
                if (pair.Length != 2) continue;
                var l = pair[0].AsString(); var r = pair[1].AsString();
                if (!string.IsNullOrEmpty(l) && !string.IsNullOrEmpty(r)) merges.Add(l + " " + r);
            }
        }
        return merges;
    }

    private static readonly Dictionary<char, byte> UnicodeToByte = BuildUnicodeToByte();
    private static Dictionary<char, byte> BuildUnicodeToByte()
    {
        var inv = new Dictionary<char, byte>(256);
        for (int b = 0; b < 256; b++) inv[ByteToUnicode[b]] = (byte)b;
        return inv;
    }
    private static string ByteDecode(string s)
    {
        var bytes = new byte[s.Length];
        int n = 0;
        foreach (var c in s)
        {
            if (!UnicodeToByte.TryGetValue(c, out var b)) return s;
            bytes[n++] = b;
        }
        return Encoding.UTF8.GetString(bytes, 0, n);
    }

    private static void WriteGgufMetadata(
        IntPtr gguf,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        string modelDir, bool byteBpe = false)
    {
        SynthInterop.GgufWriterAddMetadataStr(gguf, "general.architecture", "llama");
        SynthInterop.GgufWriterAddMetadataStr(gguf, "general.name", recipe.ModelType+"-"+recipe.RecipeEntityId.ToHex());

        using var config=JsonAstDocument.Parse(recipe.CanonicalJson);
        var ctx=config.Root.Property("max_position_embeddings");
        uint contextLength=ctx.IsValid
            ? checked((uint)(ctx.AsInt64() ?? throw new InvalidDataException("max_position_embeddings must be an integer"))) : 2048;
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.context_length", contextLength);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.embedding_length", (uint)recipe.HiddenSize);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.block_count", (uint)recipe.NumLayers);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.feed_forward_length", (uint)recipe.IntermediateSize);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.attention.head_count", (uint)recipe.NumHeads);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.attention.head_count_kv", (uint)recipe.NumKvHeads);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.vocab_size", (uint)recipe.VocabSize);
        SynthInterop.GgufWriterAddMetadataF32(gguf, "llama.attention.layer_norm_rms_epsilon", (float)recipe.RmsNormEps);
        SynthInterop.GgufWriterAddMetadataF32(gguf, "llama.rope.freq_base",
            (float)recipe.RopeTheta);


        uint bosId = 1, eosId = 2;
        string genCfgPath = Path.Combine(modelDir, "generation_config.json");
        if (File.Exists(genCfgPath))
        {
            using var gen = JsonAstDocument.Parse(File.ReadAllBytes(genCfgPath));
            var bos=gen.Root.Property("bos_token_id");
            if (bos.Kind == JsonAstKind.Number)
                bosId = checked((uint)(bos.AsInt64() ?? throw new InvalidDataException("bos_token_id must be an integer")));
            var eos=gen.Root.Property("eos_token_id");
            if (eos.Kind == JsonAstKind.Number)
                eosId = checked((uint)(eos.AsInt64() ?? throw new InvalidDataException("eos_token_id must be an integer")));
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
        var sortedTokens = tokens.OrderBy(t => t.TokenId).ToArray();

        // Laplace Universal BLAKE3 Token Vocabulary (laplace-blake3-v1):
        // Every token slot in the exported GGUF is anchored to its 16-byte BLAKE3
        // substrate entity ID (codepoint, word, n-gram, synset, trajectory, or document).
        SynthInterop.GgufWriterAddMetadataStr(gguf, "laplace.tokenizer.scheme", "laplace-blake3-v1");
        SynthInterop.GgufWriterAddMetadataU32(gguf, "laplace.vocab.size", (uint)n);
        var blake3Ids = new string[n];
        for (int i = 0; i < n; i++)
            blake3Ids[i] = sortedTokens[i].EntityId.ToHex();
        byte[] packedBlake3 = PackStrings(blake3Ids);
        unsafe
        {
            fixed (byte* p = packedBlake3)
                SynthInterop.GgufWriterAddMetadataStrArrayPacked(
                    gguf, "laplace.vocab.blake3_ids", p, (nuint)packedBlake3.Length, (nuint)n);
        }

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
            for (int i = 0; i < n; i++) { pieces[i] = sortedTokens[i].RawToken; scores[i] = 0f; types[i] = ClassifyTokenType(sortedTokens[i].RawToken); }
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

        var serializedSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < pieces.Length; i++)
        {
            if (serializedSlots.TryGetValue(pieces[i], out int previous))
                throw new InvalidDataException(
                    $"GGUF tokenizer token slots {previous} and {i} serialize to the same string {pieces[i]}");
            serializedSlots.Add(pieces[i], i);
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
            // Trained merges from tokenizer.json when present. Otherwise merges are
            // reconstructed from adjacent character pairs of each piece, which cannot
            // chain past two characters.
            var mlist = ReadTokenizerMerges(Path.Combine(modelDir, "tokenizer.json"));
            if (mlist.Count > 0)
                Console.WriteLine($"  byte-level BPE: {mlist.Count:N0} trained merges from tokenizer.json");
            else
            {
                var mseen = new HashSet<string>();
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
                Console.WriteLine($"  byte-level BPE: {mlist.Count:N0} reconstructed bigram merges (no trained merges found)");
            }
            byte[] mp = PackStrings(mlist.ToArray());
            unsafe { fixed (byte* p = mp) SynthInterop.GgufWriterAddMetadataStrArrayPacked(gguf, "tokenizer.ggml.merges", p, (nuint)mp.Length, (nuint)mlist.Count); }
        }

        string cfgPath = Path.Combine(modelDir, "tokenizer_config.json");
        if (File.Exists(cfgPath))
        {
            using var cfg = JsonAstDocument.Parse(File.ReadAllBytes(cfgPath));
            if (cfg.Root.String("chat_template") is { } template)
                SynthInterop.GgufWriterAddMetadataStr(gguf, "tokenizer.chat_template", template);
        }
    }

    private sealed record SpPiece(string Piece, float Score, int Type);

    /// <summary>
    /// Reads a SentencePiece ModelProto through the NATIVE parser
    /// (engine/synthesis/src/sentencepiece_parser.cpp). Piece bytes come back verbatim,
    /// so U+2581 and other multi-byte forms keep their exact token identity.
    /// </summary>
    private static SpPiece[] ParseSentencePieceModel(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        IntPtr m;
        unsafe
        {
            fixed (byte* p = d) m = SynthInterop.SpModelParse(p, (nuint)d.Length);
        }
        if (m == IntPtr.Zero)
            throw new InvalidDataException(
                $"sentencepiece: '{path}' is not a readable ModelProto (malformed varint or " +
                "truncated field). Refusing to load a partial vocabulary — every token id depends on it.");

        try
        {
            int n = SynthInterop.SpModelPieceCount(m);
            var pieces = new SpPiece[n];
            for (int i = 0; i < n; i++)
            {
                string text;
                unsafe
                {
                    nuint len;
                    IntPtr p = SynthInterop.SpModelPiece(m, i, &len);
                    text = p == IntPtr.Zero
                        ? string.Empty
                        : System.Text.Encoding.UTF8.GetString((byte*)p, (int)len);
                }
                pieces[i] = new SpPiece(text, SynthInterop.SpModelScore(m, i), SynthInterop.SpModelType(m, i));
            }
            return pieces;
        }
        finally
        {
            SynthInterop.SpModelFree(m);
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

    private static void MaybeEncryptExportedCartridge(string outputPath)
    {
        string? keyHex = Environment.GetEnvironmentVariable("LAPLACE_CARTRIDGE_KEY_HEX");
        if (string.IsNullOrWhiteSpace(keyHex))
            return;
        byte[] key = Convert.FromHexString(keyHex.Trim());
        FoundryCartridgeCrypto.EncryptFileInPlace(outputPath, key);
        Console.WriteLine($"  encrypted Mold-A-Model cartridge in-place (LPCART01 AES-256-GCM): {outputPath}");
    }
}
