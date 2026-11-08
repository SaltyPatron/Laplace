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

}
