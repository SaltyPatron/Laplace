using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.Unicode;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Cli;

internal static class Program
{
    private static string ConnString =>
        Environment.GetEnvironmentVariable("LAPLACE_DB")
        ?? "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace;Include Error Detail=true";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: laplace <seed-unicode | ingest <source> [path] | synthesize tinyllama [output.gguf] | decompose <text> | roundtrip <file> | stats>");
            return 2;
        }
        try
        {
            return args[0] switch
            {
                "seed-unicode" => await SeedUnicodeAsync(),
                "ingest"       => await IngestAsync(args[1..]),
                "synthesize"   => await SynthesizeAsync(args[1..]),
                "decompose"    => Decompose(string.Join(' ', args[1..])),
                "roundtrip"    => Roundtrip(args.Length > 1 ? args[1] : "", args.Length > 2 ? args[2] : null),
                "db-roundtrip" => await DbRoundtripAsync(args.Length > 1 ? args[1] : ""),
                "stats"        => await StatsAsync(),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                Console.Error.WriteLine($"  inner: {inner.GetType().Name}: {inner.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int Fail(string m) { Console.Error.WriteLine(m); return 2; }

    // === db-roundtrip: store content in the substrate, reconstruct FROM the DB ===
    private static async Task<int> DbRoundtripAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return Fail($"usage: laplace db-roundtrip <file>  (not found: {path})");
        CodepointPerfcache.Load(ResolveBlob());
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var writer = new NpgsqlSubstrateWriter(ds);

        byte[] original = File.ReadAllBytes(path);

        var swR = Stopwatch.StartNew();
        await ContentRoundtrip.BootstrapAsync(writer);
        Hash128 docId = await ContentRoundtrip.RecordAsync(writer, original);
        swR.Stop();
        Console.WriteLine($"recorded : {original.Length,10:N0} bytes → document {docId.Hi:x16}{docId.Lo:x16}  in {swR.Elapsed.TotalSeconds:F1}s");

        var swX = Stopwatch.StartNew();
        byte[] rebuilt = await ContentRoundtrip.ReconstructAsync(ds, docId);
        swX.Stop();

        string hIn = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        string hOut = Convert.ToHexString(SHA256.HashData(rebuilt)).ToLowerInvariant();
        bool match = hIn == hOut;
        Console.WriteLine($"rebuilt  : {rebuilt.Length,10:N0} bytes read back FROM the database in {swX.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"sha256 in  : {hIn}");
        Console.WriteLine($"sha256 out : {hOut}");
        Console.WriteLine(match
            ? "BIT-PERFECT FROM DATABASE — reconstruction equals the original."
            : "MISMATCH — reconstruction differs.");
        return match ? 0 : 1;
    }

    // === ingest: IngestRunner + per-source decomposer (ADR 0052) ===
    private static async Task<int> IngestAsync(string[] args)
    {
        string source = args.Length > 0 ? args[0] : "";
        string path   = args.Length > 1 ? args[1] : "";

        if (string.IsNullOrEmpty(source))
            return Fail("usage: laplace ingest <source> [path]  (supported: unicode, model)");

        return source.ToLowerInvariant() switch
        {
            "unicode" => await IngestUnicodeViaRunnerAsync(),
            "model"   => await IngestModelAsync(path),
            _ => Fail($"unknown ingest source '{source}' (supported: unicode, model)"),
        };
    }

    private static async Task<int> IngestModelAsync(string modelDir)
    {
        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir))
            return Fail($"usage: laplace ingest model <model-dir>  (not found: {modelDir})");

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var writer = new NpgsqlSubstrateWriter(ds);
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader);
        var dec    = new ModelDecomposer(modelDir);

        Console.WriteLine($"ingest model {modelDir} via IngestRunner → {ConnString} ...");
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            dec,
            IngestRunOptions.Default with { SkipLayerOrderingCheck = true },
            CancellationToken.None);
        sw.Stop();

        Console.WriteLine(
            $"done: {result.UnitsApplied:N0} intents applied, "
            + $"{result.EntitiesInserted:N0} novel entities, "
            + $"{result.AttestationsInserted:N0} attestations, "
            + $"{result.TotalRoundTrips:N0} round-trips, "
            + $"{sw.Elapsed.TotalSeconds:F1}s");
        if (result.Failures.Count > 0)
        {
            Console.Error.WriteLine($"failures: {result.Failures.Count}");
            foreach (var f in result.Failures.Take(5))
                Console.Error.WriteLine($"  {f}");
            return 1;
        }
        return 0;
    }

    // === synthesize: substrate attestations → GGUF ===
    private static async Task<int> SynthesizeAsync(string[] args)
    {
        string target = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (target == "tinyllama")
            return await SynthesizeTinyLlamaAsync(args.Length > 1 ? args[1] : "/tmp/tinyllama-substrate.gguf");
        return Fail("usage: laplace synthesize tinyllama [output.gguf]");
    }

    private const string TinyLlamaDir =
        "/vault/models/models--TinyLlama--TinyLlama-1.1B-Chat-v1.0/snapshots/fe8a4ea1ffedaf415f4da2f062534de366a451e6";

    private static async Task<int> SynthesizeTinyLlamaAsync(string outputPath)
    {
        Console.WriteLine($"synthesize tinyllama → {outputPath}");

        // 1. Parse tokenizer and recipe from the vault (same files used at ingest time)
        string configPath    = Path.Combine(TinyLlamaDir, "config.json");
        string tokenizerPath = Path.Combine(TinyLlamaDir, "tokenizer.json");
        if (!File.Exists(configPath) || !File.Exists(tokenizerPath))
            return Fail($"model files not found under {TinyLlamaDir}");

        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        var recipe = LlamaRecipeExtractor.Parse(configPath);

        // entity_id → first token_id with that entity (handles canonical collision)
        var entityToToken = new Dictionary<Hash128, int>(tokens.Count);
        foreach (var t in tokens.OrderBy(t => t.TokenId))
            entityToToken.TryAdd(t.EntityId, t.TokenId);

        // 2. Get arch template tensor manifest
        byte[] configJson = File.ReadAllBytes(configPath);
        IntPtr recipeHandle;
        IntPtr tmplHandle;
        var specs = new TensorSpec[300];
        int tensorCount;
        unsafe
        {
            fixed (byte* jsonPtr = configJson)
                recipeHandle = SynthInterop.RecipeParse(jsonPtr, (nuint)configJson.Length);
            if (recipeHandle == IntPtr.Zero)
                return Fail("recipe_parse returned null");
            tmplHandle = SynthInterop.ArchTemplateLoad("llama");
            if (tmplHandle == IntPtr.Zero)
                return Fail("arch_template_load returned null");
            fixed (TensorSpec* specsPtr = specs)
                tensorCount = SynthInterop.ArchTemplateRequiredTensors(
                    tmplHandle, recipeHandle, specsPtr, (nuint)specs.Length);
        }
        if (tensorCount <= 0)
            return Fail($"arch_template_required_tensors returned {tensorCount}");
        Console.WriteLine($"  arch template: {tensorCount} tensor slots");

        // 3. Query attestations from substrate
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();

        // 4. Create GGUF writer and add metadata
        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero)
            return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens);

        // 5. Fill and write each tensor
        var sw = Stopwatch.StartNew();
        int tensorsDone = 0;
        for (int i = 0; i < tensorCount; i++)
        {
            string name;
            ulong rows, cols;
            int   dtype;
            unsafe
            {
                var spec = specs[i];
                name  = Marshal.PtrToStringUTF8((IntPtr)spec.Name) ?? "";
                rows  = spec.Rank >= 1 ? spec.Shape[0] : 1;
                cols  = spec.Rank >= 2 ? spec.Shape[1] : 1;
                dtype = spec.Dtype;
            }

            Hash128? kindId = TensorNameToKind(name);
            if (kindId is null)
            {
                Console.WriteLine($"  skip {name} (no kind mapping)");
                continue;
            }

            // Allocate zero-filled tensor
            byte[] tensorBytes = new byte[rows * cols * (dtype == 0 ? 4UL : 2UL)];

            // Fill from attestations (async PG query — cannot be inside unsafe block)
            int attCount = await FillTensorFromAttestationsAsync(
                ds, kindId.Value, ModelDecomposer.Source,
                entityToToken, tensorBytes, (int)rows, (int)cols, dtype);

            // Write to GGUF — dims in column-major order (inner first)
            nuint[] ggufDims = cols > 1
                ? [(nuint)cols, (nuint)rows]
                : [(nuint)rows];

            unsafe
            {
                fixed (nuint* dimsPtr = ggufDims)
                fixed (byte*  dataPtr = tensorBytes)
                    SynthInterop.GgufWriterAddTensor(gguf, name, dtype, dimsPtr, (nuint)ggufDims.Length, dataPtr);
            }

            tensorsDone++;
            if (tensorsDone % 10 == 0 || tensorsDone == 1)
                Console.WriteLine($"  [{tensorsDone}/{tensorCount}] {name} rows={rows} cols={cols} att={attCount} {sw.Elapsed.TotalSeconds:F1}s");
        }

        int rc = SynthInterop.GgufWriterFinalize(gguf);
        SynthInterop.GgufWriterFree(gguf);
        SynthInterop.ArchTemplateFree(tmplHandle);
        SynthInterop.RecipeFree(recipeHandle);

        if (rc != 0)
            return Fail($"gguf_writer_finalize failed (rc={rc})");

        long fileSize = new FileInfo(outputPath).Length;
        Console.WriteLine($"synthesis complete: {outputPath} ({fileSize / 1048576.0:F0} MB) in {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    // Map tensor name → attestation kind.
    // k_proj uses the same aggregated Q_PROJECTS attestations as q_proj.
    private static Hash128? TensorNameToKind(string name)
    {
        if (name == "model.embed_tokens.weight")          return ModelDecomposer.EmbedsKind;
        if (name.Contains(".self_attn.q_proj.weight"))    return ModelDecomposer.QProjectsKind;
        if (name.Contains(".self_attn.k_proj.weight"))    return ModelDecomposer.QProjectsKind;
        if (name.Contains(".self_attn.v_proj.weight"))    return ModelDecomposer.VProjectsKind;
        if (name.Contains(".self_attn.o_proj.weight"))    return ModelDecomposer.OProjectsKind;
        if (name.Contains(".mlp.gate_proj.weight"))        return ModelDecomposer.GatesKind;
        if (name.Contains(".mlp.up_proj.weight"))          return ModelDecomposer.UpProjectsKind;
        if (name.Contains(".mlp.down_proj.weight"))        return ModelDecomposer.DownProjectsKind;
        if (name.Contains("layernorm.weight") || name == "model.norm.weight") return ModelDecomposer.NormalizesKind;
        if (name == "lm_head.weight")                     return ModelDecomposer.OutputProjectsKind;
        return null;
    }

    // Query attestations for (kind, source) and fill the flat tensor byte array.
    // dtype=0→f32 (norm weights), dtype=2→bf16 (everything else).
    private static async Task<int> FillTensorFromAttestationsAsync(
        NpgsqlDataSource ds,
        Hash128 kindId,
        Hash128 sourceId,
        Dictionary<Hash128, int> entityToToken,
        byte[] tensorBytes,
        int rows, int cols, int dtype)
    {
        int attCount = 0;

        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT subject_id, object_id, rating
            FROM laplace.attestations
            WHERE kind_id = $1 AND source_id = $2
            """;
        cmd.Parameters.AddWithValue(kindId.ToBytes());
        cmd.Parameters.AddWithValue(sourceId.ToBytes());

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var subjBytes = (byte[])rdr[0];
            var objBytes  = (byte[])rdr[1];
            long rating   = rdr.GetInt64(2);

            var subjId = Hash128FromBytes(subjBytes);
            var objId  = Hash128FromBytes(objBytes);

            if (!entityToToken.TryGetValue(subjId, out int row)) continue;
            if (!entityToToken.TryGetValue(objId,  out int col)) continue;
            if (row >= rows || col >= cols) continue;

            double weight = InverseScale(rating);

            if (dtype == 0)
            {
                // f32: 4 bytes per element
                float  fv   = (float)weight;
                uint   bits = BitConverter.SingleToUInt32Bits(fv);
                int    off  = (row * cols + col) * 4;
                tensorBytes[off + 0] = (byte)(bits & 0xFF);
                tensorBytes[off + 1] = (byte)((bits >> 8)  & 0xFF);
                tensorBytes[off + 2] = (byte)((bits >> 16) & 0xFF);
                tensorBytes[off + 3] = (byte)((bits >> 24) & 0xFF);
            }
            else
            {
                // bf16: upper 16 bits of float32
                ushort bf16 = DoubleToBF16(weight);
                int    off  = (row * cols + col) * 2;
                tensorBytes[off + 0] = (byte)(bf16 & 0xFF);
                tensorBytes[off + 1] = (byte)(bf16 >> 8);
            }
            attCount++;
        }
        return attCount;
    }

    // Inverse of LlamaWeightExtractor.ScaleToRating:
    //   rating = 1000 + 800 * (1 - 1/(1 + |w|*10))  [1000..1800], stored as fp×1e9
    //   |w| = (1/(1 - x) - 1) / 10  where x = (r - 1000) / 800
    private static double InverseScale(long ratingFp1e9)
    {
        double r = ratingFp1e9 / 1e9;
        double x = Math.Clamp((r - 1000.0) / 800.0, 0.0, 0.9999);
        return (1.0 / (1.0 - x) - 1.0) / 10.0;
    }

    private static ushort DoubleToBF16(double v)
    {
        float  f    = (float)v;
        uint   bits = BitConverter.SingleToUInt32Bits(f);
        return (ushort)(bits >> 16);
    }

    private static unsafe Hash128 Hash128FromBytes(byte[] b)
    {
        if (b.Length < 16) return Hash128.Zero;
        fixed (byte* p = b) return *(Hash128*)p;
    }

    // Write all GGUF metadata: architecture params + tokenizer vocab.
    private static void WriteGgufMetadata(
        IntPtr gguf,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens)
    {
        SynthInterop.GgufWriterAddMetadataStr(gguf, "general.architecture", "llama");
        SynthInterop.GgufWriterAddMetadataStr(gguf, "general.name", "TinyLlama Substrate Synthesis v0.1");

        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.context_length",          2048);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.embedding_length",         (uint)recipe.HiddenSize);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.block_count",              (uint)recipe.NumLayers);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.feed_forward_length",      (uint)recipe.IntermediateSize);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.attention.head_count",     (uint)recipe.NumHeads);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.attention.head_count_kv",  (uint)recipe.NumKvHeads);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.vocab_size",               (uint)recipe.VocabSize);
        SynthInterop.GgufWriterAddMetadataF32(gguf, "llama.attention.layer_norm_rms_epsilon", 1e-5f);
        SynthInterop.GgufWriterAddMetadataF32(gguf, "llama.rope.freq_base",           (float)recipe.RopeTheta);

        // Tokenizer
        SynthInterop.GgufWriterAddMetadataStr(gguf, "tokenizer.ggml.model", "llama");
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.bos_token_id",     1);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.eos_token_id",     2);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.unknown_token_id", 0);

        // Build sorted-by-id token arrays
        var sorted = tokens.OrderBy(t => t.TokenId).ToArray();
        int n = sorted.Length;

        // Packed string array: each token's raw string (with BPE markers preserved — the
        // raw surface is what llama.cpp uses for display; canonical is for substrate IDs)
        byte[] packed = PackTokenStrings(sorted);
        unsafe
        {
            fixed (byte* p = packed)
                SynthInterop.GgufWriterAddMetadataStrArrayPacked(
                    gguf, "tokenizer.ggml.tokens", p, (nuint)packed.Length, (nuint)n);
        }

        // Scores: all 0.0 (unused in BPE inference; SentencePiece only)
        var scores = new float[n];
        unsafe
        {
            fixed (float* p = scores)
                SynthInterop.GgufWriterAddMetadataF32Array(gguf, "tokenizer.ggml.scores", p, (nuint)n);
        }

        // Token types: NORMAL=0, UNKNOWN=1, CONTROL=2, BYTE=5
        var types = new int[n];
        for (int i = 0; i < n; i++)
            types[i] = ClassifyTokenType(sorted[i].RawToken);
        unsafe
        {
            fixed (int* p = types)
                SynthInterop.GgufWriterAddMetadataI32Array(gguf, "tokenizer.ggml.token_type", p, (nuint)n);
        }
    }

    // Pack token raw strings in GGUF wire format: uint64_le byte-length + UTF-8 bytes per token.
    private static byte[] PackTokenStrings(LlamaTokenizerParser.TokenRecord[] tokens)
    {
        using var ms = new System.IO.MemoryStream();
        Span<byte> lenBuf = stackalloc byte[8];
        foreach (var t in tokens)
        {
            byte[] b = Encoding.UTF8.GetBytes(t.RawToken);
            BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, (ulong)b.Length);
            ms.Write(lenBuf);
            ms.Write(b);
        }
        return ms.ToArray();
    }

    // GGUF token type: 0=NORMAL, 1=UNKNOWN, 2=CONTROL, 5=BYTE
    private static int ClassifyTokenType(string raw)
    {
        if (raw is "<unk>" or "<UNK>" or "<unknown>") return 1;
        if (raw is "<s>" or "</s>" or "<pad>" or "<bos>" or "<eos>") return 2;
        if (raw.Length == 6 && raw.StartsWith("<0x", StringComparison.Ordinal) && raw.EndsWith('>')) return 5;
        return 0;
    }

    private static async Task<int> IngestUnicodeViaRunnerAsync()
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var writer = new NpgsqlSubstrateWriter(ds);
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader);
        var dec = new UnicodeDecomposer();

        Console.WriteLine($"ingest unicode via IngestRunner → {ConnString} ...");
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            dec,
            IngestRunOptions.Default with { SkipLayerOrderingCheck = true },
            CancellationToken.None);
        sw.Stop();

        Console.WriteLine(
            $"done: {result.UnitsApplied:N0} intents applied, "
            + $"{result.EntitiesInserted:N0} novel entities, "
            + $"{result.PhysicalitiesInserted:N0} physicalities, "
            + $"{result.TotalRoundTrips:N0} round-trips, "
            + $"{sw.Elapsed.TotalSeconds:F1}s");
        if (result.Failures.Count > 0)
        {
            Console.Error.WriteLine($"failures: {result.Failures.Count}");
            return 1;
        }
        await PrintCountsAsync(ds);
        return 0;
    }

    // === seed-unicode: stream the T0 codepoint seed into the substrate ===
    private static async Task<int> SeedUnicodeAsync()
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var writer = new NpgsqlSubstrateWriter(ds);
        var reader = new NpgsqlSubstrateReader(ds);
        var dec = new UnicodeDecomposer();
        var ctx = new CliContext(writer, reader);

        Console.WriteLine($"seeding T0 codepoints into {ConnString} ...");
        var sw = Stopwatch.StartNew();

        await dec.InitializeAsync(ctx);
        long entities = 0, inserted = 0;
        int batches = 0;
        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
        {
            var r = await writer.ApplyAsync(change);
            entities += change.Entities.Length;
            inserted += r.EntitiesInserted;
            if (++batches % 16 == 0)
                Console.WriteLine($"  {entities,9:N0} codepoints applied ({sw.Elapsed.TotalSeconds:F0}s)");
        }
        sw.Stop();
        Console.WriteLine($"done: {entities:N0} codepoints presented, {inserted:N0} novel entities inserted in {sw.Elapsed.TotalSeconds:F1}s");

        await PrintCountsAsync(ds);
        return 0;
    }

    // === stats: current substrate row counts ===
    private static async Task<int> StatsAsync()
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await PrintCountsAsync(ds);
        return 0;
    }

    private static async Task PrintCountsAsync(NpgsqlDataSource ds)
    {
        await using var conn = await ds.OpenConnectionAsync();
        async Task<long> Scalar(string sql, byte[]? p = null)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = sql;
            if (p is not null) c.Parameters.AddWithValue("p", p);
            return (long)(await c.ExecuteScalarAsync())!;
        }
        long entities = await Scalar("SELECT count(*) FROM laplace.entities");
        long codepoints = await Scalar("SELECT count(*) FROM laplace.entities WHERE type_id = @p",
                                       UnicodeDecomposer.CodepointType.ToBytes());
        long phys = await Scalar("SELECT count(*) FROM laplace.physicalities");
        long content = await Scalar("SELECT count(*) FROM laplace.physicalities WHERE source_id = @p AND kind = 1",
                                    UnicodeDecomposer.Source.ToBytes());
        Console.WriteLine("substrate counts:");
        Console.WriteLine($"  entities total        : {entities,9:N0}");
        Console.WriteLine($"  └ Codepoint (T0)      : {codepoints,9:N0}");
        Console.WriteLine($"  physicalities total   : {phys,9:N0}");
        Console.WriteLine($"  └ UnicodeDecomposer CONTENT : {content,9:N0}");

        // Show a concrete row: U+0041 'A'.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT encode(p.entity_id,'hex'), e.tier,
                                   ST_X(p.coord), ST_Y(p.coord), ST_Z(p.coord), ST_M(p.coord),
                                   encode(p.hilbert_index,'hex')
                            FROM laplace.physicalities p JOIN laplace.entities e ON e.id = p.entity_id
                            WHERE p.source_id = @s AND p.kind = 1 AND p.entity_id = @e";
        cmd.Parameters.AddWithValue("s", UnicodeDecomposer.Source.ToBytes());
        // entity id of 'A' = BLAKE3-128 of UTF-8 "A"
        cmd.Parameters.AddWithValue("e", Hash128.Blake3(new byte[] { 0x41 }).ToBytes());
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (await rdr.ReadAsync())
        {
            Console.WriteLine("  sample U+0041 'A':");
            Console.WriteLine($"    entity id : {rdr.GetString(0)}  tier={rdr.GetInt16(1)}");
            Console.WriteLine($"    coord     : ({rdr.GetDouble(2):F6}, {rdr.GetDouble(3):F6}, {rdr.GetDouble(4):F6}, {rdr.GetDouble(5):F6})");
            Console.WriteLine($"    hilbert   : {rdr.GetString(6)}");
        }
        else
        {
            Console.WriteLine("  (no CONTENT physicality for U+0041 yet — run seed-unicode)");
        }
    }

    // === decompose: run the engine text decomposer + hash composer live ===
    private static int Decompose(string text)
    {
        if (string.IsNullOrEmpty(text)) return Fail("usage: laplace decompose <text>");
        CodepointPerfcache.Load(ResolveBlob());

        using var tree = TextDecomposer.Run(text);
        unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

        Console.WriteLine($"decompose \"{text}\"  ({tree.NodeCount} nodes)\n");
        uint root = (uint)tree.NodeCount - 1;
        PrintNode(tree, root, 0);
        return 0;
    }

    // === roundtrip: ingest a text file through the engine + export it byte-perfect ===
    private static int Roundtrip(string path, string? outPath)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return Fail($"usage: laplace roundtrip <file> [out]  (not found: {path})");
        CodepointPerfcache.Load(ResolveBlob());

        byte[] original = File.ReadAllBytes(path);

        // Ingest: UTF-8 → observed codepoints → UAX#29 tier tree (no NFC at ingest).
        var swIn = Stopwatch.StartNew();
        using var tree = TextDecomposer.Run(original);
        swIn.Stop();

        // Export: re-encode the tier-0 codepoint leaves (the contiguous prefix,
        // in document order) back to UTF-8.
        var swOut = Stopwatch.StartNew();
        int total = tree.NodeCount;
        int leaves = 0;
        var sb = new StringBuilder(original.Length);
        for (uint i = 0; i < total; i++)
        {
            var v = tree.GetNode(i);
            if (v.Tier != 0) break;
            sb.Append(char.ConvertFromUtf32((int)v.Atom));
            leaves++;
        }
        byte[] exported = Encoding.UTF8.GetBytes(sb.ToString());
        swOut.Stop();

        if (!string.IsNullOrEmpty(outPath)) File.WriteAllBytes(outPath, exported);

        string hIn = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        string hOut = Convert.ToHexString(SHA256.HashData(exported)).ToLowerInvariant();
        bool match = hIn == hOut;

        double mbIn = original.Length / 1048576.0;
        Console.WriteLine($"ingest  : {original.Length,10:N0} bytes  →  {total:N0} tier-tree nodes ({leaves:N0} codepoints)  in {swIn.Elapsed.TotalMilliseconds:F0} ms  ({mbIn / swIn.Elapsed.TotalSeconds:F1} MB/s)");
        Console.WriteLine($"export  : {exported.Length,10:N0} bytes  in {swOut.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"sha256 in  : {hIn}");
        Console.WriteLine($"sha256 out : {hOut}");
        Console.WriteLine(match
            ? "BIT-PERFECT — export is byte-for-byte identical to the original."
            : "MISMATCH — export differs from the original.");
        return match ? 0 : 1;
    }

    private static readonly string[] TierName = { "CP", "GRAPHEME", "WORD", "SENTENCE", "DOC" };

    private static void PrintNode(TierTree tree, uint idx, int depth)
    {
        var v = tree.GetNode(idx);
        string label = v.Tier < TierName.Length ? TierName[v.Tier] : $"T{v.Tier}";
        string idHex;
        unsafe { idHex = $"{v.Id.Hi:x16}".Substring(0, 8); }
        string text = RenderLeaves(tree, idx).Replace("\n", "\\n");
        Console.WriteLine($"{new string(' ', depth * 2)}{label,-9} [{idHex}] \"{text}\"");
        if (v.Tier == 0) return;
        for (uint i = 0; i < v.ChildCount; i++)
            PrintNode(tree, v.FirstChildIdx + i, depth + 1);
    }

    private static string RenderLeaves(TierTree tree, uint idx)
    {
        var v = tree.GetNode(idx);
        if (v.Tier == 0) return char.ConvertFromUtf32((int)v.Atom);
        var sb = new StringBuilder();
        for (uint i = 0; i < v.ChildCount; i++) sb.Append(RenderLeaves(tree, v.FirstChildIdx + i));
        return sb.ToString();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int PerfcacheResolver(
        uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        var recs = CodepointPerfcache.Records;
        if (atom >= (uint)recs.Length) return -1;
        ref readonly var r = ref recs[(int)atom];
        *outId = r.Hash;
        outCoord[0] = r.CoordX; outCoord[1] = r.CoordY; outCoord[2] = r.CoordZ; outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
    }

    private static string ResolveBlob()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        const string share = "/opt/laplace/share/laplace";
        if (Directory.Exists(share))
        {
            var hit = Directory.EnumerateFiles(share, "laplace_t0_perfcache*.bin").FirstOrDefault();
            if (hit is not null) return hit;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory.EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin",
                                                   SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        throw new InvalidOperationException("perf-cache blob not found; build the engine or set LAPLACE_PERFCACHE_BIN.");
    }

    private sealed class CliContext(ISubstrateWriter writer, ISubstrateReader reader) : IDecomposerContext
    {
        public string EcosystemPath => "/vault/Data/Unicode";
        public ISubstrateWriter Writer { get; } = writer;
        public ISubstrateReader Reader { get; } = reader;
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public string SubstrateVersion => "v0.1";
    }
}
