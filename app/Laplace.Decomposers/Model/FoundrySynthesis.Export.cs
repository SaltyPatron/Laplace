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
