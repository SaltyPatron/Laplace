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
            // Substrate-native vocab: the token list is SELECTED from the substrate's own
            // content entities (the most-connected wordforms) and the shape (dim/layers/
            // heads/ffn) is declared on the command line — the foundry generates a fresh
            // embed/lm_head/operators at that shape. No foreign tokenizer, no source model.
            int nativeVocab = 0, nativeDim = 0, nativeLayers = 0, nativeHeads = 0, nativeKv = 0, nativeFfn = 0;
            // --crawl "kung,fu,martial,arts": the vocab is the SEEDED CRAWL neighborhood of
            // those words (foundry_vocab_crawl), not a global frequency top-N. The token list
            // is exactly what the seeds reference/relate to in the consensus.
            string? crawlSeeds = null; int crawlHops = 3, crawlFanout = 64;
            // --grapheme-floor: vocab = the substrate's codepoint/grapheme floor (single tokens
            // that tile ANY text char-by-char in-engine — no merge path, no byte-shredding),
            // embed/lm_head = the factored grapheme_order (real letter statistics off the
            // tier-2 trajectory LineStrings). The universal tokenizer base.
            bool grapheme = false;
            var positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--grapheme-floor": grapheme = true; break;
                    case "--grapheme-ngram": grapheme = true; Environment.SetEnvironmentVariable("LAPLACE_FOUNDRY_NGRAM", "1"); break;
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

    // Reads a discovered recipe out of the substrate (laplace.model_recipes) and lays
    // it down as a mold directory: config.json straight from the DB, tokenizer files
    // copied from the caller's dir. Returns the config.json path, or null after Fail.
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

    // Lays down a mold (config.json + tokenizer.json + tokenizer.model) whose VOCAB is the
    // substrate's own content entities (laplace.foundry_vocab — the invention's selectable
    // token list, ranked by consensus richness so the basis carries geometry) and whose
    // SHAPE is declared on the command line. The tokenizer is a real SentencePiece (SPM)
    // model: 256 byte-fallback tokens (any character/newline encodes), ▁-prefixed word
    // pieces with frequency scores, CONTROL/UNKNOWN specials. Word surfaces are content-
    // addressed, so the parser re-derives the SAME entity id (and coords) that carry the
    // consensus edges. The foundry casts a fresh embed/lm_head/operators at the asked shape.
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
        if (ffn     <= 0) ffn = ((8 * dim / 3 + 255) / 256) * 256;   // canonical SwiGLU width, ÷256

        CodepointPerfcache.Load(ResolveBlob());   // render_text path needs the floor oracle (this process)

        // The token list is the substrate's own — a permanent substrate function selects it
        // (the CLI never improvises the SQL). Either:
        //   --crawl "a,b,c"  → foundry_vocab_crawl: the SEEDED neighborhood of those words
        //                      (native BFS over the consensus' lexical relations), focused +
        //                      self-defining — what the seeds reference/relate to.
        //   (default)        → foundry_vocab: the most-connected wordforms + their 1-hop
        //                      definitional closure.
        var sel = new List<(string surface, long weight)>(vocabN);
        string[]? seeds = crawlSeeds
            ?.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await using (var ds = new NpgsqlDataSourceBuilder(ConnString).Build())
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 0;   // the crawl/closure runs server-side; never hit the read timeout
            if (grapheme)
            {
                cmd.CommandText = "SELECT surface, weight FROM laplace.grapheme_floor_vocab($1)";
                cmd.Parameters.AddWithValue(vocabN);
                Console.WriteLine($"  vocab: GRAPHEME FLOOR ({vocabN} codepoint/grapheme atoms — "
                    + "tokenizes any text char-by-char in-engine, no merge path)");
            }
            else
            {
                // THE VOCAB IS ALWAYS A RELATION-CLOSED CRAWL — never a flat richness/frequency top-k.
                // --crawl gives explicit seeds; otherwise AUTO-SEED from the corpus's most frequent
                // words (flow vocab, stop words INCLUDED) and crawl their relation neighborhood, which
                // pulls in the knowledge targets (king→monarch, dog→animal). So the same vocab carries
                // FLOW (the frequent seeds) AND KNOWLEDGE (their closure). foundry_vocab (the flat
                // richness top-k that dropped stop words) is GONE.
                string[] crawlS = seeds is { Length: > 0 } ? seeds : System.Array.Empty<string>();
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
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                sel.Add((rdr.GetString(0), rdr.GetInt64(1)));
        }
        if (sel.Count == 0)
        {
            Fail(seeds is { Length: > 0 }
                ? "laplace.foundry_vocab_crawl returned nothing — none of the seed words resolve (try other seeds / more hops)"
                : "laplace.foundry_vocab returned nothing — ingest text first");
            return null;
        }

        // The ordered SentencePiece table (id = list index): specials, the 256-byte floor,
        // then ▁-prefixed word pieces. SP type: 1=NORMAL, 2=UNKNOWN, 3=CONTROL, 6=BYTE.
        // Word score = log-degree (frequent/rich words win longest-match); bytes score low
        // so they are fallback only; the word always beats its byte decomposition.
        var pieces = new List<(string piece, float score, int type)>(3 + 256 + sel.Count);
        pieces.Add(("<unk>", 0f, 2));
        pieces.Add(("<s>",   0f, 3));
        pieces.Add(("</s>",  0f, 3));
        for (int b = 0; b < 256; b++) pieces.Add(($"<0x{b:X2}>", -20f, 6));
        if (grapheme)
        {
            // single-grapheme pieces (bare, NO ▁ prefix) so SP tokenizes char-by-char with no
            // merge path — every piece is one codepoint, always directly reachable. ▁ is the
            // space atom (SP maps spaces to it). This is the cut that always tokenizes in-engine.
            pieces.Add(("▁", 1f, 1));
            foreach (var (surface, weight) in sel)
                pieces.Add((surface, (float)(Math.Log(weight + 1.0) + 1.0), 1));
        }
        else
        {
            // DUAL-FORM word vocab (the researched fix for the sentence-initial shred): each word gets
            // the "▁word" piece (byteBpe→'Ġword', the EMITTED spaced form) AND a bare "word" alias so a
            // SENTENCE-INITIAL / post-punctuation word matches (byte-level BPE bakes the space into
            // 'Ġword', so the first word would otherwise byte-shred — real models like Llama-4 carry
            // BOTH forms). Both canonicalize to the same surface→entity, so the factorization fills both
            // rows; the bare alias is suppressed from lm_head (input-only). SKIP a single ASCII char —
            // its byte-encoding is identical to a byte-floor token (the byte token already shares the
            // grapheme entity and serves as that word's bare input form), so no duplicate piece.
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

        // FAITHFUL transcription (attestations ARE the tensors): embed and lm_head are the
        // low-rank factors of the rank-weighted rated adjacency A (truncated SVD to rank=dim),
        // intermediate layers are no-ops. dim is a NORMAL hidden width (the SVD rank, e.g. 512),
        // NOT vocab — a 32k-token model is 32000×dim, never 32000². Only constraint: one no-op
        // layer (the readout transform is the factorization, depth carries no operator here).
        if (grapheme || FoundryExport.EnvInt("LAPLACE_FOUNDRY_FAITHFUL", 0) != 0)
        {
            layers = 1;
            Console.WriteLine($"  FAITHFUL: dim={dim} (SVD rank), 1 no-op layer, embed=U√S, lm_head=V√S of "
                + (grapheme ? "grapheme_order" : "A"));
        }

        string moldDir = Path.Combine(Path.GetTempPath(), $"laplace-native-mold-d{dim}-v{vocabSize}");
        Directory.CreateDirectory(moldDir);

        // tokenizer.json — the parser reads this to map each piece to its content entity
        // (▁word → word entity; <0xXX> → byte atom; specials flagged). vocab key = the piece.
        await using (var fs = File.Create(Path.Combine(moldDir, "tokenizer.json")))
        await using (var w = new System.Text.Json.Utf8JsonWriter(fs))
        {
            w.WriteStartObject();
            w.WriteString("version", "1.0");
            w.WriteStartArray("added_tokens");
            for (int i = 0; i < 3; i++)   // the specials are the only special tokens
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

        // tokenizer.model — the real SentencePiece proto (pieces + scores + types) that
        // WriteGgufMetadata reads to emit a correct SPM tokenizer into the gguf.
        WriteSentencePieceModel(Path.Combine(moldDir, "tokenizer.model"), pieces);

        // config.json: a flat llama recipe at the asked shape (the native recipe parser is
        // top-level-only; every field is a scalar/array).
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

    // Write a minimal SentencePiece ModelProto: repeated field 1 = SentencePiece{ piece(1,
    // string), score(2, float), type(3, varint) }. ParseSentencePieceModel reads exactly this.
    private static void WriteSentencePieceModel(string path, IReadOnlyList<(string piece, float score, int type)> pieces)
    {
        static void Varint(System.IO.Stream s, ulong v) { while (v >= 0x80) { s.WriteByte((byte)(v | 0x80UL)); v >>= 7; } s.WriteByte((byte)v); }
        static void Tag(System.IO.Stream s, int field, int wire) => Varint(s, ((ulong)(uint)field << 3) | (uint)wire);
        using var ms = new System.IO.MemoryStream();
        foreach (var (piece, score, type) in pieces)
        {
            using var inner = new System.IO.MemoryStream();
            byte[] pb = Encoding.UTF8.GetBytes(piece);
            Tag(inner, 1, 2); Varint(inner, (ulong)pb.Length); inner.Write(pb);   // piece
            Tag(inner, 2, 5); inner.Write(BitConverter.GetBytes(score));          // score (float32 LE)
            Tag(inner, 3, 0); Varint(inner, (ulong)type);                         // type
            byte[] ib = inner.ToArray();
            Tag(ms, 1, 2); Varint(ms, (ulong)ib.Length); ms.Write(ib);            // ModelProto.pieces[]
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
            // Grapheme floor: skip byte tokens from the entity map. A byte token <0xXX> is the
            // SAME content-addressed entity as its grapheme (0x72 == 'r'), so mapping both
            // splits the probability mass 50/50 across redundant tokens. Excluded byte tokens
            // zero-fill (logit ≈ 0, below the grapheme's positive log-odds) and stay in the
            // vocab purely as invalid-UTF-8 fallback. All mass routes through the grapheme.
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
        Console.WriteLine($"  recipe + arch template: {tensorCount} tensor slots, vocab={vocab}, hidden={dModel}");

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();

        // ── FAITHFUL transcription path ───────────────────────────────────────────────
        // attestations ARE the tensors — no basis, no SVD, no operators, no gains, no PMI,
        // no rank bands, no hand-typed weights. embed = identity, lm_head = the rank-weighted
        // rated adjacency A (read whole from consensus_adjacency, rank looked up per edge from
        // the banked law), layers = no-op. logits[Y] = A[Y,X] exactly: the GEMM is the lookup.
        if (grapheme || FoundryExport.EnvInt("LAPLACE_FOUNDRY_FAITHFUL", 0) != 0)
        {
            // The grapheme floor defaults to the SHIFT-HEAD TRIGRAM, not the gap-1 bigram. A pure
            // bigram readout (WriteFaithful) has no state beyond the current grapheme, so greedy
            // decode walks into a fixed cycle ("...aaaa"). WriteGraphemeNgram carries the previous
            // grapheme (the gap=2 skip-gram "history subspace" grapheme_order already computes) —
            // the diagnosed fix. Opt back to the bigram with LAPLACE_FOUNDRY_NGRAM=0.
            bool ngram = FoundryExport.EnvInt("LAPLACE_FOUNDRY_NGRAM", grapheme ? 1 : 0) != 0;
            // multi-offset multi-head regressed (interpolation sums function-word hubs → "the of the of");
            // default to the single-head trigram. Opt back in with LAPLACE_FOUNDRY_MULTIHEAD=1.
            bool multiHead = ngram && !grapheme && recipe.NumHeads > 1
                && FoundryExport.EnvInt("LAPLACE_FOUNDRY_MULTIHEAD", 0) != 0;
            // REPHEAD: shift head + a baked-in repetition penalty (head 1, uniform attention) — the loop cure.
            bool repHead = ngram && !grapheme && FoundryExport.EnvInt("LAPLACE_FOUNDRY_REPHEAD", 0) != 0;
            // CONTENT: knowledge∪order readout (king→monarch) + REPHEAD shift/rep/space-led machinery
            // + strong de-hub (suppresses function-word AND ontology-root hubs). Word vocab only.
            bool content = !grapheme && FoundryExport.EnvInt("LAPLACE_FOUNDRY_CONTENT", 0) != 0;
            // TOPIC: content readout + a uniform topic head (running mean knowledge over the context) so
            // generation is anchored to the whole prompt's topic, not just the current token's IsA chain.
            bool topic = !grapheme && FoundryExport.EnvInt("LAPLACE_FOUNDRY_TOPIC", 0) != 0;
            int faithfulRc = topic
                ? await WriteWordTopicGgufAsync(ds, recipe, tokens, tokenSlots, vocab, dModel, modelDir, outputPath, specs, tensorCount)
                : content
                ? await WriteWordContentGgufAsync(ds, recipe, tokens, tokenSlots, vocab, dModel, modelDir, outputPath, specs, tensorCount)
                : repHead
                ? await WriteWordRepGgufAsync(ds, recipe, tokens, tokenSlots, vocab, dModel, modelDir, outputPath, specs, tensorCount)
                : multiHead
                ? await WriteWordMultiHeadGgufAsync(ds, recipe, tokens, tokenSlots, vocab, dModel, modelDir, outputPath, specs, tensorCount)
                : ngram
                ? await WriteGraphemeNgramGgufAsync(ds, recipe, tokens, tokenSlots, vocab, dModel, modelDir, outputPath, specs, tensorCount, word: !grapheme)
                : await WriteFaithfulGgufAsync(ds, recipe, tokens, tokenSlots, vocab, dModel, modelDir, outputPath, specs, tensorCount, grapheme);
            SynthInterop.ArchTemplateFree(tmplHandle);
            SynthInterop.RecipeFree(recipeHandle);
            return faithfulRc == 0 ? 0 : Fail($"{(ngram ? "n-gram" : "faithful")} foundry write failed (status {faithfulRc})");
        }

        // ── read the consensus planes (one set-based query each; entity→ordinal is
        // perf-cache territory, the DB is never asked per token). The pour draws from
        // BOTH witness classes over the same token entities: the model behavioral
        // planes AND the corpus sequence consensus — "the capital of" → "France" is
        // PRECEDES testimony, and it is exactly what the completion operator encodes.
        int degreeCap = FoundryExport.EnvInt("LAPLACE_FOUNDRY_LE_DEGREE", 48);
        var swPour = Stopwatch.StartNew();
        // The export reads the FOLDED CONSENSUS — the product of the ingest-time fold, not
        // the raw geometry. The trajectory order (precedes/follows) was already folded into
        // the PRECEDES relation at deposit; re-deriving co-occurrence from the trajectories at
        // export time is recomputing what consensus already holds. So every operator role is
        // ONE vocab-bounded, INDEX-DRIVEN read (entity_relation_plane: the vocab×vocab edges of
        // the role's relation set, degree-capped server-side via the (subject_id, type_id)
        // index) — microseconds, no scan, no corpus build. A deposited model is NOT required;
        // its behavioral relations (ATTENDS/OV_RELATES/COMPLETES_TO) just join when present.
        // WITHIN-LAYER reads (the ranks ARE the layers): each role plane is one rank band,
        // content objects only, eff_mu weights — no flattening, no app/structural metadata
        // (HAS_POS etc.), no unnamed types. consensus_layer_plane enforces it server-side.
        Task<FoundryExport.PlaneCoo> LayerAsync(double lo, double hi)
            => FoundryExport.ReadLayerPlaneAsync(ds, lo, hi, tokenSlots, degreeCap);
        var simTask = LayerAsync(0.50, 0.60);   // equivalence (0.55): sameness/synonym  → embedding
        var relTask = LayerAsync(0.68, 0.86);   // taxonomic+partitive (0.82/0.73): is-a/has-part → V/O
        var preTask = LayerAsync(0.60, 0.68);   // causal (0.64): what follows/results    → FFN
        var attTask = LayerAsync(0.30, 0.50);   // associative+oppositional (0.36/0.45)    → attention
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

        // ORDER channel: witnessed continuation read straight off the trajectory stream
        // (native trajectory_cooccurrence_by_stride). This is the directed "what follows"
        // signal the folded causal consensus band lacks (147 edges over the probe vocab),
        // and it is what makes the readout predict a continuation instead of a neighbor.
        int trajGap = FoundryExport.EnvInt("LAPLACE_FOUNDRY_TRAJ_GAP", Math.Max(2, Math.Min(nLayers, 8)));
        var swTraj = Stopwatch.StartNew();
        var traj = FoundryExport.Normalize(
            await FoundryExport.ReadTrajectoryStrideAsync(ds, trajGap, tokenSlots, degreeCap));
        Console.WriteLine($"  trajectory order ladder read in {swTraj.Elapsed.TotalSeconds:F1}s "
            + $"(gap≤{trajGap}): {traj.Nnz:N0} ordered continuation edges");

        // RANK-WEIGHTED CONTENT pull: consensus_adjacency applies relation_rank PER EDGE
        // from the BANKED law server-side (Σ relation_rank·eff_mu) — the rank authority is
        // READ from the substrate, never a hardcoded copy in the app.
        var adjacency = FoundryExport.Normalize(
            await FoundryExport.ReadAdjacencyAsync(ds, tokenSlots, degreeCap));
        Console.WriteLine($"  rank-weighted adjacency read: {adjacency.Nnz:N0} content edges (banked relation_rank)");

        // ── generate the basis: LE over the union graph, GSO, Procrustes-anchored
        // to token content coordinates, deterministic capacity fill, bias channel ──
        var anchors = new double[vocab][];
        foreach (var t in tokens)
        {
            if (t.TokenId < 0 || t.TokenId >= vocab || !t.HasContentCoord) continue;
            anchors[t.TokenId] = [t.ContentX, t.ContentY, t.ContentZ, t.ContentM];
        }
        var basisSeed = Hash128.Blake3(recipe.CanonicalJson);

        // METRIC-HEAD attention (destroyed box): a head is a NAMED substrate metric between
        // token trajectories, not a learned pattern. LAPLACE_FOUNDRY_ATTN_METRIC ∈
        // frechet|hausdorff|angular replaces the associative consensus band with the geometric
        // metric — laplace.metric_edges computes each token's metric neighbours. The metric
        // affinity is GEOMETRIC (trajectory shape), orthogonal to the consensus planes, so it
        // must be IN the basis E or no linear q/k projection can reproduce it (measured: 4%
        // recall when E is consensus-only). It therefore joins the union graph that builds E AND
        // is the attention rank that gets projected+factored. Unset → associative consensus (default).
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
            // S³ IS THE RIGID FRAME: carry every token's native super-Fib coordinate verbatim
            // (physicalities.coord) and place it directly in the basis (no LE, no Procrustes fit).
            int coordFilled = await FoundryExport.FillCoordAnchorsAsync(ds, tokenSlots, anchors);
            if (Environment.GetEnvironmentVariable("LAPLACE_FOUNDRY_COORD_DIRECT") is null)
                Environment.SetEnvironmentVariable("LAPLACE_FOUNDRY_COORD_DIRECT", "1");
            if (Environment.GetEnvironmentVariable("LAPLACE_FOUNDRY_COORD_SCALE") is null)
                Environment.SetEnvironmentVariable("LAPLACE_FOUNDRY_COORD_SCALE", "20");   // frame dominates the normalized stream → coord dims ≈ unit S³
            Console.WriteLine($"  S³ frame: {coordFilled:N0} tokens placed at their native coordinate verbatim (no LE/Procrustes)");
        }
        // PURE S³ FRAME (no metric head needed): COORD_ONLY makes the native super-Fibonacci
        // coordinate the WHOLE embedding — BuildBasis then skips the Lanczos eigensolve entirely.
        // Fill anchors verbatim from physicalities.coord so every token lands at its real S³ point.
        if (FoundryExport.EnvInt("LAPLACE_FOUNDRY_COORD_ONLY", 0) != 0 && attnMetric == "")
        {
            int coordOnlyFilled = await FoundryExport.FillCoordAnchorsAsync(ds, tokenSlots, anchors);
            Console.WriteLine($"  S³ COORD-ONLY: {coordOnlyFilled:N0} tokens placed at native coordinate (NO Lanczos eigensolve)");
        }
        var swBasis = Stopwatch.StartNew();
        // The metric edges (~12k) are outnumbered ~3:1 by the consensus edges in the affinity
        // SVD, so at equal weight E barely spans the metric geometry (measured: 14% recall).
        // Up-weight the metric plane into the BASIS union so it claims enough SVD energy for
        // q/k to select it (the attention rank it factors stays unscaled — this only shapes E).
        double metricBasisGain = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_METRIC_BASIS_GAIN", 4.0);
        var metricForBasis = attnMetric != ""
            ? attnPlane with { Vals = Array.ConvertAll(attnPlane.Vals, v => v * metricBasisGain) }
            : attnPlane;
        var unionGraph = attnMetric != ""
            ? FoundryExport.Union(sim, rel, pre, att, metricForBasis)
            : FoundryExport.Union(sim, rel, pre, att);
        // AFFINITY-SVD basis (token = SVD of its relational row) carries the consensus geometry
        // far better than Laplacian-eigenmaps (measured +3.0σ vs +0.58σ → conditioning works).
        // It is a dense vocab×vocab SVD, so default it ON for modest vocab and fall back to LE
        // above the size where dense SVD is impractical. Env forces: 1=on, 0=off.
        string? affRaw = Environment.GetEnvironmentVariable("LAPLACE_FOUNDRY_EMBED_AFFINITY");
        // A metric head needs the native S³ coordinate IN the basis; BuildBasisAffinity ignores
        // anchors, so force the LE-path builder (which honours coord-direct) when one is active.
        double[] E;
        FoundryExport.BasisStats basisStats;
        if (FoundryExport.EnvInt("LAPLACE_FOUNDRY_COORD_ONLY", 0) != 0)
        {
            // EXACT S³ EMBED — no LE, no GSO, no Procrustes, no Lanczos, no SVD. The embedding IS
            // the verbatim super-Fibonacci/DUCET/Hopf coordinate (physicalities.coord, filled into
            // `anchors`): the 4 S³ dims placed directly into the hidden space, remaining dims zero.
            // The anchor is the basis; nothing is generated or fit, so nothing can NaN.
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

        // ── project the operators through the basis and factor them at mold ranks ─
        double relTol = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_REL_ERR_TOL", 0.0);
        int kAttn = Math.Min(kvDimR, dModel);
        int kFfn  = Math.Min(intermR, dModel);
        // PER-LAYER-DISTINCT operators: depth traverses the relation RANKS instead of
        // power-iterating one operator to its dominant eigenvector (which collapsed every
        // prompt to the same hubs regardless of input). Each layer applies a different
        // rank's consensus — associative (co-occurrence), taxonomic+partitive (what-it-is/
        // made-of), causal+sequence (what-follows), equivalence (sameness) — so the model
        // is a walk down the rank ladder, not M^L. Each is projected through the basis and
        // factored at the mold ranks; OV/FFN compose outer·inner (factor Mᵀ), attention qᵀk.
        // FFN/completion = the folded causal band UNION the EXACT trajectory continuation. The
        // folded causal band is near-empty over a focused vocab (here pre.Nnz=0); `traj` (read
        // above, then previously DISCARDED) is the real "what follows" signal off the LineStrings.
        // Without it the FFN operator is empty and every prompt collapses to one generic token.
        var completion = FoundryExport.Normalize(FoundryExport.Union(pre, traj));
        var rankPlanes = new[] { attnPlane, rel, completion, sim };
        var rankNames  = new[] { attnMetric != "" ? $"metric:{attnMetric}" : "associative", "taxo+part", "causal+seq", "equivalence" };
        int nOps = rankPlanes.Length;
        var fOvR   = new FoundryExport.Factors[nOps];   // transpose:true,  rank kAttn  (V/O)
        var fFfnR  = new FoundryExport.Factors[nOps];   // transpose:true,  rank kFfn   (FFN)
        var fAttnR = new FoundryExport.Factors[nOps];   // transpose:false, rank kAttn  (Q/K)
        var swOps = Stopwatch.StartNew();
        for (int r = 0; r < nOps; r++)
        {
            var m = FoundryExport.ProjectOperator(E, vocab, dModel, rankPlanes[r]);
            // RESIDUAL CANCELLATION (logical, from the layer math, not a tuned knob): a llama
            // sublayer computes h += op(h), so a raw operator M nets to (I+M) per layer and
            // (I+M)^L over depth — the I re-adds the input (the echo) and (I+M)^L power-iterates
            // to M's dominant eigenvector (the hub/collapse). The residual already supplies I, so
            // the residual-path operators (V/O, FFN) must be M − I: then h += (M−I)h = M·h, the
            // pure operator, and depth becomes the product M_L···M_1 of the per-layer-distinct
            // ranks — no echo, no (I+M)^L. Attention (Q/K scores) is not a residual-add of M·h,
            // so it keeps the raw operator.
            var mResid = (double[])m.Clone();
            for (int d = 0; d < dModel; d++) mResid[(long)d * dModel + d] -= 1.0;
            fOvR[r]   = FoundryExport.Factor(mResid, dModel, kAttn, relTol, transpose: true);
            fFfnR[r]  = FoundryExport.Factor(mResid, dModel, kFfn,  relTol, transpose: true);
            fAttnR[r] = FoundryExport.Factor(m,      dModel, kAttn, relTol, transpose: false);
        }
        if (attnMetric != "")
            FoundryExport.ReportMetricHeadFidelity(E, vocab, dModel, attnPlane, fAttnR[0], attnMetric);

        // FULL RANK-WEIGHTED CONSENSUS UNEMBEDDING (the substrate's whole affinity read out,
        // not one plane, not the echo). With lm_head == embed, logits = E·h peaks on the INPUT
        // token (self dot=1: "dog"→dog,caninize). Reading out PRECEDES only is the other error
        // — a word's continuation is its ENTIRE rank-weighted relational neighborhood: what it
        // IS / relates to / is part of (sim,rel), what FOLLOWS / completes it (pre), what it
        // co-occurs / associates with (att). Build
        //   lm_head[Y,:] = Σ_R wRank(R) · Σ_X A_R[X,Y] · E[X,:]
        // over every role plane R weighted by its rank authority. Then
        //   logits[Y] = lm_head[Y]·h ≈ Σ_R wRank(R)·A_R[lastCtx,Y]
        // = the full rank-weighted consensus affinity of Y to the context — the rating looked
        // up, every relation contributing by its authority. Content dims only; bias zeroed.
        var lmHead = new double[(long)vocab * dModel];
        {
            int dC = dModel - 1;
            var inDeg = new double[vocab];   // Σ_R wRank·Σ_X A_R[X,Y] : Y's full marginal pull
            // role plane ⇒ rank authority (the rank header weights the operators already use):
            // equivalence/standards 0.91, taxonomic+partitive 0.82, causal+seq 0.64, assoc 0.36.
            // TWO DISTINCT POURS, not a blend: an lm_head that reads SIMILARITY (the rated
            // adjacency — what is LIKE the context) is an EMBEDDING model; an lm_head that
            // reads CONTINUATION (the trajectory order — what FOLLOWS the context in witnessed
            // sentences) is a GENERATIVE / CHAT model. The substrate computes both channels;
            // the pour selects one. Generative (default) reads the order channel so
            // autoregressive decode FLOWS (walk_text transcribed) instead of returning neighbors.
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
            // PMI / IDF marginal correction: a raw bigram readout predicts whatever is
            // preceded by EVERYTHING — the function words (but/or/the), regardless of context
            // (measured: "the dog" → upon,but,or,then,when...). Divide each target row by
            // √(its marginal) so the logit reflects P(Y|X) lift over P(Y), surfacing the
            // context-specific follower instead of the global marginal. This is the standard
            // PMI statistic, not a tuned scalar. Bias dim zeroed (constant carries no signal).
            for (int v = 0; v < vocab; v++)
            {
                long off = (long)v * dModel;
                double idf = 1.0 / (inDeg[v] + 1.0);   // full marginal division = conditional P(X→Y)/P(Y)
                for (int c = 0; c < dC; c++) lmHead[off + c] *= idf;
                lmHead[off + dC] = 0.0;
            }
            // READOUT = SPACE-LED WORD CONTINUATIONS ONLY. byte tokens (<0xXX>) and the bare
            // (no-leading-space) dual-form aliases are valid INPUTS but must never be emitted:
            // a byte-level-BPE detokenizer concatenates a bare piece with no space, so emitting
            // them runs words together ("on"+"the"+"y"+"of" → "ontheyof"). The ▁/leading-space
            // form is the output form. Zero the others' lm_head rows (mirrors the n-gram readouts'
            // suppression, which the default operator/basis path was missing).
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
            // global scale so the average row norm ≈ 1 (sane logit magnitudes).
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

        // Every layer carries the same projected operators via a uniform residual split
        // (1/√L), so the prompt embedding survives the depth while operator structure accrues.
        // Gains calibrate the spectrally-normalized operators against the residual stream
        // (both halves of a composed pair carry the scale, so the operator gets gain²).
        double attnGainEnv  = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_ATTN_GAIN", 1.0);
        double residGainEnv = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_RESID_GAIN", 1.0);
        // The gate reads only the bias channel; after RMSNorm the bias component is
        // ≈ √(d/2), so gateCol·√(d/2) = GateZ lands SiLU in its near-linear region,
        // and the up factor is pre-divided by SiLU(GateZ) to neutralize the gain.
        double gateZ = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_GATE_Z", 6.0);
        double gateCol = gateZ / Math.Sqrt(dModel / 2.0);
        double upGain = 1.0 / FoundryExport.Silu(gateZ);

        // Cast the gguf at one (attn, resid) gain pair. The gains scale the spectrally-
        // normalized operators against the residual stream: too low → the readout echoes
        // the prompt's last token; too high → the operator collapses the representation.
        // The basis + factored operators above are computed ONCE; this only re-fills and
        // writes, so a sweep (LAPLACE_FOUNDRY_GAIN_SWEEP="a:r,a:r,…") finds the sweet spot
        // in one pour instead of one pour per gain.
        int WriteCast(string outPath, double aGain, double rGain)
        {
            // Per-factor split: each composed operator (qᵀk, Wo·Wv, Wdown·Wup) is the
            // PRODUCT of two filled factors, so a per-factor scale s yields a composed
            // magnitude s². The documented "uniform residual split (1/√L)" is a statement
            // about the COMPOSED operator's contribution to the residual stream — so each
            // factor must carry L^(-1/4), NOT L^(-1/2). At L^(-1/2) per factor the composed
            // split was 1/L: a √L undercount (~4.7× too weak at L=22) that collapses operator
            // structure into the prompt embedding (weak completion) and flattens attention
            // scores toward uniform (mean-pooling). gain is squared by the pair too, so the
            // composed operator scales as gain²/√L.
            double split = Math.Pow(Math.Max(1, nLayers), -0.25);
            double attnScale  = aGain * split;
            double layerScale = rGain * split;
            var gguf = SynthInterop.GgufWriterCreate(outPath);
            if (gguf == IntPtr.Zero) { Console.WriteLine($"  gguf_writer_create failed for {outPath}"); return 2; }
            WriteGgufMetadata(gguf, recipe, tokens, modelDir, byteBpe: true);   // word vocab → byte-level BPE (ignore_merges)
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
                    dtype = 0;   // F32 only — 14900KS has AVX-512 fused off; bf16 has no fast path and crushes the ~0.02 embed deltas
                }
                long nElem = (long)rows * (long)Math.Max(1UL, cols);
                var vals = new float[nElem];
                int tr = (int)rows, tc = (int)Math.Max(1UL, cols);

                // The mold is filled ENTIRELY from generated material. Any manifest name
                // the foundry does not define is a hard error — never a zero-fill.
                if (name is "model.embed_tokens.weight" or "lm_head.weight")
                {
                    // embed = E (input geometry); lm_head = completion-composed unembedding
                    // (M_pre·embed), so the readout predicts what FOLLOWS, not the input token.
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
                    // Depth is a GENERATION progression, not an arbitrary cycle. The readout is
                    // E·(final residual); since lm_head == embed, an equivalence (synonym/self)
                    // transform last makes logits peak on the INPUT token and its morphology
                    // (measured: "dog"→dog,caninize,caninekind — pure echo). The next-token
                    // signal is the CAUSAL/COMPLETION rank (PRECEDES); the depth probe showed the
                    // right continuation surfaces exactly when it is applied (canine 2001→37) and
                    // gets re-buried when equivalence runs after it. So LAND completion on the
                    // final layer (the readout transform reads "what follows", not "what it is").
                    // rank index: 0=associative 1=taxo+part 2=causal+seq(completion) 3=equivalence
                    const int RANK_COMPLETION = 2, RANK_ASSOC = 0, RANK_TAXO = 1;
                    int last = nLayers - 1;
                    int aIdx, fIdx;
                    if (layerIdx == last)   { aIdx = RANK_COMPLETION; fIdx = RANK_COMPLETION; }
                    else if (layerIdx == 0) { aIdx = RANK_ASSOC;      fIdx = RANK_TAXO; }
                    else                    { aIdx = layerIdx % nOps; fIdx = (layerIdx + 1) % nOps; }
                    var fAttn = fAttnR[aIdx];
                    var fOv   = fOvR[aIdx];
                    var fFfn  = fFfnR[fIdx];
                    // A layer on the metric rank → DIRECT rigid-frame (coord) head: q·k = cos on S³
                    // (the angular metric) by SELECTING the coordinate dims, not the SVD-factored
                    // operator that collapses to the noise floor (8% vs 100%).
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
            Console.WriteLine($"synthesis complete: {outPath} ({fsz / 1048576.0:F0} MB, attn={aGain} resid={rGain}) in {sw.Elapsed.TotalSeconds:F1}s");
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

    // FAITHFUL generative cast: the attestations transcribed into the container, no learning,
    // no gains, no rank bands. The rank-weighted rated adjacency A[X,Y] (read whole from
    // consensus_adjacency — the rank looked up PER EDGE from the banked law server-side) is
    // factored by truncated SVD to rank=dim: embed = U√S, lm_head = V√S. In the cast,
    //   logits[Y|X] = lm_head[Y]·embed[X] = Σ_k U[X,k]·S_k·V[Y,k] = A_dim[X,Y]
    // — the rank-weighted rating LOOKED UP, factored to the hidden width. dim is a NORMAL
    // embedding size, NOT vocab (a 32k-token model is 32000×dim). Intermediate layers are
    // no-ops, norms = 1. This is last-token (bigram) order; carrying context across the prompt
    // is a later layer of work, and the model says so rather than pretend.
    private static async Task<int> WriteFaithfulGgufAsync(
        NpgsqlDataSource ds,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Dictionary<Hash128, List<int>> tokenSlots,
        int vocab, int dModel, string modelDir, string outputPath,
        TensorSpec[] specs, int tensorCount, bool grapheme = false)
    {
        int cap = FoundryExport.EnvInt("LAPLACE_FOUNDRY_FAITHFUL_CAP", 128);
        var sw = Stopwatch.StartNew();
        // grapheme floor reads the letter-bigram order off the trajectories. The word model has
        // two readout sources (the LAYERING LAW: severity/eff_mu is not value):
        //   default     → consensus_adjacency: ALL in-vocab content edges, rank-weighted. Includes
        //                 the PRECEDES order glue (king→of/was/and) which, being a first-order
        //                 frequency witness, HUBS on function words under greedy decode.
        //   --knowledge → consensus_layer_plane gated to the CONTENT rank band [0.55,0.85]: IS_A
        //                 (king→monarch), HAS_PROPERTY, IS_SYNONYM_OF — ABOVE the 0.36 glue/metadata
        //                 bucket (PRECEDES, HAS_DOMAIN_TOPIC, HAS_EXAMPLE). The substrate's own
        //                 relation_rank does the de-hubbing the flat readout threw away.
        bool knowledge = !grapheme && FoundryExport.EnvInt("LAPLACE_FOUNDRY_KNOWLEDGE", 0) != 0;
        double rkLo = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_RANK_LO", 0.55);
        double rkHi = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_RANK_HI", 0.85);
        // TRAJORDER: the EXACT readout — no folded PRECEDES glue, no flat hub. The rated KNOWLEDGE
        // band (IS_A/property/synonym, rank∈[lo,hi]) UNION the EXACT trajectory continuation
        // (P(next word | word) read straight off the witnessed sentence trajectory LineStrings via
        // word_order, NOT the lossy single-hop folded PRECEDES band that hubs on function words).
        // Both normalized to comparable scale, then merged. This is the order signal the function-
        // word loop was missing; it lives in the trajectories, not in type=PRECEDES.
        bool trajOrder = !grapheme && FoundryExport.EnvInt("LAPLACE_FOUNDRY_TRAJORDER", 0) != 0;
        FoundryExport.PlaneCoo A;
        if (grapheme)
            A = await FoundryExport.ReadGraphemeOrderAsync(ds, tokenSlots);
        else if (trajOrder)
        {
            var know  = FoundryExport.Normalize(await FoundryExport.ReadLayerPlaneAsync(ds, rkLo, rkHi, tokenSlots, cap));
            var order = FoundryExport.Normalize(await FoundryExport.ReadWordOrderAsync(
                ds, tokenSlots, 1, FoundryExport.EnvInt("LAPLACE_FOUNDRY_WORD_TRAJS", 400000), cap));
            A = FoundryExport.Union(know, order);
            Console.WriteLine($"  TRAJORDER readout: knowledge band rank∈[{rkLo},{rkHi}] ∪ exact trajectory continuation "
                + $"(word_order off LineStrings; {know.Nnz:N0} knowledge + {order.Nnz:N0} order edges)");
        }
        else if (knowledge)
        {
            A = await FoundryExport.ReadLayerPlaneAsync(ds, rkLo, rkHi, tokenSlots, cap);
            Console.WriteLine($"  KNOWLEDGE readout: consensus content band rank∈[{rkLo},{rkHi}] (IS_A/property/synonym, no PRECEDES glue)");
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

        // factor A → embed (U) + lm_head (V·S) at rank dModel (the hidden width, not vocab).
        // conditional=true (log-odds of the continuation P(Y|X)) for BOTH grapheme and word: it is
        // the GENERATIVE readout. PPMI is a SIMILARITY transform that inflates rare next-tokens into
        // a hub — wrong for next-token decode (it is what made the word cast collapse to byte garbage
        // while the grapheme cast, already conditional, generated).
        var swSvd = Stopwatch.StartNew();
        // conditional log-odds (P(Y|X)) for grapheme + word-default; PMI / global-prior-subtraction
        // (log P(Y|X) − log P(Y)) for the knowledge readout. The plain log-odds matrix has a dominant
        // Perron-Frobenius top singular direction (same sign for every token) that acts as a global
        // bias; bf16 rounding in the GGUF erases the smaller differentiating terms, collapsing every
        // input to that one hub direction in llama.cpp. PMI subtracts log P(Y), removing exactly that
        // global-frequency hub so the input-specific signal survives (with byte/content suppression
        // already taming PMI's rare-token inflation).
        FoundryExport.FactorAdjacency(A, vocab, dModel, out var embed, out var lmHead, out int usedRank, conditional: true, suppressSelf: !grapheme);
        Console.WriteLine($"  FAITHFUL factorization in {swSvd.Elapsed.TotalSeconds:F1}s: "
            + $"log-odds-SVD rank {usedRank}/{dModel} → embed=U, lm_head=V·S, no-op layers");

        // SUBSTRATE-NATIVE LOOKUP (knowledge, dModel≥vocab): the attestation lookup IS the forward
        // pass — embed = I, lm_head[Y][X] = log-odds(X→Y). Then logits[Y] = lm_head[Y]·RMSNorm(e_X)
        // = log-odds(X→Y) EXACTLY. NO SVD, so no Perron-Frobenius global-frequency hub (the and/that/as
        // direction the SVD injected). This is the design the substrate states outright: "lm_head=A,
        // embed=I; the GEMM literally is the lookup." Grounded in the source material (a knowledge
        // graph), not in fluent-LM priors. Falls back to SVD if dModel<vocab (identity impossible).
        bool lookup = knowledge && FoundryExport.EnvInt("LAPLACE_FOUNDRY_LOOKUP", 1) != 0 && dModel >= vocab;
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
        else if (knowledge)
            Console.WriteLine($"  (lookup needs dModel≥vocab; dModel={dModel} vocab={vocab} → SVD fallback, may re-introduce the hub)");

        // A token that nothing precedes (no incoming edge) cannot be a next-token. Such tokens —
        // the 256 byte floor, specials, off-graph words — have all-zero adjacency rows, so the
        // truncated SVD assigns them ARBITRARY null-space directions in lm_head that can win the
        // argmax (king→GJjJj, water→RRRR). Zero their lm_head row so the readout can only emit
        // witnessed continuations. (embed is left intact — they still tokenize as inputs.)
        {
            var isContinuation = new bool[vocab];
            foreach (var y in A.Cols) if (y >= 0 && y < vocab) isContinuation[y] = true;
            // For the WORD model, byte/punctuation tokens are valid INPUTS but must never be a
            // PREDICTED next-token: they are continuation HUBS (<0x49>='I', <0x2E>='.') that
            // collapse the readout to one direction in llama.cpp. Suppress them; word tokens only.
            var isByte = new bool[vocab];
            if (!grapheme) foreach (var t in tokens) if (t.TokenId >= 0 && t.TokenId < vocab && t.IsByteLevel) isByte[t.TokenId] = true;
            int suppressed = 0;
            for (int y = 0; y < vocab; y++)
                if (!isContinuation[y] || isByte[y]) { for (int c = 0; c < dModel; c++) lmHead[(long)y * dModel + c] = 0; suppressed++; }
            Console.WriteLine($"  suppressed {suppressed:N0}/{vocab:N0} non-continuation + byte tokens from lm_head (word continuations only)");
        }

        // ── ACCEPTANCE GATE (in-process, BEFORE the cast) ───────────────────────────────────
        // The cast's logits[Y|X] rank EXACTLY as lm_head[Y]·embed[X] (the no-op layers pass the
        // embedding through; the final RMSNorm is a positive per-X scalar that cannot change the
        // argmax/order over Y). So we can SCORE the model here, without llama.cpp: for each probe
        // token X, the reconstructed top continuations must reproduce the substrate's own raw
        // rated adjacency A[X,·] (the ground truth dog→teeth/animal). Overlap is the gate.
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

        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero) return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens, modelDir, byteBpe: !grapheme);   // word → byte-level BPE; grapheme → SPM
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
                dtype = 0;   // F32 only — 14900KS has AVX-512 fused off; bf16 has no fast path and crushes the ~0.02 embed deltas
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
        long fsz = new FileInfo(outputPath).Length;
        Console.WriteLine($"FAITHFUL synthesis complete: {outputPath} ({fsz / 1048576.0:F0} MB) in {swW.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    // REPETITION-HEAD word model: head 0 = the proven RoPE shift (flow trigram); head 1 = a baked-in
    // REPETITION PENALTY. Head 1 attends UNIFORMLY over the causal window (q=k=0 → flat softmax →
    // mean of values), averaging each token's random unit IDENTITY code; the lm_head subtracts
    // repGain·(code_Y · mean) ≈ repGain·(how often Y appears in recent context). A token just emitted
    // is down-weighted, so "get back get back" breaks WITHOUT a decode-time penalty — the cure the
    // de-hub knob could only shift. Bands (r=(d-1)/5): B0 bigram, B1 skip-readout, B2 skip-source,
    // B3 identity-source, B4 recent-readout; [d-1]=const. 2 heads → head_dim=d/2.
    private static async Task<int> WriteWordRepGgufAsync(
        NpgsqlDataSource ds, LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Dictionary<Hash128, List<int>> tokenSlots,
        int vocab, int dModel, string modelDir, string outputPath,
        TensorSpec[] specs, int tensorCount)
    {
        int nHeads = Math.Max(2, recipe.NumHeads), headDim = dModel / nHeads;
        double ropeTheta = recipe.RopeTheta > 0 ? recipe.RopeTheta : 10000.0;
        int trajN = FoundryExport.EnvInt("LAPLACE_FOUNDRY_WORD_TRAJS", 400000);
        double dehub = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_DEHUB", 0.3);
        double repGain = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_REP_GAIN", 6.0);
        int r = (dModel - 1) / 5;
        int CONST = dModel - 1;

        var sw = Stopwatch.StartNew();
        var A1 = await FoundryExport.ReadWordOrderAsync(ds, tokenSlots, 1, trajN);
        var A2 = await FoundryExport.ReadWordOrderAsync(ds, tokenSlots, 2, trajN);
        if (A1.Nnz == 0) return Fail("word_order gap1 returned nothing — ingest text first");
        FoundryExport.FactorAdjacency(A1, vocab, r, out var embB, out var lmB, out _, conditional: true, suppressSelf: true, dehub: dehub);
        FoundryExport.FactorAdjacency(A2, vocab, r, out var embS, out var lmS, out _, conditional: true, suppressSelf: true, dehub: dehub);
        Console.WriteLine($"  rep-head word n-gram: bigram {A1.Nnz:N0}, skip {A2.Nnz:N0} edges, r={r}, repGain={repGain}, dehub={dehub}, in {sw.Elapsed.TotalSeconds:F1}s");

        // deterministic unit identity code per token (Box-Muller on a SplitMix64 stream)
        var code = new double[(long)vocab * r];
        ulong st = 0xD1CE5EEDUL;
        double NextG()
        {
            st += 0x9E3779B97F4A7C15UL; ulong z = st;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; z ^= z >> 31;
            st += 0x9E3779B97F4A7C15UL; ulong z2 = st;
            z2 = (z2 ^ (z2 >> 30)) * 0xBF58476D1CE4E5B9UL; z2 = (z2 ^ (z2 >> 27)) * 0x94D049BB133111EBUL; z2 ^= z2 >> 31;
            double u1 = ((z >> 11) + 1.0) / (9007199254740992.0 + 1.0), u2 = (z2 >> 11) / 9007199254740992.0;
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
        for (int v = 0; v < vocab; v++)
        {
            long oc = (long)v * r; double n2 = 0;
            for (int i = 0; i < r; i++) { double g = NextG(); code[oc + i] = g; n2 += g * g; }
            double inv = n2 > 1e-9 ? 1.0 / Math.Sqrt(n2) : 0.0;
            for (int i = 0; i < r; i++) code[oc + i] *= inv;
        }

        double constVal = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_SHIFT_CONST", 10.0);
        var embed  = new double[(long)vocab * dModel];
        var lmHead = new double[(long)vocab * dModel];
        for (int v = 0; v < vocab; v++)
        {
            long o = (long)v * dModel, oc = (long)v * r;
            for (int i = 0; i < r; i++)
            {
                embed[o + i]        = embB[oc + i];               // B0 cur bigram
                lmHead[o + i]       = lmB[oc + i];                // B0 bigram readout
                lmHead[o + r + i]   = lmS[oc + i];                // B1 skip readout (prev, via head 0)
                embed[o + 2 * r + i]= embS[oc + i];               // B2 skip source
                embed[o + 3 * r + i]= code[oc + i];               // B3 identity source
                lmHead[o + 4 * r + i] = -repGain * code[oc + i];  // B4 recent-suppression readout
            }
            embed[o + CONST] = constVal;
        }
        var isCont = new bool[vocab];
        foreach (var y in A1.Cols) if (y >= 0 && y < vocab) isCont[y] = true;
        var isByte = new bool[vocab]; var isLead = new bool[vocab];
        foreach (var t in tokens) if (t.TokenId >= 0 && t.TokenId < vocab)
        { if (t.IsByteLevel) isByte[t.TokenId] = true; if (t.Role.HasFlag(TokenRole.LeadingSpace)) isLead[t.TokenId] = true; }
        int supp = 0;
        for (int y = 0; y < vocab; y++)
            if (!isCont[y] || isByte[y] || !isLead[y])
            { long o = (long)y * dModel; for (int c = 0; c < dModel; c++) lmHead[o + c] = 0; lmHead[o + CONST] = -100.0; supp++; }
        Console.WriteLine($"  suppressed {supp:N0}/{vocab:N0} byte + non-continuation + bare-alias tokens");

        double scale = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_SHIFT_SCALE", 4.0);
        int npairs = headDim / 2;
        var qbase = new double[headDim]; var kbase = new double[headDim];
        for (int f = 0; f < npairs; f++)
        { double thetaF = Math.Pow(ropeTheta, -2.0 * f / headDim), s = scale / Math.Sqrt(npairs);
          kbase[f] = s; kbase[f + npairs] = 0; qbase[f] = s * Math.Cos(thetaF); qbase[f + npairs] = -s * Math.Sin(thetaF); }

        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero) return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens, modelDir, byteBpe: true);
        for (int i = 0; i < tensorCount; i++)
        {
            string name; ulong rows, cols;
            unsafe { var sp = specs[i]; name = Marshal.PtrToStringUTF8((IntPtr)sp.Name) ?? ""; rows = sp.Rank >= 1 ? sp.Shape[0] : 1; cols = sp.Rank >= 2 ? sp.Shape[1] : 1; }
            int tr = (int)rows, tc = (int)Math.Max(1UL, cols);
            var vals = new float[(long)tr * tc];
            if (name == "model.embed_tokens.weight")
                for (int v = 0; v < tr; v++) for (int c = 0; c < tc; c++) vals[(long)v * tc + c] = (float)embed[(long)v * dModel + c];
            else if (name == "lm_head.weight")
                for (int v = 0; v < tr; v++) for (int c = 0; c < tc; c++) vals[(long)v * tc + c] = (float)lmHead[(long)v * dModel + c];
            else if (name == "model.norm.weight" || name.EndsWith("input_layernorm.weight", StringComparison.Ordinal) || name.EndsWith("post_attention_layernorm.weight", StringComparison.Ordinal))
                Array.Fill(vals, 1.0f);
            else if (name.EndsWith("self_attn.q_proj.weight", StringComparison.Ordinal))
                for (int hd = 0; hd < headDim && hd < tr; hd++) vals[(long)hd * tc + CONST] = (float)qbase[hd];   // head 0 shift; head 1 stays 0 → uniform
            else if (name.EndsWith("self_attn.k_proj.weight", StringComparison.Ordinal))
                for (int hd = 0; hd < headDim && hd < tr; hd++) vals[(long)hd * tc + CONST] = (float)kbase[hd];
            else if (name.EndsWith("self_attn.v_proj.weight", StringComparison.Ordinal))
            {   // head 0 v[j]=h[B2+j] (skip source); head 1 v[headDim+j]=h[B3+j] (identity)
                for (int j = 0; j < r && j < tr; j++) vals[(long)j * tc + (2 * r + j)] = 1.0f;
                for (int j = 0; j < r && (headDim + j) < tr; j++) vals[(long)(headDim + j) * tc + (3 * r + j)] = 1.0f;
            }
            else if (name.EndsWith("self_attn.o_proj.weight", StringComparison.Ordinal))
            {   // head 0 o[j]→resid[B1+j] (skip readout); head 1 o[headDim+j]→resid[B4+j] (recent mean)
                for (int j = 0; j < r && (r + j) < tr; j++) vals[(long)(r + j) * tc + j] = 1.0f;
                for (int j = 0; j < r && (4 * r + j) < tr; j++) vals[(long)(4 * r + j) * tc + (headDim + j)] = 1.0f;
            }
            else if (name.StartsWith("model.layers.", StringComparison.Ordinal)) { }
            else { Console.WriteLine($"  rep n-gram does not define '{name}'"); SynthInterop.GgufWriterFree(gguf); return 3; }
            byte[] tb = FoundryExport.ToF32Bytes(vals);
            nuint[] dims = cols > 1 ? new nuint[] { (nuint)cols, (nuint)rows } : new nuint[] { (nuint)rows };
            unsafe { fixed (nuint* dp = dims) fixed (byte* bp = tb) SynthInterop.GgufWriterAddTensor(gguf, HfToGgmlName(name), 0, dp, (nuint)dims.Length, bp); }
        }
        int rcw = SynthInterop.GgufWriterFinalize(gguf); SynthInterop.GgufWriterFree(gguf);
        if (rcw != 0) return Fail($"gguf finalize failed rc={rcw}");
        Console.WriteLine($"REP synthesis complete: {outputPath} ({new FileInfo(outputPath).Length / 1048576.0:F0} MB, 2 heads: shift+repetition, repGain={repGain}, r={r})");
        return 0;
    }

    // CONTENT cast: the proven REPHEAD machinery (RoPE shift head 0 + uniform repetition-penalty
    // head 1 + space-led suppression for readable detokenization) but the bigram readout A1 is the
    // KNOWLEDGE band (rank∈[lo,hi] — IS_A/RelatedTo/synonym content) UNION the trajectory order, so
    // the readout surfaces CONTENT (king→monarch, captain→chieftain) instead of word-order function
    // words. A STRONG de-hub (subtract λ·log P(Y) in FactorAdjacency) suppresses BOTH the function-
    // word hubs (the/of/and) AND the ConceptNet ontology-root hubs (concept/abstraction) — the
    // over-general IsA chain that collapsed the plain knowledge readout to "…concept abstraction".
    // Both hubs are high-in-degree Y, so one λ knob de-hubs both. Skip channel (A2) stays pure order.
    private static async Task<int> WriteWordContentGgufAsync(
        NpgsqlDataSource ds, LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Dictionary<Hash128, List<int>> tokenSlots,
        int vocab, int dModel, string modelDir, string outputPath,
        TensorSpec[] specs, int tensorCount)
    {
        int nHeads = Math.Max(2, recipe.NumHeads), headDim = dModel / nHeads;
        double ropeTheta = recipe.RopeTheta > 0 ? recipe.RopeTheta : 10000.0;
        int trajN = FoundryExport.EnvInt("LAPLACE_FOUNDRY_WORD_TRAJS", 400000);
        double dehub = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_DEHUB", 0.6);   // calibrated: keeps king→monarch, whale→mammal→animal; kills ontology-root hubs
        double repGain = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_REP_GAIN", 6.0);
        double rkLo = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_RANK_LO", 0.55);
        double rkHi = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_RANK_HI", 0.85);
        int r = (dModel - 1) / 5;
        int CONST = dModel - 1;

        var swR = Stopwatch.StartNew();
        var order1 = FoundryExport.Normalize(await FoundryExport.ReadWordOrderAsync(ds, tokenSlots, 1, trajN));
        var know   = FoundryExport.Normalize(await FoundryExport.ReadLayerPlaneAsync(ds, rkLo, rkHi, tokenSlots, 64));
        var order2 = await FoundryExport.ReadWordOrderAsync(ds, tokenSlots, 2, trajN);
        if (order1.Nnz == 0) return Fail("order channel empty — ingest text first");
        // MULTI-CHANNEL B0 readout: logit(Y|cur) = order(cur→Y) + α·knowledge(cur→Y). Factoring
        // order ∪ knowledge as ONE matrix lets the denser order channel drown the sparser knowledge
        // (king→monarch never surfaced); making B0 PURE knowledge climbs the IsA chain (whale→mammal→
        // animal→…→object) instead of flowing. The fix: factor order and knowledge SEPARATELY at half
        // rank and CONCATENATE them in band B0, so the B0 dot product SUMS the two affinities — prose
        // FLOW (order) biased toward grounded CONTENT (knowledge, weight α). Skip (B1/B2 via the shift
        // head) and rep (B3/B4) machinery unchanged.
        int rh = r / 2;
        double alpha = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_KNOW_ALPHA", 2.0);
        double skipW = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_SKIP_W", 0.4);   // full-rank skip drowns the half-rank B0 grounding unless down-weighted
        FoundryExport.FactorAdjacency(order1, vocab, rh, out var embO, out var lmO, out _, conditional: true, suppressSelf: true, dehub: dehub);
        FoundryExport.FactorAdjacency(know.Nnz > 0 ? know : order1, vocab, rh, out var embK, out var lmK, out _, conditional: true, suppressSelf: true, dehub: dehub);
        FoundryExport.FactorAdjacency(order2, vocab, r, out var embS, out var lmS, out _, conditional: true, suppressSelf: true, dehub: dehub);
        Console.WriteLine($"  content cast (multi-channel B0): order {order1.Nnz:N0} + α·knowledge[{rkLo:F2},{rkHi:F2}] {know.Nnz:N0} (α={alpha}), skip {order2.Nnz:N0}, rh={rh}, dehub={dehub}, in {swR.Elapsed.TotalSeconds:F1}s");

        // deterministic unit identity code per token (Box-Muller on a SplitMix64 stream)
        var code = new double[(long)vocab * r];
        ulong st = 0xD1CE5EEDUL;
        double NextG()
        {
            st += 0x9E3779B97F4A7C15UL; ulong z = st;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; z ^= z >> 31;
            st += 0x9E3779B97F4A7C15UL; ulong z2 = st;
            z2 = (z2 ^ (z2 >> 30)) * 0xBF58476D1CE4E5B9UL; z2 = (z2 ^ (z2 >> 27)) * 0x94D049BB133111EBUL; z2 ^= z2 >> 31;
            double u1 = ((z >> 11) + 1.0) / (9007199254740992.0 + 1.0), u2 = (z2 >> 11) / 9007199254740992.0;
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
        for (int v = 0; v < vocab; v++)
        {
            long oc = (long)v * r; double n2 = 0;
            for (int i = 0; i < r; i++) { double g = NextG(); code[oc + i] = g; n2 += g * g; }
            double inv = n2 > 1e-9 ? 1.0 / Math.Sqrt(n2) : 0.0;
            for (int i = 0; i < r; i++) code[oc + i] *= inv;
        }

        double constVal = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_SHIFT_CONST", 10.0);
        var embed  = new double[(long)vocab * dModel];
        var lmHead = new double[(long)vocab * dModel];
        for (int v = 0; v < vocab; v++)
        {
            long o = (long)v * dModel, oc = (long)v * r, och = (long)v * rh;
            for (int i = 0; i < rh; i++)
            {
                embed[o + i]        = embO[och + i];              // B0a order source
                lmHead[o + i]       = lmO[och + i];               // B0a order readout (prose flow)
                embed[o + rh + i]   = embK[och + i];              // B0b knowledge source
                lmHead[o + rh + i]  = alpha * lmK[och + i];       // B0b knowledge readout (×α, grounding bias)
            }
            for (int i = 0; i < r; i++)
            {
                lmHead[o + r + i]   = skipW * lmS[oc + i];        // B1 skip readout (prev, via head 0; down-weighted so B0 grounding drives)
                embed[o + 2 * r + i]= embS[oc + i];               // B2 skip source
                embed[o + 3 * r + i]= code[oc + i];               // B3 identity source
                lmHead[o + 4 * r + i] = -repGain * code[oc + i];  // B4 recent-suppression readout
            }
            embed[o + CONST] = constVal;
        }
        var isCont = new bool[vocab];
        foreach (var y in order1.Cols) if (y >= 0 && y < vocab) isCont[y] = true;
        foreach (var y in know.Cols) if (y >= 0 && y < vocab) isCont[y] = true;
        var isByte = new bool[vocab]; var isLead = new bool[vocab];
        foreach (var t in tokens) if (t.TokenId >= 0 && t.TokenId < vocab)
        { if (t.IsByteLevel) isByte[t.TokenId] = true; if (t.Role.HasFlag(TokenRole.LeadingSpace)) isLead[t.TokenId] = true; }
        int supp = 0;
        for (int y = 0; y < vocab; y++)
            if (!isCont[y] || isByte[y] || !isLead[y])
            { long o = (long)y * dModel; for (int c = 0; c < dModel; c++) lmHead[o + c] = 0; lmHead[o + CONST] = -100.0; supp++; }
        Console.WriteLine($"  suppressed {supp:N0}/{vocab:N0} byte + non-continuation + bare-alias tokens (space-led words only)");

        double scale = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_SHIFT_SCALE", 4.0);
        int npairs = headDim / 2;
        var qbase = new double[headDim]; var kbase = new double[headDim];
        for (int f = 0; f < npairs; f++)
        { double thetaF = Math.Pow(ropeTheta, -2.0 * f / headDim), s = scale / Math.Sqrt(npairs);
          kbase[f] = s; kbase[f + npairs] = 0; qbase[f] = s * Math.Cos(thetaF); qbase[f + npairs] = -s * Math.Sin(thetaF); }

        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero) return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens, modelDir, byteBpe: true);
        for (int i = 0; i < tensorCount; i++)
        {
            string name; ulong rows, cols;
            unsafe { var sp = specs[i]; name = Marshal.PtrToStringUTF8((IntPtr)sp.Name) ?? ""; rows = sp.Rank >= 1 ? sp.Shape[0] : 1; cols = sp.Rank >= 2 ? sp.Shape[1] : 1; }
            int tr = (int)rows, tc = (int)Math.Max(1UL, cols);
            var vals = new float[(long)tr * tc];
            if (name == "model.embed_tokens.weight")
                for (int v = 0; v < tr; v++) for (int c = 0; c < tc; c++) vals[(long)v * tc + c] = (float)embed[(long)v * dModel + c];
            else if (name == "lm_head.weight")
                for (int v = 0; v < tr; v++) for (int c = 0; c < tc; c++) vals[(long)v * tc + c] = (float)lmHead[(long)v * dModel + c];
            else if (name == "model.norm.weight" || name.EndsWith("input_layernorm.weight", StringComparison.Ordinal) || name.EndsWith("post_attention_layernorm.weight", StringComparison.Ordinal))
                Array.Fill(vals, 1.0f);
            else if (name.EndsWith("self_attn.q_proj.weight", StringComparison.Ordinal))
                for (int hd = 0; hd < headDim && hd < tr; hd++) vals[(long)hd * tc + CONST] = (float)qbase[hd];
            else if (name.EndsWith("self_attn.k_proj.weight", StringComparison.Ordinal))
                for (int hd = 0; hd < headDim && hd < tr; hd++) vals[(long)hd * tc + CONST] = (float)kbase[hd];
            else if (name.EndsWith("self_attn.v_proj.weight", StringComparison.Ordinal))
            {
                for (int j = 0; j < r && j < tr; j++) vals[(long)j * tc + (2 * r + j)] = 1.0f;
                for (int j = 0; j < r && (headDim + j) < tr; j++) vals[(long)(headDim + j) * tc + (3 * r + j)] = 1.0f;
            }
            else if (name.EndsWith("self_attn.o_proj.weight", StringComparison.Ordinal))
            {
                for (int j = 0; j < r && (r + j) < tr; j++) vals[(long)(r + j) * tc + j] = 1.0f;
                for (int j = 0; j < r && (4 * r + j) < tr; j++) vals[(long)(4 * r + j) * tc + (headDim + j)] = 1.0f;
            }
            else if (name.StartsWith("model.layers.", StringComparison.Ordinal)) { }
            else { Console.WriteLine($"  content cast does not define '{name}'"); SynthInterop.GgufWriterFree(gguf); return 3; }
            byte[] tb = FoundryExport.ToF32Bytes(vals);
            nuint[] dims = cols > 1 ? new nuint[] { (nuint)cols, (nuint)rows } : new nuint[] { (nuint)rows };
            unsafe { fixed (nuint* dp = dims) fixed (byte* bp = tb) SynthInterop.GgufWriterAddTensor(gguf, HfToGgmlName(name), 0, dp, (nuint)dims.Length, bp); }
        }
        int rcwC = SynthInterop.GgufWriterFinalize(gguf); SynthInterop.GgufWriterFree(gguf);
        if (rcwC != 0) return Fail($"gguf finalize failed rc={rcwC}");
        Console.WriteLine($"CONTENT synthesis complete: {outputPath} ({new FileInfo(outputPath).Length / 1048576.0:F0} MB, knowledge+order readout, dehub={dehub})");
        return 0;
    }

    // TOPIC-AUGMENTED cast: adds the CONTEXT state the bigram lacks. logit(Y) = order(cur→Y)
    // + α·knowledge(cur→Y) + βt·topic(ctx→Y) − rep. The TOPIC head is a UNIFORM-attention head
    // (q=k=0 → softmax over the causal window is uniform → MEAN of the values) whose value is each
    // token's KNOWLEDGE embedding, so its output is the running mean knowledge vector of the whole
    // prompt — the topic. lm_head reads βt·lmK against it, biasing every step toward words knowledge-
    // related to the ENTIRE context (not just the current token), which anchors generation instead of
    // letting it climb the IsA chain / loop on hubs. Head 1 (also uniform) is the repetition penalty;
    // the direct B0 order band carries local flow (no shift head). Bands are rh=(d-1)/10 wide so a
    // head's value/output fits within head_dim for any reasonable head count.
    private static async Task<int> WriteWordTopicGgufAsync(
        NpgsqlDataSource ds, LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Dictionary<Hash128, List<int>> tokenSlots,
        int vocab, int dModel, string modelDir, string outputPath,
        TensorSpec[] specs, int tensorCount)
    {
        int nHeads = Math.Max(2, recipe.NumHeads), headDim = dModel / nHeads;
        int trajN = FoundryExport.EnvInt("LAPLACE_FOUNDRY_WORD_TRAJS", 400000);
        double dehub = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_DEHUB", 0.6);
        double repGain = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_REP_GAIN", 6.0);
        double rkLo = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_RANK_LO", 0.55);
        double rkHi = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_RANK_HI", 0.85);
        double alpha = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_KNOW_ALPHA", 1.0);
        double topicG = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_TOPIC_GAIN", 3.0);
        int r = (dModel - 1) / 5, rh = r / 2;
        int CONST = dModel - 1;
        if (rh > headDim) return Fail($"--heads {nHeads}: head_dim {headDim} < band width {rh}; use fewer heads or larger --dim");

        var swR = Stopwatch.StartNew();
        var order1 = FoundryExport.Normalize(await FoundryExport.ReadWordOrderAsync(ds, tokenSlots, 1, trajN));
        var know   = FoundryExport.Normalize(await FoundryExport.ReadLayerPlaneAsync(ds, rkLo, rkHi, tokenSlots, 64));
        if (order1.Nnz == 0) return Fail("order channel empty — ingest text first");
        FoundryExport.FactorAdjacency(order1, vocab, rh, out var embO, out var lmO, out _, conditional: true, suppressSelf: true, dehub: dehub);
        FoundryExport.FactorAdjacency(know.Nnz > 0 ? know : order1, vocab, rh, out var embK, out var lmK, out _, conditional: true, suppressSelf: true, dehub: dehub);
        Console.WriteLine($"  topic-augmented cast: order {order1.Nnz:N0} + α·know[{rkLo:F2},{rkHi:F2}] {know.Nnz:N0} (α={alpha}) + βt·topic (βt={topicG}), rh={rh}, dehub={dehub}, in {swR.Elapsed.TotalSeconds:F1}s");

        // deterministic unit identity code per token (Box-Muller on a SplitMix64 stream)
        var code = new double[(long)vocab * rh];
        ulong st = 0xD1CE5EEDUL;
        double NextG()
        {
            st += 0x9E3779B97F4A7C15UL; ulong z = st;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; z ^= z >> 31;
            st += 0x9E3779B97F4A7C15UL; ulong z2 = st;
            z2 = (z2 ^ (z2 >> 30)) * 0xBF58476D1CE4E5B9UL; z2 = (z2 ^ (z2 >> 27)) * 0x94D049BB133111EBUL; z2 ^= z2 >> 31;
            double u1 = ((z >> 11) + 1.0) / (9007199254740992.0 + 1.0), u2 = (z2 >> 11) / 9007199254740992.0;
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
        for (int v = 0; v < vocab; v++)
        {
            long oc = (long)v * rh; double n2 = 0;
            for (int i = 0; i < rh; i++) { double g = NextG(); code[oc + i] = g; n2 += g * g; }
            double inv = n2 > 1e-9 ? 1.0 / Math.Sqrt(n2) : 0.0;
            for (int i = 0; i < rh; i++) code[oc + i] *= inv;
        }

        double constVal = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_SHIFT_CONST", 4.0);
        var embed  = new double[(long)vocab * dModel];
        var lmHead = new double[(long)vocab * dModel];
        for (int v = 0; v < vocab; v++)
        {
            long o = (long)v * dModel, och = (long)v * rh;
            for (int i = 0; i < rh; i++)
            {
                embed[o + i]           = embO[och + i];            // [0,rh)    order source
                lmHead[o + i]          = lmO[och + i];             // order readout (direct, local flow)
                embed[o + rh + i]      = embK[och + i];            // [rh,2rh)  knowledge source (also topic head V)
                lmHead[o + rh + i]     = alpha * lmK[och + i];     // knowledge readout (direct, local grounding)
                lmHead[o + 2 * rh + i] = topicG * lmK[och + i];    // [2rh,3rh) TOPIC readout (resid written by head 0)
                embed[o + 3 * rh + i]  = code[och + i];            // [3rh,4rh) identity source (rep head V)
                lmHead[o + 4 * rh + i] = -repGain * code[och + i]; // [4rh,5rh) rep readout (resid written by head 1)
            }
            embed[o + CONST] = constVal;
        }
        var isCont = new bool[vocab];
        foreach (var y in order1.Cols) if (y >= 0 && y < vocab) isCont[y] = true;
        foreach (var y in know.Cols) if (y >= 0 && y < vocab) isCont[y] = true;
        var isByte = new bool[vocab]; var isLead = new bool[vocab];
        foreach (var t in tokens) if (t.TokenId >= 0 && t.TokenId < vocab)
        { if (t.IsByteLevel) isByte[t.TokenId] = true; if (t.Role.HasFlag(TokenRole.LeadingSpace)) isLead[t.TokenId] = true; }
        int supp = 0;
        for (int y = 0; y < vocab; y++)
            if (!isCont[y] || isByte[y] || !isLead[y])
            { long o = (long)y * dModel; for (int c = 0; c < dModel; c++) lmHead[o + c] = 0; lmHead[o + CONST] = -100.0; supp++; }
        Console.WriteLine($"  suppressed {supp:N0}/{vocab:N0} byte + non-continuation + bare-alias tokens (space-led words only)");

        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero) return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens, modelDir, byteBpe: true);
        for (int i = 0; i < tensorCount; i++)
        {
            string name; ulong rows, cols;
            unsafe { var sp = specs[i]; name = Marshal.PtrToStringUTF8((IntPtr)sp.Name) ?? ""; rows = sp.Rank >= 1 ? sp.Shape[0] : 1; cols = sp.Rank >= 2 ? sp.Shape[1] : 1; }
            int tr = (int)rows, tc = (int)Math.Max(1UL, cols);
            var vals = new float[(long)tr * tc];
            if (name == "model.embed_tokens.weight")
                for (int v = 0; v < tr; v++) for (int c = 0; c < tc; c++) vals[(long)v * tc + c] = (float)embed[(long)v * dModel + c];
            else if (name == "lm_head.weight")
                for (int v = 0; v < tr; v++) for (int c = 0; c < tc; c++) vals[(long)v * tc + c] = (float)lmHead[(long)v * dModel + c];
            else if (name == "model.norm.weight" || name.EndsWith("input_layernorm.weight", StringComparison.Ordinal) || name.EndsWith("post_attention_layernorm.weight", StringComparison.Ordinal))
                Array.Fill(vals, 1.0f);
            else if (name.EndsWith("self_attn.q_proj.weight", StringComparison.Ordinal)) { /* q=0 → uniform attention (mean over causal window) */ }
            else if (name.EndsWith("self_attn.k_proj.weight", StringComparison.Ordinal)) { /* k=0 → uniform */ }
            else if (name.EndsWith("self_attn.v_proj.weight", StringComparison.Ordinal))
            {   // head 0 (topic) value = knowledge src [rh,2rh); head 1 (rep) value = identity src [3rh,4rh)
                for (int j = 0; j < rh && j < tr; j++) vals[(long)j * tc + (rh + j)] = 1.0f;
                for (int j = 0; j < rh && (headDim + j) < tr; j++) vals[(long)(headDim + j) * tc + (3 * rh + j)] = 1.0f;
            }
            else if (name.EndsWith("self_attn.o_proj.weight", StringComparison.Ordinal))
            {   // head 0 output → topic readout resid [2rh,3rh); head 1 output → rep readout resid [4rh,5rh)
                for (int j = 0; j < rh && (2 * rh + j) < tr; j++) vals[(long)(2 * rh + j) * tc + j] = 1.0f;
                for (int j = 0; j < rh && (4 * rh + j) < tr; j++) vals[(long)(4 * rh + j) * tc + (headDim + j)] = 1.0f;
            }
            else if (name.StartsWith("model.layers.", StringComparison.Ordinal)) { }
            else { Console.WriteLine($"  topic cast does not define '{name}'"); SynthInterop.GgufWriterFree(gguf); return 3; }
            byte[] tb = FoundryExport.ToF32Bytes(vals);
            nuint[] dims = cols > 1 ? new nuint[] { (nuint)cols, (nuint)rows } : new nuint[] { (nuint)rows };
            unsafe { fixed (nuint* dp = dims) fixed (byte* bp = tb) SynthInterop.GgufWriterAddTensor(gguf, HfToGgmlName(name), 0, dp, (nuint)dims.Length, bp); }
        }
        int rcwT = SynthInterop.GgufWriterFinalize(gguf); SynthInterop.GgufWriterFree(gguf);
        if (rcwT != 0) return Fail($"gguf finalize failed rc={rcwT}");
        Console.WriteLine($"TOPIC synthesis complete: {outputPath} ({new FileInfo(outputPath).Length / 1048576.0:F0} MB, topic-augmented, α={alpha} βt={topicG} dehub={dehub})");
        return 0;
    }

    // MULTI-HEAD word n-gram: N REAL attention heads, head j a RoPE shift to offset (j+1), each
    // gathering the word at t-(j+1) and decoding its gap-(j+2) continuation — an honest interpolated
    // (N+1)-gram  log P(w|t-1) + Σ_{j} log P_gap(j+2)(w | t-(j+1)).  This is genuine multi-head (one
    // head per offset), NOT bands crammed into one head: the degenerate "only one of the only" is not
    // a witnessed 5-gram, so the longer context drops its probability and the loop breaks. Every head
    // is the SAME analytic shift (q_base = R_{-(j+1)}·k_base over the recipe RoPE), differing only in
    // offset. Bands (r = (d-1)/(2N+1)): B0 = gap-1 (direct); readout bands 1..N (gathered); source
    // bands N+1..2N (each token's gap-g follower embed, gathered by the heads); [d-1] = const Q/K.
    private static async Task<int> WriteWordMultiHeadGgufAsync(
        NpgsqlDataSource ds, LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Dictionary<Hash128, List<int>> tokenSlots,
        int vocab, int dModel, string modelDir, string outputPath,
        TensorSpec[] specs, int tensorCount)
    {
        int nHeads = Math.Max(2, recipe.NumHeads), headDim = dModel / nHeads;
        double ropeTheta = recipe.RopeTheta > 0 ? recipe.RopeTheta : 10000.0;
        int trajN = FoundryExport.EnvInt("LAPLACE_FOUNDRY_WORD_TRAJS", 400000);
        int nGap = nHeads + 1;                 // gaps 1..nHeads+1 (gap1 direct, gaps 2.. via heads)
        int r = (dModel - 1) / (2 * nHeads + 1);
        int CONST = dModel - 1;
        if (r < 8) return Fail($"--heads {nHeads} too many for --dim {dModel} (band width r={r}); fewer heads or larger dim");

        var sw = Stopwatch.StartNew();
        var embG = new double[nGap][]; var lmG = new double[nGap][];
        var cont = new HashSet<int>();
        long edges = 0;
        for (int g = 1; g <= nGap; g++)
        {
            var Ag = await FoundryExport.ReadWordOrderAsync(ds, tokenSlots, g, trajN);
            if (g == 1 && Ag.Nnz == 0) return Fail("word_order gap1 returned nothing — ingest text first");
            edges += Ag.Nnz;
            foreach (var y in Ag.Cols) if (y >= 0 && y < vocab) cont.Add(y);
            FoundryExport.FactorAdjacency(Ag, vocab, r, out embG[g - 1], out lmG[g - 1], out _, conditional: true, suppressSelf: true);
        }
        Console.WriteLine($"  multi-head word n-gram: {nHeads} heads (offsets 1..{nHeads}), gaps 1..{nGap}, "
            + $"{edges:N0} order edges, band r={r}, in {sw.Elapsed.TotalSeconds:F1}s");

        double constVal = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_SHIFT_CONST", 10.0);
        var embed  = new double[(long)vocab * dModel];
        var lmHead = new double[(long)vocab * dModel];
        for (int v = 0; v < vocab; v++)
        {
            long o = (long)v * dModel;
            for (int i = 0; i < r; i++)
            {
                embed[o + i]  = embG[0][(long)v * r + i];   // B0: gap-1 source
                lmHead[o + i] = lmG[0][(long)v * r + i];    // B0: gap-1 readout (direct, cur)
                for (int j = 0; j < nHeads; j++)
                {
                    lmHead[o + r + j * r + i]               = lmG[j + 1][(long)v * r + i];  // readout band j = gap-(j+2)
                    embed[o + (nHeads + 1) * r + j * r + i] = embG[j + 1][(long)v * r + i]; // source band j = gap-(j+2)
                }
            }
            embed[o + CONST] = constVal;
        }
        // suppress byte + non-continuation tokens from the readout (hard veto via the const channel).
        var isByte = new bool[vocab];
        foreach (var t in tokens) if (t.TokenId >= 0 && t.TokenId < vocab && t.IsByteLevel) isByte[t.TokenId] = true;
        int supp = 0;
        for (int y = 0; y < vocab; y++)
            if (!cont.Contains(y) || isByte[y])
            { long o = (long)y * dModel; for (int c = 0; c < dModel; c++) lmHead[o + c] = 0; lmHead[o + CONST] = -100.0; supp++; }
        Console.WriteLine($"  suppressed {supp:N0}/{vocab:N0} byte + non-continuation tokens from the readout");

        // per-head shift bases: head j attends to offset (j+1) — q_base = R_{-(j+1)}·k_base.
        double scale = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_SHIFT_SCALE", 4.0);
        int npairs = headDim / 2;
        var kbase = new double[headDim];
        var qbaseH = new double[nHeads][];
        for (int f = 0; f < npairs; f++) { double s = scale / Math.Sqrt(npairs); kbase[f] = s; kbase[f + npairs] = 0; }
        for (int j = 0; j < nHeads; j++)
        {
            qbaseH[j] = new double[headDim];
            int off = j + 1;
            for (int f = 0; f < npairs; f++)
            {
                double thetaF = Math.Pow(ropeTheta, -2.0 * f / headDim), s = scale / Math.Sqrt(npairs);
                qbaseH[j][f] = s * Math.Cos(off * thetaF); qbaseH[j][f + npairs] = -s * Math.Sin(off * thetaF);
            }
        }

        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero) return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens, modelDir, byteBpe: true);
        for (int i = 0; i < tensorCount; i++)
        {
            string name; ulong rows, cols;
            unsafe { var sp = specs[i]; name = Marshal.PtrToStringUTF8((IntPtr)sp.Name) ?? ""; rows = sp.Rank >= 1 ? sp.Shape[0] : 1; cols = sp.Rank >= 2 ? sp.Shape[1] : 1; }
            int tr = (int)rows, tc = (int)Math.Max(1UL, cols);
            var vals = new float[(long)tr * tc];

            if (name == "model.embed_tokens.weight")
                for (int v = 0; v < tr; v++) for (int c = 0; c < tc; c++) vals[(long)v * tc + c] = (float)embed[(long)v * dModel + c];
            else if (name == "lm_head.weight")
                for (int v = 0; v < tr; v++) for (int c = 0; c < tc; c++) vals[(long)v * tc + c] = (float)lmHead[(long)v * dModel + c];
            else if (name == "model.norm.weight" || name.EndsWith("input_layernorm.weight", StringComparison.Ordinal) || name.EndsWith("post_attention_layernorm.weight", StringComparison.Ordinal))
                Array.Fill(vals, 1.0f);
            else if (name.EndsWith("self_attn.q_proj.weight", StringComparison.Ordinal))
                for (int j = 0; j < nHeads; j++) for (int d = 0; d < headDim && (j * headDim + d) < tr; d++) vals[(long)(j * headDim + d) * tc + CONST] = (float)qbaseH[j][d];
            else if (name.EndsWith("self_attn.k_proj.weight", StringComparison.Ordinal))
                for (int j = 0; j < nHeads; j++) for (int d = 0; d < headDim && (j * headDim + d) < tr; d++) vals[(long)(j * headDim + d) * tc + CONST] = (float)kbase[d];
            else if (name.EndsWith("self_attn.v_proj.weight", StringComparison.Ordinal))
                // head j's value = its source band j; v[j*hd + d] = h[(N+1)r + j*r + d]
                for (int j = 0; j < nHeads; j++) for (int d = 0; d < r && (j * headDim + d) < tr; d++) vals[(long)(j * headDim + d) * tc + ((nHeads + 1) * r + j * r + d)] = 1.0f;
            else if (name.EndsWith("self_attn.o_proj.weight", StringComparison.Ordinal))
                // write head j's gathered value into readout band j; resid[r + j*r + d] += o[j*hd + d]
                for (int j = 0; j < nHeads; j++) for (int d = 0; d < r && (r + j * r + d) < tr; d++) vals[(long)(r + j * r + d) * tc + (j * headDim + d)] = 1.0f;
            else if (name.StartsWith("model.layers.", StringComparison.Ordinal)) { /* mlp/extra: no-op */ }
            else { Console.WriteLine($"  multi-head n-gram does not define '{name}'"); SynthInterop.GgufWriterFree(gguf); return 3; }

            byte[] tb = FoundryExport.ToF32Bytes(vals);
            nuint[] dims = cols > 1 ? new nuint[] { (nuint)cols, (nuint)rows } : new nuint[] { (nuint)rows };
            unsafe { fixed (nuint* dp = dims) fixed (byte* bp = tb) SynthInterop.GgufWriterAddTensor(gguf, HfToGgmlName(name), 0, dp, (nuint)dims.Length, bp); }
        }
        int rcw = SynthInterop.GgufWriterFinalize(gguf);
        SynthInterop.GgufWriterFree(gguf);
        if (rcw != 0) return Fail($"gguf finalize failed rc={rcw}");
        long fsz = new FileInfo(outputPath).Length;
        Console.WriteLine($"MULTI-HEAD synthesis complete: {outputPath} ({fsz / 1048576.0:F0} MB, {nHeads} heads, gaps 1..{nGap}, r={r}) — interpolated {nGap}-gram");
        return 0;
    }

    // GRAPHEME N-GRAM (trigram) cast: an analytically-constructed previous-token (shift) head
    // injects emb_skip(x_{t-1}) into a reserved residual block so the lm_head decodes the
    // additive trigram  log P(c|cur) + log P_skip(c|prev). The shift is CALCULATED from the
    // recipe's RoPE law (q_base = R_{-1}·k_base over the recipe's per-pair angles θ_f), not
    // chosen — the positional scheme is whatever the recipe declares. Residual layout
    // (block width r = (d-1)/3, head 0 is the shift, cast with --heads 1 so head_dim = d):
    //   [0,r)   current bigram embed   (written by embed; read by lm_head bigram half)
    //   [r,2r)  history                (gets emb_skip(prev) from the shift head; skip half)
    //   [2r,3r) skip source            (emb_skip(cur); read by the head's V, carried to t+1)
    //   [d-1]   constant 1             (the position-only Q/K channel)
    private static async Task<int> WriteGraphemeNgramGgufAsync(
        NpgsqlDataSource ds, LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Dictionary<Hash128, List<int>> tokenSlots,
        int vocab, int dModel, string modelDir, string outputPath,
        TensorSpec[] specs, int tensorCount, bool word = false)
    {
        int nHeads = Math.Max(1, recipe.NumHeads), headDim = dModel / nHeads;
        double ropeTheta = recipe.RopeTheta > 0 ? recipe.RopeTheta : 10000.0;

        var sw = Stopwatch.StartNew();
        // WORD model reads the bigram/skip from the CONTENT TRAJECTORY GEOMETRY (word_order);
        // grapheme model reads the letter bigram from word constituencies (grapheme_order).
        int trajN = FoundryExport.EnvInt("LAPLACE_FOUNDRY_WORD_TRAJS", 400000);
        var A1 = word ? await FoundryExport.ReadWordOrderAsync(ds, tokenSlots, 1, trajN)
                      : await FoundryExport.ReadGraphemeOrderAsync(ds, tokenSlots, 1);
        var A2 = word ? await FoundryExport.ReadWordOrderAsync(ds, tokenSlots, 2, trajN)
                      : await FoundryExport.ReadGraphemeOrderAsync(ds, tokenSlots, 2);
        if (A1.Nnz == 0) return Fail((word ? "word_order" : "grapheme_order") + " gap1 returned nothing — ingest text first");
        Console.WriteLine($"  {(word ? "word" : "grapheme")} n-gram read in {sw.Elapsed.TotalSeconds:F1}s: bigram {A1.Nnz:N0} edges, skip {A2.Nnz:N0} edges");

        // word bigram suppresses self (no king→king); grapheme keeps doubles (ss, nn).
        // WORD model = 5 bands so the SAME shift head copies BOTH the order skip AND the prev word's
        // CONTENT relations (knowledge); grapheme = 3 bands (bigram, history, skip).
        // TUNABLE KNOBS (later become recipe fields): DEHUB λ weakens the function-word loop in the
        // order readout; KNOW_GAIN scales the prev-word knowledge band so facts surface vs flow.
        double dehub    = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_DEHUB", 0.3);
        double knowGain = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_KNOW_GAIN", 1.0);
        int r = word ? (dModel - 1) / 5 : (dModel - 1) / 3;
        FoundryExport.FactorAdjacency(A1, vocab, r, out var embB, out var lmB, out _, conditional: true, suppressSelf: word, dehub: word ? dehub : 0.0);
        FoundryExport.FactorAdjacency(A2, vocab, r, out var embS, out var lmS, out _, conditional: true, suppressSelf: word, dehub: word ? dehub : 0.0);

        // KNOWLEDGE band (word only): the CONTENT rank band (0.55..0.85 = IS_A / HAS_PROPERTY /
        // SYNONYM, ABOVE the order glue). Copied from prev by the shift head → "water is" → liquid,
        // "the king is" → ruler/monarch: facts ON TOP of the flow trigram, no extra attention head.
        double[] embK = System.Array.Empty<double>(), lmK = System.Array.Empty<double>();
        var knowCont = new HashSet<int>();
        if (word)
        {
            var Ak = await FoundryExport.ReadLayerPlaneAsync(ds, 0.55, 0.85, tokenSlots,
                         FoundryExport.EnvInt("LAPLACE_FOUNDRY_FAITHFUL_CAP", 128));
            Console.WriteLine($"  knowledge band: {Ak.Nnz:N0} content edges (IS_A/property/synonym)");
            if (Ak.Nnz > 0)
            {
                // PMI on the rank×eff_mu-weighted content band: rank×eff_mu (in ReadLayerPlaneAsync)
                // gives each relation its AUTHORITY×SIGNIFICANCE; PMI then divides out the object's
                // base rate so a generic high-in-degree target (person/food) doesn't dominate and the
                // SPECIFIC authoritative relation (king IS_A monarch) surfaces. No skipTop hack, no
                // dehub band-aid — the weighting is the fix.
                FoundryExport.FactorAdjacency(Ak, vocab, r, out embK, out lmK, out _, conditional: false, suppressSelf: true);
                foreach (var y in Ak.Cols) if (y >= 0 && y < vocab) knowCont.Add(y);
            }
        }
        bool haveK = embK.Length > 0;

        int CONST = dModel - 1;
        // The const channel must DOMINATE each token's norm so RMSNorm leaves it ~equal across tokens
        // (h[CONST] ≈ √d constant), so the shift head's Q/K are truly position-only. NOT read by lm_head.
        double constVal = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_SHIFT_CONST", 10.0);
        var embed  = new double[(long)vocab * dModel];
        var lmHead = new double[(long)vocab * dModel];
        for (int v = 0; v < vocab; v++)
        {
            long o = (long)v * dModel;
            for (int i = 0; i < r; i++)
            {
                embed[o + i]      = embB[(long)v * r + i];     // B0 cur bigram embed
                lmHead[o + i]     = lmB[(long)v * r + i];      // B0 bigram readout
                lmHead[o + r + i] = lmS[(long)v * r + i];      // B1 skip readout (prev, via shift)
                if (word)
                {
                    if (haveK) lmHead[o + 2 * r + i] = lmK[(long)v * r + i] * knowGain;  // B2 knowledge readout (prev)
                    embed[o + 3 * r + i] = embS[(long)v * r + i];            // B3 skip SOURCE
                    if (haveK) embed[o + 4 * r + i] = embK[(long)v * r + i];  // B4 knowledge SOURCE
                }
                else
                    embed[o + 2 * r + i] = embS[(long)v * r + i];   // grapheme skip source @ [2r,3r)
            }
            embed[o + CONST] = constVal;
        }

        // SUPPRESS byte + non-continuation tokens from the READOUT (the punctuation-salad fix the
        // FAITHFUL path already has): a token nothing precedes (no incoming bigram edge) and every
        // byte-floor token must never be a PREDICTED next-token — their zero/null-space lm_head rows
        // otherwise win the argmax. Zero their readout AND drive their const-channel logit very
        // negative (h[CONST] stays = constVal·rms > 0 through the no-op layers, so this is a hard
        // veto, not just logit-0 that a negative real continuation could still lose to).
        if (word)
        {
            var isCont = new bool[vocab];
            foreach (var y in A1.Cols) if (y >= 0 && y < vocab) isCont[y] = true;
            foreach (var y in knowCont) isCont[y] = true;   // knowledge targets (monarch, liquid…) are emittable
            var isByte = new bool[vocab];
            // dual-form: only the SPACED 'Ġword' (LeadingSpace) forms are EMITTABLE; bare aliases are
            // input-only (they exist so a sentence-initial word matches) — emitting them would produce
            // spaceless output. Suppress every non-LeadingSpace, byte, and non-continuation token.
            var isLead = new bool[vocab];
            foreach (var t in tokens) if (t.TokenId >= 0 && t.TokenId < vocab)
            {
                if (t.IsByteLevel) isByte[t.TokenId] = true;
                if (t.Role.HasFlag(TokenRole.LeadingSpace)) isLead[t.TokenId] = true;
            }
            int supp = 0;
            for (int y = 0; y < vocab; y++)
                if (!isCont[y] || isByte[y] || !isLead[y])
                {
                    long o = (long)y * dModel;
                    for (int c = 0; c < dModel; c++) lmHead[o + c] = 0;
                    lmHead[o + CONST] = -100.0;   // hard veto: logit += -100·h[CONST] ≪ 0
                    supp++;
                }
            Console.WriteLine($"  suppressed {supp:N0}/{vocab:N0} byte + non-continuation tokens from the readout");
        }

        // RoPE-calculated previous-token (shift) head, head 0. Q/K read ONLY the const channel
        // so they are position-independent; k_base[pair f] = (s,0), q_base = R_{-1}·k_base =
        // (s·cosθ_f, −s·sinθ_f), so after RoPE  q_t·k_{t-1}  peaks. s large → softmax ≈ hard.
        double scale = FoundryExport.EnvDouble("LAPLACE_FOUNDRY_SHIFT_SCALE", 4.0);
        int npairs = headDim / 2;
        var qbase = new double[headDim]; var kbase = new double[headDim];
        for (int f = 0; f < npairs; f++)
        {
            double thetaF = Math.Pow(ropeTheta, -2.0 * f / headDim);
            double s = scale / Math.Sqrt(npairs);
            // HALF-SPLIT (NeoX) RoPE pairing: pair f is (dim f, dim f+npairs), NOT interleaved
            // (2f, 2f+1). This matches llama.cpp's NeoX rope and the reference oracle, so the
            // q_base = R_{-1}·k_base rotation actually shifts attention to t-1.
            kbase[f] = s;                    kbase[f + npairs] = 0;
            qbase[f] = s * Math.Cos(thetaF); qbase[f + npairs] = -s * Math.Sin(thetaF);
        }

        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero) return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens, modelDir, byteBpe: word);   // word → byte-level BPE; grapheme → SPM
        for (int i = 0; i < tensorCount; i++)
        {
            string name; ulong rows, cols; int dtype;
            unsafe
            {
                var sp = specs[i];
                name  = Marshal.PtrToStringUTF8((IntPtr)sp.Name) ?? "";
                rows  = sp.Rank >= 1 ? sp.Shape[0] : 1;
                cols  = sp.Rank >= 2 ? sp.Shape[1] : 1;
                dtype = 0;   // F32 only — 14900KS has AVX-512 fused off; bf16 has no fast path and crushes the ~0.02 embed deltas
            }
            int tr = (int)rows, tc = (int)Math.Max(1UL, cols);
            var vals = new float[(long)tr * tc];

            if (name == "model.embed_tokens.weight")
                for (int v = 0; v < tr; v++) for (int c = 0; c < tc; c++)
                    vals[(long)v * tc + c] = (float)embed[(long)v * dModel + c];
            else if (name == "lm_head.weight")
                for (int v = 0; v < tr; v++) for (int c = 0; c < tc; c++)
                    vals[(long)v * tc + c] = (float)lmHead[(long)v * dModel + c];
            else if (name == "model.norm.weight"
                     || name.EndsWith("input_layernorm.weight", StringComparison.Ordinal)
                     || name.EndsWith("post_attention_layernorm.weight", StringComparison.Ordinal))
                Array.Fill(vals, 1.0f);
            else if (name.EndsWith("self_attn.q_proj.weight", StringComparison.Ordinal))
                for (int hd = 0; hd < headDim && hd < tr; hd++) vals[(long)hd * tc + CONST] = (float)qbase[hd];
            else if (name.EndsWith("self_attn.k_proj.weight", StringComparison.Ordinal))
                for (int hd = 0; hd < headDim && hd < tr; hd++) vals[(long)hd * tc + CONST] = (float)kbase[hd];
            else if (name.EndsWith("self_attn.v_proj.weight", StringComparison.Ordinal))
            {
                // shift head's VALUE = the prev word's SOURCE bands. word: copy [3r,5r) (skip + knowledge
                // source, span 2r); grapheme: copy [2r,3r) (skip source, span r).
                int span = word ? 2 * r : r, src = word ? 3 * r : 2 * r;
                for (int j = 0; j < span && j < tr; j++) vals[(long)j * tc + (src + j)] = 1.0f;   // v[j] = h[src+j]
            }
            else if (name.EndsWith("self_attn.o_proj.weight", StringComparison.Ordinal))
            {
                // write the gathered prev source into the READOUT bands [r, r+span): word = skip + knowledge
                // readout [r,3r); grapheme = skip readout [r,2r).
                int span = word ? 2 * r : r;
                for (int j = 0; j < span && (r + j) < tr; j++) vals[(long)(r + j) * tc + j] = 1.0f;  // resid[r+j] += o[j]
            }
            else if (name.StartsWith("model.layers.", StringComparison.Ordinal))
            { /* mlp.* and any remaining layer tensor: no-op (leave zero) */ }
            else { Console.WriteLine($"  n-gram foundry does not define '{name}'"); SynthInterop.GgufWriterFree(gguf); return 3; }

            byte[] tb = dtype == 0 ? FoundryExport.ToF32Bytes(vals) : FoundryExport.ToBf16Bytes(vals);
            nuint[] dims = cols > 1 ? new nuint[] { (nuint)cols, (nuint)rows } : new nuint[] { (nuint)rows };
            unsafe
            {
                fixed (nuint* dp = dims) fixed (byte* bp = tb)
                    SynthInterop.GgufWriterAddTensor(gguf, HfToGgmlName(name), dtype, dp, (nuint)dims.Length, bp);
            }
        }
        int rcw = SynthInterop.GgufWriterFinalize(gguf);
        SynthInterop.GgufWriterFree(gguf);
        if (rcw != 0) return Fail($"gguf finalize failed rc={rcw}");
        long fsz = new FileInfo(outputPath).Length;
        Console.WriteLine($"N-GRAM synthesis complete: {outputPath} ({fsz / 1048576.0:F0} MB, r={r}, heads={nHeads}, "
            + $"shift-scale={scale}, θ={ropeTheta:F0}) — additive trigram log P(c|cur)+log P_skip(c|prev)");
        return 0;
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

    // GPT-2 / byte-level BPE byte->unicode map: bytes 0x21-0x7E, 0xA1-0xAC, 0xAE-0xFF render as
    // themselves; the rest map to U+0100+ so every byte is a printable token. Space (0x20)->'Ġ'
    // (U+0120). Exactly the map llama.cpp's gpt2/llama3 tokenizer uses.
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
    private static int ParseByteToken(string p) => Convert.ToInt32(p.Substring(3, 2), 16);   // "<0xXX>" -> XX

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

        // bos/eos from the mold's generation_config.json when present; llama defaults otherwise.
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
            // BYTE-LEVEL BPE (gpt2) + pre="llama3" sets ignore_merges=true in llama.cpp: any word
            // DIRECTLY in the vocab tokenizes as ONE token, no merge chain — the fix for a
            // whole-word vocab with no sub-pieces (SPM has no merge path → byte-falls-back to
            // garbage, the all-session word-cast failure). OOV composes from the 256 byte tokens.
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
        // ALWAYS prepend a space: word pieces are the 'Ġword' (leading-space) form, so a
        // sentence-initial word ("the king") must also get a leading space or it byte-shreds
        // ("the" → t,h,e while " king" matches). add_space_prefix makes the FIRST word match too.
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
            // Re-encode the SP pieces into the gpt2 byte-level alphabet: <0xXX> -> that byte's
            // byte->unicode char (a NORMAL token); "▁word" -> byteEncode(" word") (leading space
            // becomes 'Ġ'); bare words -> byteEncode(word); specials kept as CONTROL. gpt2 ignores
            // scores. The token INDEX is unchanged, so embed/lm_head (indexed by id) stay aligned.
            for (int i = 0; i < n; i++)
            {
                if (types[i] == 6) { pieces[i] = ByteToUnicode[ParseByteToken(pieces[i])].ToString(); types[i] = 1; }
                else if (types[i] == 1 && pieces[i].StartsWith("▁", StringComparison.Ordinal)) pieces[i] = ByteEncode(" " + pieces[i][1..]);   // 'Ġword' (leading space) so generated words render WITH spaces
                else if (types[i] == 1) pieces[i] = ByteEncode(pieces[i]);
                else types[i] = 3;   // <unk>/<s>/</s> -> CONTROL
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
            // gpt2 REQUIRES a non-empty merges array to load. With ignore_merges (pre=llama3) the
            // in-vocab words tokenize directly and NEVER consult merges; these adjacent byte-pair
            // merges exist only to satisfy the loader (and let OOV compose from byte tokens).
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
