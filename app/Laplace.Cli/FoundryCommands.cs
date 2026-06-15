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
            var positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
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
                var nativeMold = await MaterializeNativeMoldAsync(nativeVocab, nativeDim, nativeLayers, nativeHeads, nativeKv, nativeFfn, crawlSeeds, crawlHops, crawlFanout);
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
            return await SynthesizeFromSubstrateAsync(recipePath, outputPath);
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
        string? crawlSeeds = null, int crawlHops = 3, int crawlFanout = 64)
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
            if (seeds is { Length: > 0 })
            {
                cmd.CommandText = "SELECT surface, weight FROM laplace.foundry_vocab_crawl($1, $2, $3, $4)";
                cmd.Parameters.AddWithValue(seeds);
                cmd.Parameters.AddWithValue(vocabN);
                cmd.Parameters.AddWithValue(crawlHops);
                cmd.Parameters.AddWithValue(crawlFanout);
                Console.WriteLine($"  vocab: seeded crawl from [{string.Join(", ", seeds)}] (hops {crawlHops}, fanout {crawlFanout})");
            }
            else
            {
                cmd.CommandText = "SELECT surface, weight FROM laplace.foundry_vocab($1)";
                cmd.Parameters.AddWithValue(vocabN);
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
        foreach (var (surface, weight) in sel)
            pieces.Add(("▁" + surface, (float)(Math.Log(weight + 1.0) + 1.0), 1));
        int vocabSize = pieces.Count;
        Console.WriteLine($"  native vocab: {sel.Count:N0} substrate word entities + 256 byte floor + 3 specials = {vocabSize:N0}");

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

    private static async Task<int> SynthesizeFromSubstrateAsync(string recipePath, string outputPath)
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
        Task<FoundryExport.PlaneCoo> RoleAsync(params string[] rels)
            => FoundryExport.ReadConsensusPlaneAsync(ds, rels, tokenSlots, degreeCap);
        // The operators are projections of the FULL rank-grouped consensus — every
        // populated relation type, not a sliver. Each transformer channel reads the
        // relations of the rank whose role it plays (rank header: standards 0.91 …
        // taxonomic 0.82 … causal 0.64 … associative 0.36 … tensor_calculation 0.27).
        // The model-contracted tensor_calculation relations (ATTENDS/OV_RELATES/
        // COMPLETES_TO) are the LEAST authoritative class and just join when present —
        // the seed facts outrank them, so a deposited model is never required.
        // EMBEDDING geometry ← equivalence (sameness / similarity / translation / form).
        var simTask = RoleAsync(
            "IS_SYNONYM_OF", "IS_SIMILAR_TO", "SIMILAR_TO", "IS_TRANSLATION_OF",
            "FORM_OF", "HAS_VARIANT_OF", "CORRESPONDS_TO", "IS_LEMMA_OF", "IS_PARTICIPLE_OF");
        // V/O ← taxonomic + partitive (what it IS, what it is MADE OF) + model OV_RELATES.
        var relTask = RoleAsync(
            "IS_A", "IS_INSTANCE_OF", "MANNER_OF", "EVOKES_FRAME", "IS_SENSE_OF",
            "HAS_PART", "HAS_A", "HAS_PROPERTY", "HAS_MEMBER", "HAS_ATTRIBUTE", "HAS_FEATURE",
            "HAS_SEMANTIC_ROLE", "HAS_FRAME_ELEMENT", "HAS_THEMATIC_ROLE", "CONTAINS",
            "MADE_UP_OF", "HAS_SUBSTANCE", "HAS_SENSE", "HAS_POS", "HAS_XPOS",
            "DEPENDS_ON", "RECEIVES_ACTION", "OV_RELATES");
        // FFN / completion ← causal + sequence (what FOLLOWS, RESULTS, ENTAILS) + COMPLETES_TO.
        var preTask = RoleAsync(
            "PRECEDES", "IS_BEFORE", "IS_AFTER", "CAUSES", "CAUSATIVE_OF", "INCHOATIVE_OF",
            "ENTAILS", "HAS_SUBEVENT", "HAS_FIRST_SUBEVENT", "HAS_LAST_SUBEVENT", "HAS_PREREQUISITE",
            "X_INTENT", "X_NEED", "X_EFFECT", "X_WANT", "X_REACT", "X_ATTR", "X_REASON", "X_FILLED_BY",
            "O_EFFECT", "O_REACT", "O_WANT", "CAUSES_DESIRE", "MOTIVATED_BY_GOAL",
            "OBJECT_USE", "OBSTRUCTED_BY", "CREATED_BY", "COMPLETES_TO");
        // ATTENTION ← associative (relatedness / co-occurrence / use / location) + model ATTENDS.
        var attTask = RoleAsync(
            "RELATED_TO", "USED_FOR", "AT_LOCATION", "LOCATED_NEAR", "CAPABLE_OF", "DESIRES",
            "HAS_CONTEXT", "IS_COORDINATE_TERM_WITH", "DERIVATIONALLY_RELATED", "PERTAINS_TO",
            "SYMBOL_OF", "ALSO_SEE", "HAS_VERB_FRAME", "REFERENCES", "CALLS",
            "ATTENDS", "CO_OCCURS_WITH");
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

        // ── generate the basis: LE over the union graph, GSO, Procrustes-anchored
        // to token content coordinates, deterministic capacity fill, bias channel ──
        var anchors = new double[vocab][];
        foreach (var t in tokens)
        {
            if (t.TokenId < 0 || t.TokenId >= vocab || !t.HasContentCoord) continue;
            anchors[t.TokenId] = [t.ContentX, t.ContentY, t.ContentZ, t.ContentM];
        }
        var basisSeed = Hash128.Blake3(recipe.CanonicalJson);
        var swBasis = Stopwatch.StartNew();
        var unionGraph = FoundryExport.Union(sim, rel, pre, att);
        // AFFINITY-SVD basis (token = SVD of its relational row) carries the consensus geometry
        // far better than Laplacian-eigenmaps (measured +3.0σ vs +0.58σ → conditioning works).
        // It is a dense vocab×vocab SVD, so default it ON for modest vocab and fall back to LE
        // above the size where dense SVD is impractical. Env forces: 1=on, 0=off.
        string? affRaw = Environment.GetEnvironmentVariable("LAPLACE_FOUNDRY_EMBED_AFFINITY");
        bool affBasis = affRaw == "1" || (affRaw != "0" && vocab <= 3000);
        Console.WriteLine($"  basis path: {(affBasis ? "AFFINITY-SVD (token = SVD of its relational row)" : "Laplacian-eigenmaps")} (vocab {vocab})");
        var E = affBasis
            ? FoundryExport.BuildBasisAffinity(vocab, dModel, unionGraph, anchors, basisSeed, out var basisStats)
            : FoundryExport.BuildBasis(vocab, dModel, unionGraph, anchors, basisSeed, out basisStats);
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
        var rankPlanes = new[] { att, rel, pre, sim };
        var rankNames  = new[] { "associative", "taxo+part", "causal+seq", "equivalence" };
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
            var roPlanes = new[] { sim, rel, pre, att };
            var roW      = new[] { 0.91, 0.82, 0.64, 0.36 };
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
            WriteGgufMetadata(gguf, recipe, tokens, modelDir);
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
                    dtype = sp.Dtype;
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
                    switch (rest)
                    {
                        case "self_attn.q_proj.weight": FoundryExport.FillRows(vals, tr, tc, fAttn, attnScale); break;
                        case "self_attn.k_proj.weight": FoundryExport.FillRowsRight(vals, tr, tc, fAttn, attnScale); break;
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

    private static void WriteGgufMetadata(
        IntPtr gguf,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        string modelDir)
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
