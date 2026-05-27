using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.ISO;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.OMW;
using Laplace.Decomposers.UD;
using Laplace.Decomposers.Unicode;
using Laplace.Decomposers.WordNet;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using DynamicsInterop = Laplace.Engine.Dynamics.NativeInterop;
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
            return Fail("usage: laplace ingest <source> [path]  (unicode | iso639 | wordnet | omw | ud | model)");

        return source.ToLowerInvariant() switch
        {
            "unicode"  => await IngestUnicodeViaRunnerAsync(),
            "iso639"   => await IngestISO639Async(),
            "wordnet"  => await IngestViaRunnerAsync(new WordNetDecomposer(), "/vault/Data/Wordnet", skipLayerCheck: false),
            "omw"      => await IngestViaRunnerAsync(new OMWDecomposer(), "/vault/Data/omw", skipLayerCheck: false),
            "ud"       => await IngestViaRunnerAsync(new UDDecomposer(), "/vault/Data/UD-Treebanks", skipLayerCheck: false),
            "model"    => await IngestModelAsync(path),
            _ => Fail($"unknown ingest source '{source}' (supported: unicode, iso639, wordnet, omw, ud, model)"),
        };
    }

    private static async Task<int> IngestModelAsync(string modelDir)
    {
        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir))
            return Fail($"usage: laplace ingest model <model-dir>  (not found: {modelDir})");

        /* LlamaTokenizerParser.Parse now routes tokens through TextDecomposer +
         * HashComposer + TextEntityBuilder so token entities are content-addressed
         * the same as every other text entity (R5). The HashComposer atom resolver
         * reads from CodepointPerfcache, so the process-wide T0 perf-cache MUST
         * be loaded before any tokenizer parse runs. */
        CodepointPerfcache.Load(ResolveBlob());

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();

        /* Check if this model source is already ingested — Q_PROJECTS attestations
         * only exist after the weight extraction phase completes successfully.
         * Same attestation ID = same content = ON CONFLICT DO NOTHING, so re-running
         * is safe but wasteful. Short-circuit here for the common re-run case. */
        await using (var chkConn = await ds.OpenConnectionAsync())
        {
            await using var chkCmd = chkConn.CreateCommand();
            chkCmd.CommandText =
                "SELECT EXISTS(SELECT 1 FROM laplace.attestations " +
                "WHERE source_id = $1 AND kind_id = $2 LIMIT 1)";
            chkCmd.Parameters.Add(new global::Npgsql.NpgsqlParameter { Value = ModelDecomposer.Source.ToBytes(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
            chkCmd.Parameters.Add(new global::Npgsql.NpgsqlParameter { Value = ModelDecomposer.QProjectsKind.ToBytes(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
            bool alreadyIngested = (bool)(await chkCmd.ExecuteScalarAsync() ?? false);
            if (alreadyIngested)
            {
                Console.WriteLine($"Model already ingested — source entity: {ModelDecomposer.Source}");
                Console.WriteLine("(use 'just db-nuke && just seed-t0 && just ingest-tinyllama' to re-ingest from scratch)");
                return 0;
            }
        }

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
        if (target == "tinyllama-passthrough")
            return await SynthesizeTinyLlamaPassthroughAsync(args.Length > 1 ? args[1] : "/tmp/tinyllama-passthrough.gguf");
        if (target == "tinyllama-passthrough-f32")
            return await SynthesizeTinyLlamaPassthroughAsync(args.Length > 1 ? args[1] : "/tmp/tinyllama-passthrough-f32.gguf", allF32: true);
        if (target == "tinyllama-sparse")
        {
            double tol = args.Length > 1 && double.TryParse(args[1], out var t) ? t : 0.10;
            string outp = args.Length > 2 ? args[2] : $"/tmp/tinyllama-sparse-{tol:0.00}.gguf";
            // BF16 output (half the F32 size) — run with `-dev CUDA0` to pin the 4060 (native BF16).
            return await SynthesizeTinyLlamaPassthroughAsync(outp, allF32: false, sparseTol: tol);
        }
        return Fail("usage: laplace synthesize <tinyllama | tinyllama-passthrough | tinyllama-passthrough-f32 | tinyllama-sparse [tol]> [output.gguf]");
    }

    private const string TinyLlamaDir =
        "/vault/models/models--TinyLlama--TinyLlama-1.1B-Chat-v1.0/snapshots/fe8a4ea1ffedaf415f4da2f062534de366a451e6";

    private static async Task<int> SynthesizeTinyLlamaAsync(string outputPath)
    {
        Console.WriteLine($"synthesize tinyllama → {outputPath}");

        /* Same perfcache prerequisite as IngestModelAsync — LlamaTokenizerParser
         * runs TextDecomposer + HashComposer which read codepoint records from
         * the process-wide T0 perf-cache. */
        CodepointPerfcache.Load(ResolveBlob());

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

        // 3. Open substrate connection
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();

        // 4. Pre-compute spectral embeddings + interior tensors from substrate.
        //    The recipe drives the dispatch in the manifest loop below; the
        //    heavy substrate queries + dynamics-pipeline calls happen ONCE up
        //    front (one E per direction; one W per kind; broadcast across all
        //    layers per ADR 0056 within-model aggregation).
        int vocabSize = recipe.VocabSize;
        int dModel    = recipe.HiddenSize;
        int nHeads    = recipe.NumHeads;
        int nKvHeads  = recipe.NumKvHeads;
        int headDim   = dModel / nHeads;
        int interm    = recipe.IntermediateSize;
        double lambda = 1e-3;

        Console.WriteLine($"  building cross-kind input adjacency from substrate...");
        var swPre = Stopwatch.StartNew();
        var crossKindAdj = await BuildSubstrateAdjacencyAsync(
            ds, ModelDecomposer.Source, entityToToken, kindFilter: null);
        Console.WriteLine($"    input adjacency: {crossKindAdj.Rows.Length} edges in {swPre.Elapsed.TotalSeconds:F1}s");

        swPre.Restart();
        Console.WriteLine($"  spectral embedding (token_embd, dim={dModel})...");
        var EInput = ComputeSpectralEmbedding(crossKindAdj, vocabSize, dModel);
        Console.WriteLine($"    token_embd spectral embedding in {swPre.Elapsed.TotalSeconds:F1}s");

        swPre.Restart();
        Console.WriteLine($"  output-direction adjacency from PROJECTION_OUTPUT physicalities...");
        var outAdj = await BuildOutputDirectionAdjacencyAsync(
            ds, ModelDecomposer.Source, entityToToken);
        Console.WriteLine($"    output adjacency: {outAdj.Rows.Length} edges in {swPre.Elapsed.TotalSeconds:F1}s");

        double[] EOutput;
        if (outAdj.Rows.Length > 0)
        {
            swPre.Restart();
            Console.WriteLine($"  spectral embedding (output.weight, dim={dModel})...");
            EOutput = ComputeSpectralEmbedding(outAdj, vocabSize, dModel);
            Console.WriteLine($"    output.weight spectral embedding in {swPre.Elapsed.TotalSeconds:F1}s");
        }
        else
        {
            /* No lm_head ingest → v0 fallback: copy of input embedding
             * (tied-weights). Documented limit. */
            EOutput = EInput;
            Console.WriteLine($"    (no PROJECTION_OUTPUT data — output.weight tied to token_embd)");
        }

        swPre.Restart();
        Console.WriteLine($"  reconstructing interior tensors from per-kind subgraphs...");
        var qAdj  = await BuildSubstrateAdjacencyAsync(ds, ModelDecomposer.Source, entityToToken, ModelDecomposer.QProjectsKind);
        var vAdj  = await BuildSubstrateAdjacencyAsync(ds, ModelDecomposer.Source, entityToToken, ModelDecomposer.VProjectsKind);
        var oAdj  = await BuildSubstrateAdjacencyAsync(ds, ModelDecomposer.Source, entityToToken, ModelDecomposer.OProjectsKind);
        var gAdj  = await BuildSubstrateAdjacencyAsync(ds, ModelDecomposer.Source, entityToToken, ModelDecomposer.GatesKind);
        var upAdj = await BuildSubstrateAdjacencyAsync(ds, ModelDecomposer.Source, entityToToken, ModelDecomposer.UpProjectsKind);
        var dnAdj = await BuildSubstrateAdjacencyAsync(ds, ModelDecomposer.Source, entityToToken, ModelDecomposer.DownProjectsKind);

        var (Wq, Wk) = ReconstructInteriorTensorAsymmetric(
            EInput, qAdj, vocabSize, dModel,
            outDimQ: nHeads   * headDim,
            outDimK: nKvHeads * headDim, lambda);
        var Wv    = ReconstructInteriorTensorSymmetric(EInput, vAdj,  vocabSize, dModel, nKvHeads * headDim, lambda);
        var Wo    = ReconstructInteriorTensorSymmetric(EInput, oAdj,  vocabSize, dModel, dModel,             lambda);
        var Wgate = ReconstructInteriorTensorSymmetric(EInput, gAdj,  vocabSize, dModel, interm,             lambda);
        var Wup   = ReconstructInteriorTensorSymmetric(EInput, upAdj, vocabSize, dModel, interm,             lambda);
        var WdnT  = ReconstructInteriorTensorSymmetric(EInput, dnAdj, vocabSize, dModel, interm,             lambda);
        /* WdnT is [interm × dModel]; HF down_proj is [dModel × interm] — transpose at write. */
        Console.WriteLine($"    interior reconstruction in {swPre.Elapsed.TotalSeconds:F1}s");

        Console.WriteLine($"  loading NORMALIZES per-(layer, role, dim) scales...");
        var normVecs = await QueryNormalizesPerDimAsync(
            ds, ModelDecomposer.Source, recipe.NumLayers, dModel);
        Console.WriteLine($"    {normVecs.Count} norm vectors recovered");

        // 5. Create GGUF writer and add metadata
        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero)
            return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens);

        // 6. Walk recipe tensor manifest; dispatch by GGML name → write substrate-derived content.
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

            /* Allocate zero-filled tensor (R4 sparse-by-construction — any
             * cell without substrate signal stays exactly zero). */
            byte[] tensorBytes = new byte[rows * cols * (dtype == 0 ? 4UL : 2UL)];
            string ggmlName = HfToGgmlName(name);
            string label = "(zero)";

            if (ggmlName == "token_embd.weight")
            {
                WriteDoubleMatrixToTensorBytes(EInput, (int)rows, (int)cols, dtype, tensorBytes);
                label = "token_embd ← spectral E_input";
            }
            else if (ggmlName == "output.weight")
            {
                WriteDoubleMatrixToTensorBytes(EOutput, (int)rows, (int)cols, dtype, tensorBytes);
                label = "output ← spectral E_output";
            }
            else if (ggmlName.EndsWith("_norm.weight", StringComparison.Ordinal))
            {
                /* Identify (layer, role) from the GGML name:
                 *   blk.{L}.attn_norm.weight  → (L, "input_layernorm")
                 *   blk.{L}.ffn_norm.weight   → (L, "post_attention_layernorm")
                 *   output_norm.weight        → (-1, "model_norm") */
                (int layer, string role)? slot = null;
                if (ggmlName == "output_norm.weight") slot = (-1, "model_norm");
                else if (ggmlName.StartsWith("blk.", StringComparison.Ordinal))
                {
                    int dotIdx = ggmlName.IndexOf('.', 4);
                    if (dotIdx > 4 && int.TryParse(ggmlName.AsSpan(4, dotIdx - 4), out int L))
                    {
                        string rest = ggmlName.Substring(dotIdx + 1);
                        if (rest == "attn_norm.weight")     slot = (L, "input_layernorm");
                        else if (rest == "ffn_norm.weight") slot = (L, "post_attention_layernorm");
                    }
                }
                float[]? scaleVec = (slot is { } s
                    && normVecs.TryGetValue(s, out var v)) ? v : null;
                FillPerDimNorm(tensorBytes, scaleVec, (int)Math.Max(rows, cols), dtype);
                label = scaleVec != null ? $"norm ← NORMALIZES{slot}" : "norm ← identity 1.0";
            }
            else if (ggmlName.StartsWith("blk.", StringComparison.Ordinal))
            {
                int dotIdx = ggmlName.IndexOf('.', 4);
                if (dotIdx > 4)
                {
                    string suffix = ggmlName.Substring(dotIdx + 1);
                    switch (suffix)
                    {
                        case "attn_q.weight":
                            WriteFloatMatrixToTensorBytes(Wq, (int)rows, (int)cols, dtype, tensorBytes);
                            label = "attn_q ← Q_PROJECTS reconstruct (asym)";
                            break;
                        case "attn_k.weight":
                            WriteFloatMatrixToTensorBytes(Wk, (int)rows, (int)cols, dtype, tensorBytes);
                            label = "attn_k ← Q_PROJECTS reconstruct (asym)";
                            break;
                        case "attn_v.weight":
                            WriteFloatMatrixToTensorBytes(Wv, (int)rows, (int)cols, dtype, tensorBytes);
                            label = "attn_v ← V_PROJECTS reconstruct";
                            break;
                        case "attn_output.weight":
                            WriteFloatMatrixToTensorBytes(Wo, (int)rows, (int)cols, dtype, tensorBytes);
                            label = "attn_output ← O_PROJECTS reconstruct";
                            break;
                        case "ffn_gate.weight":
                            WriteFloatMatrixToTensorBytes(Wgate, (int)rows, (int)cols, dtype, tensorBytes);
                            label = "ffn_gate ← GATES reconstruct";
                            break;
                        case "ffn_up.weight":
                            WriteFloatMatrixToTensorBytes(Wup, (int)rows, (int)cols, dtype, tensorBytes);
                            label = "ffn_up ← UP_PROJECTS reconstruct";
                            break;
                        case "ffn_down.weight":
                            WriteTransposedFloatMatrixToTensorBytes(
                                WdnT, srcRows: interm, srcCols: dModel,
                                dstRows: (int)rows, dstCols: (int)cols, dtype, tensorBytes);
                            label = "ffn_down ← DOWN_PROJECTS reconstruct (transposed)";
                            break;
                    }
                }
            }

            // Write to GGUF — dims in column-major order (inner first)
            nuint[] ggufDims = cols > 1
                ? [(nuint)cols, (nuint)rows]
                : [(nuint)rows];

            unsafe
            {
                fixed (nuint* dimsPtr = ggufDims)
                fixed (byte*  dataPtr = tensorBytes)
                    SynthInterop.GgufWriterAddTensor(gguf, HfToGgmlName(name), dtype, dimsPtr, (nuint)ggufDims.Length, dataPtr);
            }

            tensorsDone++;
            if (tensorsDone % 10 == 0 || tensorsDone == 1)
                Console.WriteLine($"  [{tensorsDone}/{tensorCount}] {name} rows={rows} cols={cols} {label} {sw.Elapsed.TotalSeconds:F1}s");
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

    // === synthesize tinyllama-passthrough: REAL weights → our GGUF writer (no substrate) ===
    // Isolates format/metadata/arch_template correctness from substrate data quality.
    // Each tensor is transcoded to the arch_template's declared dtype (norms bf16→f32,
    // weights bf16 passthrough) to match llama.cpp's GGUF conventions.
    private static async Task<int> SynthesizeTinyLlamaPassthroughAsync(string outputPath, bool allF32 = false, double sparseTol = 0.0)
    {
        Console.WriteLine($"synthesize tinyllama {(sparseTol > 0 ? $"SPARSE(tol={sparseTol:0.000})" : "PASSTHROUGH")} (real weights → GGUF{(allF32 || sparseTol > 0 ? ", F32" : "")}) → {outputPath}");
        /* LlamaTokenizerParser now requires the perfcache (TextDecomposer +
         * HashComposer in Parse). */
        CodepointPerfcache.Load(ResolveBlob());
        string configPath      = Path.Combine(TinyLlamaDir, "config.json");
        string tokenizerPath   = Path.Combine(TinyLlamaDir, "tokenizer.json");
        string safetensorsPath = Path.Combine(TinyLlamaDir, "model.safetensors");
        if (!File.Exists(configPath) || !File.Exists(tokenizerPath) || !File.Exists(safetensorsPath))
            return Fail($"model files not found under {TinyLlamaDir}");

        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        var recipe = LlamaRecipeExtractor.Parse(configPath);

        byte[] configJson = File.ReadAllBytes(configPath);
        IntPtr recipeHandle, tmplHandle;
        var specs = new TensorSpec[400];
        int tensorCount;
        unsafe
        {
            fixed (byte* jsonPtr = configJson)
                recipeHandle = SynthInterop.RecipeParse(jsonPtr, (nuint)configJson.Length);
            if (recipeHandle == IntPtr.Zero) return Fail("recipe_parse returned null");
            tmplHandle = SynthInterop.ArchTemplateLoad("llama");
            if (tmplHandle == IntPtr.Zero) return Fail("arch_template_load returned null");
            fixed (TensorSpec* specsPtr = specs)
                tensorCount = SynthInterop.ArchTemplateRequiredTensors(
                    tmplHandle, recipeHandle, specsPtr, (nuint)specs.Length);
        }
        if (tensorCount <= 0) return Fail($"arch_template_required_tensors returned {tensorCount}");
        Console.WriteLine($"  arch template: {tensorCount} tensor slots");

        var refs = SafetensorsContainerParser.ParseHeader(safetensorsPath);
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(refs.Count, StringComparer.Ordinal);
        foreach (var r in refs) refMap[r.Name] = r;

        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero) return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens);

        using var fs = new FileStream(safetensorsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: false);
        var sw = Stopwatch.StartNew();
        int done = 0, missing = 0;
        long keptTotal = 0, sparsedElems = 0;   // sparsity accounting (interior tensors only)
        for (int i = 0; i < tensorCount; i++)
        {
            string name; ulong rows, cols; int dtype;
            unsafe
            {
                var spec = specs[i];
                name  = Marshal.PtrToStringUTF8((IntPtr)spec.Name) ?? "";
                rows  = spec.Rank >= 1 ? spec.Shape[0] : 1;
                cols  = spec.Rank >= 2 ? spec.Shape[1] : 1;
                dtype = spec.Dtype;
            }
            long nElem = (long)rows * (long)cols;
            int outDtype = allF32 ? 0 : dtype;   // arch_template dtype (bf16 weights / f32 norms) unless allF32
            // Interior weight tensors (attention + MLP projections) are the sparsity target;
            // the embedding frame, lm_head, and norms stay dense (cheap + critical).
            bool interior = name.Contains(".self_attn.") || name.Contains(".mlp.");

            byte[] outBytes;
            if (refMap.TryGetValue(name, out var tref))
            {
                byte[] raw = ReadTensorBytes(fs, tref);
                if (sparseTol > 0 && interior)
                {
                    float[] fv = BytesToF32(raw, tref.Dtype, nElem);
                    long kept = Sparsify(fv, sparseTol);
                    keptTotal += kept; sparsedElems += nElem;
                    outBytes = outDtype == 2 ? F32ToBf16Bytes(fv) : F32ToBytes(fv);
                }
                else
                {
                    outBytes = TranscodeToDtype(raw, tref.Dtype, outDtype, nElem);
                }
            }
            else
            {
                missing++;
                outBytes = new byte[nElem * (outDtype == 0 ? 4L : 2L)];
                Console.WriteLine($"  MISSING in safetensors: {name} → zero-filled");
            }

            nuint[] ggufDims = cols > 1 ? [(nuint)cols, (nuint)rows] : [(nuint)rows];
            unsafe
            {
                fixed (nuint* dimsPtr = ggufDims)
                fixed (byte*  dataPtr = outBytes)
                    SynthInterop.GgufWriterAddTensor(gguf, HfToGgmlName(name), outDtype, dimsPtr, (nuint)ggufDims.Length, dataPtr);
            }

            done++;
            if (done == 1 || done % 50 == 0)
                Console.WriteLine($"  [{done}/{tensorCount}] {name} rows={rows} cols={cols} dt={dtype} {sw.Elapsed.TotalSeconds:F1}s");
        }

        int rc = SynthInterop.GgufWriterFinalize(gguf);
        SynthInterop.GgufWriterFree(gguf);
        SynthInterop.ArchTemplateFree(tmplHandle);
        SynthInterop.RecipeFree(recipeHandle);
        if (rc != 0) return Fail($"gguf_writer_finalize failed (rc={rc})");

        long fileSize = new FileInfo(outputPath).Length;
        Console.WriteLine($"{(sparseTol > 0 ? "sparse" : "passthrough")} complete: {outputPath} ({fileSize / 1048576.0:F0} MB), "
            + $"{done} tensors, {missing} missing, {sw.Elapsed.TotalSeconds:F1}s");
        if (sparseTol > 0 && sparsedElems > 0)
            Console.WriteLine($"  SPARSITY (interior weights @ tol={sparseTol:0.000}): kept {keptTotal:N0} / {sparsedElems:N0} "
                + $"= {100.0 * keptTotal / sparsedElems:F1}% nonzero (dropped {100.0 * (1.0 - (double)keptTotal / sparsedElems):F1}%)");
        await Task.CompletedTask;
        return 0;
    }

    // Zero the smallest-magnitude entries that together carry <= tol^2 of the tensor's
    // Frobenius energy (energy-based, not a flat threshold). Returns the kept (nonzero) count.
    private static long Sparsify(float[] data, double tol)
    {
        int n = data.Length;
        double total = 0; for (int i = 0; i < n; i++) { double v = data[i]; total += v * v; }
        if (total <= 0) return n;
        double budget = tol * tol * total;
        var mag = new float[n];
        for (int i = 0; i < n; i++) mag[i] = MathF.Abs(data[i]);
        Array.Sort(mag);
        double acc = 0; float cutoff = -1f;
        for (int i = 0; i < n; i++) { double v = mag[i]; if (acc + v * v > budget) break; acc += v * v; cutoff = mag[i]; }
        if (cutoff < 0) return n;            // nothing droppable within budget
        long kept = 0;
        for (int i = 0; i < n; i++) { if (MathF.Abs(data[i]) <= cutoff) data[i] = 0f; else kept++; }
        return kept;
    }

    // Decode raw tensor bytes (BF16 or F32) to a float[] of nElem values.
    private static float[] BytesToF32(byte[] raw, string srcDtype, long nElem)
    {
        var o = new float[nElem];
        if (srcDtype == "F32") { Buffer.BlockCopy(raw, 0, o, 0, (int)(nElem * 4)); return o; }
        for (long i = 0; i < nElem; i++)
        {
            ushort bf = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
            o[i] = BitConverter.UInt32BitsToSingle((uint)bf << 16);
        }
        return o;
    }

    private static byte[] F32ToBytes(float[] data)
    {
        var o = new byte[(long)data.Length * 4];
        Buffer.BlockCopy(data, 0, o, 0, o.Length);
        return o;
    }

    // Encode a float[] as BF16 bytes (truncate to the upper 16 bits of each f32).
    private static byte[] F32ToBf16Bytes(float[] data)
    {
        var o = new byte[(long)data.Length * 2];
        for (long i = 0; i < data.Length; i++)
        {
            uint b = BitConverter.SingleToUInt32Bits(data[i]);
            ushort bf = (ushort)(b >> 16);
            o[i * 2] = (byte)(bf & 0xFF);
            o[i * 2 + 1] = (byte)(bf >> 8);
        }
        return o;
    }

    private static byte[] ReadTensorBytes(FileStream fs, SafetensorsContainerParser.TensorReference tref)
    {
        byte[] buf = new byte[tref.DataLength];
        fs.Seek(tref.AbsoluteDataStart, SeekOrigin.Begin);
        int total = 0;
        while (total < buf.Length)
        {
            int n = fs.Read(buf, total, buf.Length - total);
            if (n == 0) throw new IOException($"safetensors: truncated data for {tref.Name}");
            total += n;
        }
        return buf;
    }

    // Transcode raw tensor bytes from a safetensors dtype to the target GGUF dtype (0=f32, 2=bf16).
    private static byte[] TranscodeToDtype(byte[] raw, string srcDtype, int dstDtype, long nElem)
    {
        bool srcBf16 = srcDtype == "BF16";
        bool srcF32  = srcDtype == "F32";

        if (dstDtype == 2) // target bf16
        {
            if (srcBf16) return raw;
            if (srcF32)
            {
                var o = new byte[nElem * 2];
                for (long i = 0; i < nElem; i++)
                {
                    uint b = (uint)(raw[i*4] | (raw[i*4+1] << 8) | (raw[i*4+2] << 16) | (raw[i*4+3] << 24));
                    ushort bf = (ushort)(b >> 16);
                    o[i*2] = (byte)(bf & 0xFF); o[i*2+1] = (byte)(bf >> 8);
                }
                return o;
            }
        }
        else if (dstDtype == 0) // target f32
        {
            if (srcF32) return raw;
            if (srcBf16)
            {
                var o = new byte[nElem * 4];
                for (long i = 0; i < nElem; i++)
                {
                    ushort bf = (ushort)(raw[i*2] | (raw[i*2+1] << 8));
                    uint b = (uint)bf << 16;
                    o[i*4]   = (byte)(b & 0xFF);
                    o[i*4+1] = (byte)((b >> 8)  & 0xFF);
                    o[i*4+2] = (byte)((b >> 16) & 0xFF);
                    o[i*4+3] = (byte)((b >> 24) & 0xFF);
                }
                return o;
            }
        }
        throw new NotSupportedException($"transcode {srcDtype} → dtype {dstDtype} unsupported");
    }

    // Map HuggingFace safetensors tensor names → GGML/llama.cpp names.
    // arch_template emits HF names (to match the source safetensors during ingest);
    // llama.cpp's loader requires the GGML naming scheme, so we rename at GGUF-write time.
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
        return hf; // unknown — pass through unchanged
    }

    /* === Substrate → Recipe-Mold Synthesis Helpers ============================
     *
     * The substrate codec at synthesis: build a sparse typed-edge graph from
     * substrate attestations (cross-kind for the input-direction embedding;
     * per-kind subgraphs for interior reconstruction); spectral-embed via
     * Laplacian eigenmaps of the graph for embed_tokens / output.weight at
     * the recipe's target dim; reconstruct interior tensors via least-squares
     * factorization of `E·Wᵀ·W·Eᵀ ≈ S_kind` (symmetric) or `E·Wqᵀ·Wk·Eᵀ ≈ S_Q`
     * (asymmetric, for GQA). Norm scales come from NORMALIZES per-dim
     * attestations.
     *
     * The same primitives work for any architecture family / modality — the
     * recipe is the mold; the primitives are universal. */

    private sealed record SubstrateAdjacencyData(int[] Rows, int[] Cols, double[] Weights);

    /* Token×token attestation kinds the AI-model interior tensors emit. */
    private static readonly Hash128[] TokenPairAttestationKinds =
    [
        ModelDecomposer.QProjectsKind,
        ModelDecomposer.VProjectsKind,
        ModelDecomposer.OProjectsKind,
        ModelDecomposer.GatesKind,
        ModelDecomposer.UpProjectsKind,
        ModelDecomposer.DownProjectsKind,
    ];

    /// <summary>
    /// Query token-pair attestations from substrate and build a sparse adjacency
    /// in COO form. Each row's contribution is `effective_mu = max(0, (rating −
    /// 2*rd) / 1e9)` (the Glicko-2 lower bound per ADR 0036). Multiple rows for
    /// the same (subject_token, object_token) tuple — from per-kind aggregation
    /// or cross-source consensus — get summed into one edge weight in C#.
    ///
    /// kindFilter == null → all token-pair kinds (cross-kind aggregate for
    /// input-direction embedding). kindFilter == specific kind → that kind's
    /// subgraph for interior reconstruction.
    /// </summary>
    private static async Task<SubstrateAdjacencyData> BuildSubstrateAdjacencyAsync(
        NpgsqlDataSource ds,
        Hash128 modelSourceId,
        Dictionary<Hash128, int> entityToToken,
        Hash128? kindFilter)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd  = conn.CreateCommand();
        if (kindFilter is { } kf)
        {
            cmd.CommandText =
                """
                SELECT subject_id, object_id, rating, rd
                FROM laplace.attestations
                WHERE source_id = $1 AND kind_id = $2
                """;
            cmd.Parameters.AddWithValue(modelSourceId.ToBytes());
            cmd.Parameters.AddWithValue(kf.ToBytes());
        }
        else
        {
            /* Cross-kind: include all the token-pair kinds. */
            cmd.CommandText =
                """
                SELECT subject_id, object_id, rating, rd
                FROM laplace.attestations
                WHERE source_id = $1
                  AND kind_id = ANY($2)
                """;
            cmd.Parameters.AddWithValue(modelSourceId.ToBytes());
            var kindBytes = TokenPairAttestationKinds.Select(k => k.ToBytes()).ToArray();
            cmd.Parameters.AddWithValue(kindBytes);
        }

        var acc = new Dictionary<(int r, int c), double>(1 << 16);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var subjBytes = (byte[])rdr[0];
            var objBytes  = (byte[])rdr[1];
            long rating   = rdr.GetInt64(2);
            long rdVal    = rdr.GetInt64(3);

            double effMu = Math.Max(0.0, (rating - 2.0 * rdVal) / 1e9);
            if (effMu <= 0.0) continue;

            var subjId = Hash128FromBytes(subjBytes);
            var objId  = Hash128FromBytes(objBytes);
            if (!entityToToken.TryGetValue(subjId, out int r)) continue;
            if (!entityToToken.TryGetValue(objId,  out int c)) continue;

            var key = (r, c);
            if (acc.TryGetValue(key, out double existing))
                acc[key] = existing + effMu;
            else
                acc[key] = effMu;
        }

        int n = acc.Count;
        var rows    = new int[n];
        var cols    = new int[n];
        var weights = new double[n];
        int idx = 0;
        foreach (var ((r, c), w) in acc)
        {
            rows[idx] = r; cols[idx] = c; weights[idx] = w; idx++;
        }
        return new SubstrateAdjacencyData(rows, cols, weights);
    }

    /// <summary>
    /// Build the output-direction adjacency for lm_head synthesis. Edges come
    /// from PROJECTION_OUTPUT physicality 4D proximity in the substrate
    /// canonical frame — tokens whose output-direction positions are close
    /// share an edge. Uses raw Euclidean distance in 4D (PostGIS could
    /// alternatively serve this via ST_DWithin/GIST, but for vocab=32K the
    /// pairwise scan is tractable here).
    /// </summary>
    private static async Task<SubstrateAdjacencyData> BuildOutputDirectionAdjacencyAsync(
        NpgsqlDataSource ds,
        Hash128 modelSourceId,
        Dictionary<Hash128, int> entityToToken,
        int kNearest = 32)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT entity_id, ST_X(coord), ST_Y(coord), ST_Z(coord), ST_M(coord)
            FROM laplace.physicalities
            WHERE kind = 4 AND source_id = $1
            """;
        cmd.Parameters.AddWithValue(modelSourceId.ToBytes());

        var tokenIdxToCoord = new Dictionary<int, (double x, double y, double z, double m)>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var entityId = Hash128FromBytes((byte[])rdr[0]);
            if (!entityToToken.TryGetValue(entityId, out int t)) continue;
            tokenIdxToCoord[t] = (rdr.GetDouble(1), rdr.GetDouble(2),
                                  rdr.GetDouble(3), rdr.GetDouble(4));
        }
        if (tokenIdxToCoord.Count == 0)
            return new SubstrateAdjacencyData(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<double>());

        /* For each token, find its kNearest neighbors by 4D Euclidean distance.
         * O(n²) — for vocab=32K this is ~1B ops, ~few seconds on modern CPU. */
        var arr = tokenIdxToCoord.Select(kv => (idx: kv.Key, c: kv.Value)).ToArray();
        int n = arr.Length;

        var rowsList = new List<int>(n * kNearest);
        var colsList = new List<int>(n * kNearest);
        var weightsList = new List<double>(n * kNearest);

        var distBuf = new (double dist, int idx)[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i == j) { distBuf[j] = (double.PositiveInfinity, j); continue; }
                double dx = arr[i].c.x - arr[j].c.x;
                double dy = arr[i].c.y - arr[j].c.y;
                double dz = arr[i].c.z - arr[j].c.z;
                double dm = arr[i].c.m - arr[j].c.m;
                distBuf[j] = (dx*dx + dy*dy + dz*dz + dm*dm, j);
            }
            /* nth_element-style partial sort */
            int k = Math.Min(kNearest, n - 1);
            Array.Sort(distBuf, 0, n, Comparer<(double dist, int idx)>.Create(
                (a, b) => a.dist.CompareTo(b.dist)));
            for (int neighbor = 0; neighbor < k; neighbor++)
            {
                var (dist, j) = distBuf[neighbor];
                if (double.IsInfinity(dist)) break;
                /* Edge weight: inverse distance (closer = stronger). */
                double w = 1.0 / (1.0 + dist);
                rowsList.Add(arr[i].idx);
                colsList.Add(arr[j].idx);
                weightsList.Add(w);
            }
        }

        return new SubstrateAdjacencyData(
            rowsList.ToArray(), colsList.ToArray(), weightsList.ToArray());
    }

    /// <summary>
    /// Spectral embedding: top-`targetDim` Laplacian eigenvectors of the sparse
    /// graph. Result is `[n × targetDim]` row-major doubles. Eigenvectors are
    /// scaled by `sqrt(n) * 0.02` to match typical transformer embedding init
    /// magnitude (unit-normalized eigenvectors are far smaller than what GGUF
    /// expects).
    /// </summary>
    private static unsafe double[] ComputeSpectralEmbedding(
        SubstrateAdjacencyData adj, int n, int targetDim)
    {
        if (adj.Rows.Length == 0 || n == 0 || targetDim == 0)
            return new double[(long)n * targetDim];

        var emb = new double[(long)n * targetDim];
        fixed (int* rowsPtr = adj.Rows)
        fixed (int* colsPtr = adj.Cols)
        fixed (double* wPtr = adj.Weights)
        fixed (double* embPtr = emb)
        {
            int rc = DynamicsInterop.LaplacianEigenmapsFromSparseGraph(
                rowsPtr, colsPtr, wPtr,
                (nuint)adj.Rows.Length, (nuint)n, (nuint)targetDim,
                embPtr);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"laplacian_eigenmaps_from_sparse_graph returned {rc}");
        }

        /* Scale to match transformer embed init magnitude. Spectral
         * eigenvectors are unit-norm; multiply by sqrt(targetDim) * 0.02
         * so each row's L2 norm is ~sqrt(targetDim) * 0.02 — the typical
         * Xavier/He init scale for transformer embedding rows. */
        double scale = Math.Sqrt(targetDim) * 0.02;
        for (long i = 0; i < emb.LongLength; i++) emb[i] *= scale;
        return emb;
    }

    /// <summary>
    /// Symmetric interior-tensor reconstruction:
    /// recover W [outDim × N] such that `E·Wᵀ·W·Eᵀ ≈ S_kind`.
    /// </summary>
    private static unsafe float[] ReconstructInteriorTensorSymmetric(
        double[] E, SubstrateAdjacencyData kindAdj,
        int vocab, int N, int outDim, double lambda)
    {
        var W = new float[(long)outDim * N];
        if (kindAdj.Rows.Length == 0) return W;  // zero-filled

        fixed (double* ePtr = E)
        fixed (int* rowsPtr = kindAdj.Rows)
        fixed (int* colsPtr = kindAdj.Cols)
        fixed (double* wPtr = kindAdj.Weights)
        fixed (float* wOutPtr = W)
        {
            int rc = SynthInterop.ReconstructWFromTokenPairAttestations(
                ePtr, (nuint)vocab, (nuint)N,
                rowsPtr, colsPtr, wPtr, (nuint)kindAdj.Rows.Length,
                (nuint)outDim, lambda, wOutPtr);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"reconstruct_w_from_token_pair_attestations returned {rc}");
        }
        return W;
    }

    /// <summary>
    /// Asymmetric (joint-bilinear) reconstruction for Q_PROJECTS:
    /// recover Wq [outDimQ × N] AND Wk [outDimK × N] such that
    /// `E·Wqᵀ·Wk·Eᵀ ≈ S_Q`. TinyLlama GQA has Wq=[2048×2048] and
    /// Wk=[256×2048] — symmetric collapse would destroy behavioral fidelity.
    /// </summary>
    private static unsafe (float[] Wq, float[] Wk) ReconstructInteriorTensorAsymmetric(
        double[] E, SubstrateAdjacencyData kindAdj,
        int vocab, int N, int outDimQ, int outDimK, double lambda)
    {
        var Wq = new float[(long)outDimQ * N];
        var Wk = new float[(long)outDimK * N];
        if (kindAdj.Rows.Length == 0) return (Wq, Wk);  // both zero-filled

        fixed (double* ePtr = E)
        fixed (int* rowsPtr = kindAdj.Rows)
        fixed (int* colsPtr = kindAdj.Cols)
        fixed (double* wPtr = kindAdj.Weights)
        fixed (float* wqPtr = Wq)
        fixed (float* wkPtr = Wk)
        {
            int rc = SynthInterop.ReconstructQkFromTokenPairAttestations(
                ePtr, (nuint)vocab, (nuint)N,
                rowsPtr, colsPtr, wPtr, (nuint)kindAdj.Rows.Length,
                (nuint)outDimQ, (nuint)outDimK, lambda, wqPtr, wkPtr);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"reconstruct_qk_from_token_pair_attestations returned {rc}");
        }
        return (Wq, Wk);
    }

    /// <summary>
    /// Query NORMALIZES attestations for this model source and return per-
    /// (layer, role) full dModel-length scale vectors. Layer = -1 represents
    /// the final `model.norm`. Maps the context-id back to (layer, role, dim)
    /// by reconstructing the canonical context entity ids.
    /// </summary>
    private static async Task<Dictionary<(int layer, string role), float[]>>
        QueryNormalizesPerDimAsync(NpgsqlDataSource ds, Hash128 modelSourceId,
                                    int numLayers, int dModel)
    {
        var result = new Dictionary<(int layer, string role), float[]>();
        var ctxToSlot = new Dictionary<Hash128, (int layer, string role, int dim)>();
        var roles = new[] { "input_layernorm", "post_attention_layernorm" };
        for (int L = 0; L < numLayers; L++)
        {
            foreach (var role in roles)
            {
                for (int d = 0; d < dModel; d++)
                {
                    var ctxId = LlamaWeightExtractor.LayerNormContextId(L, role, d);
                    ctxToSlot[ctxId] = (L, role, d);
                }
            }
        }
        for (int d = 0; d < dModel; d++)
        {
            var ctxId = LlamaWeightExtractor.LayerNormContextId(-1, "model_norm", d);
            ctxToSlot[ctxId] = (-1, "model_norm", d);
        }

        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT context_id, rating
            FROM laplace.attestations
            WHERE source_id = $1 AND kind_id = $2 AND context_id IS NOT NULL
            """;
        cmd.Parameters.AddWithValue(modelSourceId.ToBytes());
        cmd.Parameters.AddWithValue(ModelDecomposer.NormalizesKind.ToBytes());

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var ctxBytes = (byte[])rdr[0];
            long rating  = rdr.GetInt64(1);
            var ctxId = Hash128FromBytes(ctxBytes);
            if (!ctxToSlot.TryGetValue(ctxId, out var slot)) continue;

            var key = (slot.layer, slot.role);
            if (!result.TryGetValue(key, out var vec))
            {
                vec = new float[dModel];
                result[key] = vec;
            }
            vec[slot.dim] = (float)(rating / 1e9);
        }
        return result;
    }

    /// <summary>Write a row-major double[rows × cols] matrix to tensorBytes at
    /// the GGUF dtype (0=f32, 2=bf16).</summary>
    private static void WriteDoubleMatrixToTensorBytes(
        double[] matrix, int rows, int cols, int dtype, byte[] tensorBytes)
    {
        long n = (long)rows * cols;
        if (dtype == 0)
        {
            for (long i = 0; i < n; i++)
            {
                float fv = (float)matrix[i];
                uint bits = BitConverter.SingleToUInt32Bits(fv);
                int off = (int)(i * 4);
                tensorBytes[off + 0] = (byte)(bits & 0xFF);
                tensorBytes[off + 1] = (byte)((bits >>  8) & 0xFF);
                tensorBytes[off + 2] = (byte)((bits >> 16) & 0xFF);
                tensorBytes[off + 3] = (byte)((bits >> 24) & 0xFF);
            }
        }
        else
        {
            for (long i = 0; i < n; i++)
            {
                float fv = (float)matrix[i];
                uint bits = BitConverter.SingleToUInt32Bits(fv);
                ushort bf16 = (ushort)(bits >> 16);
                int off = (int)(i * 2);
                tensorBytes[off + 0] = (byte)(bf16 & 0xFF);
                tensorBytes[off + 1] = (byte)(bf16 >> 8);
            }
        }
    }

    /// <summary>Write a row-major float[rows × cols] matrix to tensorBytes.</summary>
    private static void WriteFloatMatrixToTensorBytes(
        float[] matrix, int rows, int cols, int dtype, byte[] tensorBytes)
    {
        long n = (long)rows * cols;
        if (dtype == 0)
        {
            for (long i = 0; i < n; i++)
            {
                uint bits = BitConverter.SingleToUInt32Bits(matrix[i]);
                int off = (int)(i * 4);
                tensorBytes[off + 0] = (byte)(bits & 0xFF);
                tensorBytes[off + 1] = (byte)((bits >>  8) & 0xFF);
                tensorBytes[off + 2] = (byte)((bits >> 16) & 0xFF);
                tensorBytes[off + 3] = (byte)((bits >> 24) & 0xFF);
            }
        }
        else
        {
            for (long i = 0; i < n; i++)
            {
                uint bits = BitConverter.SingleToUInt32Bits(matrix[i]);
                ushort bf16 = (ushort)(bits >> 16);
                int off = (int)(i * 2);
                tensorBytes[off + 0] = (byte)(bf16 & 0xFF);
                tensorBytes[off + 1] = (byte)(bf16 >> 8);
            }
        }
    }

    /// <summary>Write the transpose of source[srcRows × srcCols] into
    /// tensorBytes shaped as [dstRows × dstCols] = [srcCols × srcRows].</summary>
    private static void WriteTransposedFloatMatrixToTensorBytes(
        float[] source, int srcRows, int srcCols,
        int dstRows, int dstCols, int dtype, byte[] tensorBytes)
    {
        if (dstRows != srcCols || dstCols != srcRows)
            throw new ArgumentException(
                $"Transpose shape mismatch: src=[{srcRows}×{srcCols}], " +
                $"dst=[{dstRows}×{dstCols}]");
        int elemSize = dtype == 0 ? 4 : 2;
        for (int r = 0; r < dstRows; r++)
        {
            for (int c = 0; c < dstCols; c++)
            {
                float v = source[c * srcCols + r];   // src row=c, col=r
                int off = (r * dstCols + c) * elemSize;
                if (dtype == 0)
                {
                    uint bits = BitConverter.SingleToUInt32Bits(v);
                    tensorBytes[off + 0] = (byte)(bits & 0xFF);
                    tensorBytes[off + 1] = (byte)((bits >>  8) & 0xFF);
                    tensorBytes[off + 2] = (byte)((bits >> 16) & 0xFF);
                    tensorBytes[off + 3] = (byte)((bits >> 24) & 0xFF);
                }
                else
                {
                    uint bits = BitConverter.SingleToUInt32Bits(v);
                    ushort bf16 = (ushort)(bits >> 16);
                    tensorBytes[off + 0] = (byte)(bf16 & 0xFF);
                    tensorBytes[off + 1] = (byte)(bf16 >> 8);
                }
            }
        }
    }

    /// <summary>Write per-dim norm scale vector to tensorBytes. Defaults to
    /// 1.0 (identity) per dim when `scale` is null. Norms are f32 per the
    /// arch_template convention.</summary>
    private static void FillPerDimNorm(byte[] tensorBytes, float[]? scale, int dModel, int dtype)
    {
        for (int d = 0; d < dModel; d++)
        {
            float v = (scale != null && d < scale.Length && scale[d] != 0.0f) ? scale[d] : 1.0f;
            uint bits = BitConverter.SingleToUInt32Bits(v);
            if (dtype == 0)
            {
                int off = d * 4;
                tensorBytes[off + 0] = (byte)(bits & 0xFF);
                tensorBytes[off + 1] = (byte)((bits >>  8) & 0xFF);
                tensorBytes[off + 2] = (byte)((bits >> 16) & 0xFF);
                tensorBytes[off + 3] = (byte)((bits >> 24) & 0xFF);
            }
            else
            {
                ushort bf16 = (ushort)(bits >> 16);
                int off = d * 2;
                tensorBytes[off + 0] = (byte)(bf16 & 0xFF);
                tensorBytes[off + 1] = (byte)(bf16 >> 8);
            }
        }
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
        // Tokenizer control flags a real Llama conversion always writes — without these
        // llama.cpp doesn't prepend BOS (model degenerates) and mishandles the SPM
        // leading-space prefix (output loses spaces). LlamaTokenizer defaults.
        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_bos_token",    1);
        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_eos_token",    0);
        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_space_prefix", 1);

        int n = tokens.Count;

        // Authoritative tokenizer vocab from the SentencePiece model (real pieces +
        // scores + types). The HF tokenizer.json is BPE-format with no scores; emitting
        // zero scores under vocab type=SPM breaks tokenization in llama.cpp.
        string spPath = Path.Combine(TinyLlamaDir, "tokenizer.model");
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

        // Real chat template (so the server's chat endpoint uses TinyLlama's template,
        // not a generic ChatML fallback).
        string cfgPath = Path.Combine(TinyLlamaDir, "tokenizer_config.json");
        if (File.Exists(cfgPath))
        {
            using var cfg = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(cfgPath));
            if (cfg.RootElement.TryGetProperty("chat_template", out var ct)
                && ct.ValueKind == System.Text.Json.JsonValueKind.String)
                SynthInterop.GgufWriterAddMetadataStr(gguf, "tokenizer.chat_template", ct.GetString()!);
        }
    }

    // === SentencePiece model (.model protobuf) reader — extracts (piece, score, type)
    // per token id, dependency-free. ModelProto field 1 = repeated SentencePiece
    // { string piece=1; float score=2; Type type=3 (default NORMAL=1) }.
    // SP Type enum values mirror llama.cpp's token types (NORMAL=1, UNKNOWN=2,
    // CONTROL=3, USER_DEFINED=4, UNUSED=5, BYTE=6), so the type passes through directly.
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
                string piece = ""; float score = 0f; int type = 1; /* NORMAL */
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

    // Pack strings in GGUF wire format: uint64_le byte-length + UTF-8 bytes per string.
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

    // GGUF token type: 0=NORMAL, 1=UNKNOWN, 2=CONTROL, 5=BYTE
    private static int ClassifyTokenType(string raw)
    {
        if (raw is "<unk>" or "<UNK>" or "<unknown>") return 1;
        if (raw is "<s>" or "</s>" or "<pad>" or "<bos>" or "<eos>") return 2;
        if (raw.Length == 6 && raw.StartsWith("<0x", StringComparison.Ordinal) && raw.EndsWith('>')) return 5;
        return 0;
    }

    private static async Task<int> IngestUnicodeViaRunnerAsync()
        => await IngestViaRunnerAsync(new UnicodeDecomposer(), "/vault/Data/Unicode", skipLayerCheck: true);

    private static async Task<int> IngestISO639Async()
        => await IngestViaRunnerAsync(new ISODecomposer(), "/vault/Data/ISO639", skipLayerCheck: false);

    private static async Task<int> IngestViaRunnerAsync(
        IDecomposer dec, string ecosystemPath, bool skipLayerCheck)
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var writer = new NpgsqlSubstrateWriter(ds);
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader);

        Console.WriteLine($"ingest {dec.SourceName} via IngestRunner → {ConnString} ...");
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            dec,
            IngestRunOptions.Default with
            {
                SkipLayerOrderingCheck = skipLayerCheck,
                EcosystemPath = ecosystemPath,
            },
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

        long modelAtts = await Scalar(
            "SELECT count(*) FROM laplace.attestations WHERE source_id = @p",
            ModelDecomposer.Source.ToBytes());
        if (modelAtts == 0)
        {
            Console.WriteLine("  model attestations    : (none — ingest model)");
            return;
        }

        Console.WriteLine($"  model attestations    : {modelAtts,9:N0}  (source TinyLlama)");
        async Task<long> KindCount(Hash128 kind)
        {
            await using var c = conn.CreateCommand();
            c.CommandText =
                "SELECT count(*) FROM laplace.attestations WHERE source_id = @s AND kind_id = @k";
            c.Parameters.AddWithValue("s", ModelDecomposer.Source.ToBytes());
            c.Parameters.AddWithValue("k", kind.ToBytes());
            return (long)(await c.ExecuteScalarAsync())!;
        }

        (string label, Hash128 kind)[] modelKinds =
        [
            ("EMBEDS",          ModelDecomposer.EmbedsKind),
            ("Q_PROJECTS",      ModelDecomposer.QProjectsKind),
            ("V_PROJECTS",      ModelDecomposer.VProjectsKind),
            ("O_PROJECTS",      ModelDecomposer.OProjectsKind),
            ("GATES",           ModelDecomposer.GatesKind),
            ("UP_PROJECTS",     ModelDecomposer.UpProjectsKind),
            ("DOWN_PROJECTS",   ModelDecomposer.DownProjectsKind),
            ("OUTPUT_PROJECTS", ModelDecomposer.OutputProjectsKind),
        ];
        foreach (var (label, kind) in modelKinds)
        {
            long n = await KindCount(kind);
            Console.WriteLine($"  └ {label,-16}: {n,9:N0}");
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
