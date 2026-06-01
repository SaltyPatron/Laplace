using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.ISO;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.OMW;
using Laplace.Decomposers.Tatoeba;
using Laplace.Decomposers.UD;
using Laplace.Decomposers.Wiktionary;
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
    private static string ConnString
    {
        get
        {
            // Always include error detail — without it Npgsql redacts the FK-violating
            // row's data ("DETAIL: Detail redacted as it may contain sensitive data"),
            // which makes substrate-debug iteration painful. The substrate's per-row
            // bytea(16) values are not actually sensitive — they're content-addressed
            // hashes of public substrate canon.
            var s = Environment.GetEnvironmentVariable("LAPLACE_DB")
                ?? "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace";
            if (!s.Contains("Include Error Detail", StringComparison.OrdinalIgnoreCase))
                s += ";Include Error Detail=true";
            // The substrate inspection SRFs (laplace.entity_facets, attestations_out, …)
            // reference their tables unqualified, so the laplace schema must be on the
            // session search_path for them to resolve.
            if (!s.Contains("Search Path", StringComparison.OrdinalIgnoreCase))
                s += ";Search Path=laplace,public";
            return s;
        }
    }

    /// <summary>Read a positive integer tuning knob from the environment,
    /// falling back to <paramref name="fallback"/> when unset or unparseable.
    /// Same env-override convention as <see cref="ConnString"/>.</summary>
    private static int EnvInt(string name, int fallback, int min)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var v) && v >= min ? v : fallback;
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "usage: laplace <command> [args]\n"
                + "  seed-unicode\n"
                + "  ingest <source> [path]            (unicode | iso639 | wordnet | omw | ud | model)\n"
                + "  synthesize substrate <recipe.json> [output.gguf] [--source-scope <ids>] [--format <name>]\n"
                + "  synthesize passthrough <model-dir> [output.gguf] [--f32] [--sparse-tol <tol>]\n"
                + "  decompose <text>\n"
                + "  inspect <text>\n"
                + "  roundtrip <file> [out]\n"
                + "  db-roundtrip <file>\n"
                + "  qk-bench <model-dir>             (profile the QK kernel on real weights; no DB)\n"
                + "  stats");
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
                "inspect"      => await InspectAsync(string.Join(' ', args[1..])),
                "roundtrip"    => Roundtrip(args.Length > 1 ? args[1] : "", args.Length > 2 ? args[2] : null),
                "db-roundtrip" => await DbRoundtripAsync(args.Length > 1 ? args[1] : ""),
                "stats"        => await StatsAsync(),
                "rebuild-consensus" => await RebuildConsensusAsync(),
                "qk-bench"     => QkBenchCmd(args.Length > 1 ? args[1] : ""),
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

    // Materialize the consensus layer from the attestations evidence (S2).
    // Called after ingestion so cross-witness consensus is current before
    // inference / synthesis reads it. Batch rebuild via laplace.rebuild_consensus().
    private static async Task<int> RebuildConsensusAsync()
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;  // batch Glicko-2 accumulation over all evidence can exceed the 30s default
        cmd.CommandText = "SELECT laplace.rebuild_consensus()";
        var n = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        Console.WriteLine($"consensus rebuilt: {n:N0} rows");
        return 0;
    }

    // === qk-bench: profile the QK kernel on real weights (no DB, no ingest harness) ===
    private static int QkBenchCmd(string modelDir)
    {
        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir))
            return Fail($"usage: laplace qk-bench <model-dir>  (not found: '{modelDir}')");
        QkBench.Run(modelDir);
        return 0;
    }

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

    private static string Hex(Hash128 h) => $"{h.Hi:x16}{h.Lo:x16}";

    private static Hash128 ReadHash16(byte[] b) =>
        new Hash128(BitConverter.ToUInt64(b, 0), BitConverter.ToUInt64(b, 8));

    // === inspect: resolve text -> entity via the engine, read its substrate facets ===
    // The glass-box, usable by hand: text → correct merkle entity id (engine
    // TextDecomposer + HashComposer, not SQL), then its glome physicalities and
    // Glicko-2-rated attestation neighborhood straight from the DB.
    private static async Task<int> InspectAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return Fail("usage: laplace inspect <text>");
        CodepointPerfcache.Load(ResolveBlob());

        using var tree = TextDecomposer.Run(text);
        unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

        // codepoint id -> char, for readable rendering of atomic subjects/objects
        var idToCp = new Dictionary<Hash128, uint>(1_114_112);
        var recs = CodepointPerfcache.Records;
        for (int i = 0; i < recs.Length; i++) idToCp[recs[i].Hash] = recs[i].Codepoint;
        string Render(Hash128 h) =>
            idToCp.TryGetValue(h, out var cp) ? $"'{char.ConvertFromUtf32((int)cp)}'(U+{cp:X4})" : Hex(h)[..16] + "…";

        var root = tree.GetNode(tree.NaturalUnitIndex());
        Hash128 id = root.Id;
        Console.WriteLine($"inspect \"{text}\"");
        Console.WriteLine($"  engine-resolved id : {Hex(id)}");
        Console.WriteLine($"  tier {root.Tier}, {tree.NodeCount} nodes in the decomposition DAG\n");

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();

        // Identity facet — laplace.entity_facets(id)
        bool exists = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT tier, encode(type_id,'hex') FROM laplace.entity_facets(@id)";
            cmd.Parameters.AddWithValue("id", id.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                exists = true;
                Console.WriteLine($"  ENTITY: present  tier={r.GetInt16(0)}  type_id={r.GetString(1)[..16]}…");
            }
        }
        if (!exists)
        {
            Console.WriteLine("  ENTITY: not in substrate (id is correct, but this n-gram was never ingested)");
            return 0;
        }

        // Glome facet — laplace.entity_physicalities(id)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT kind, x, y, z, m, radius, n_constituents, encode(source_id,'hex') "
                            + "FROM laplace.entity_physicalities(@id)";
            cmd.Parameters.AddWithValue("id", id.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync();
            Console.WriteLine("\n  GLOME (physicalities):");
            int n = 0;
            while (await r.ReadAsync())
            {
                n++;
                Console.WriteLine($"    kind={r.GetInt16(0)}  coord=({r.GetDouble(1):F4},{r.GetDouble(2):F4},{r.GetDouble(3):F4},{r.GetDouble(4):F4})"
                    + $"  r={r.GetDouble(5):F6}  n_constituents={r.GetInt32(6)}  source={r.GetString(7)[..12]}…");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        // Attestation neighborhood — laplace.attestations_out(id) / attestations_in(id)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT kind_id, object_id, source_id, rating, rd, volatility, observation_count "
                            + "FROM laplace.attestations_out(@id)";
            cmd.Parameters.AddWithValue("id", id.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync();
            Console.WriteLine("\n  OUTGOING attestations (this → object), Glicko-2:");
            int n = 0;
            while (await r.ReadAsync())
            {
                n++;
                var kind = ReadHash16((byte[])r[0]);
                var obj  = r.IsDBNull(1) ? Hash128.Zero : ReadHash16((byte[])r[1]);
                Console.WriteLine($"    [{Hex(kind)[..12]}…] → {Render(obj),-24}  μ={r.GetInt64(3)/1e9:F3} rd={r.GetInt64(4)/1e9:F3} σ={r.GetInt64(5)/1e9:F4}"
                    + $"  src={Hex(ReadHash16((byte[])r[2]))[..10]}…  obs={r.GetInt64(6)}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT subject_id, kind_id, source_id, rating, rd, volatility "
                            + "FROM laplace.attestations_in(@id)";
            cmd.Parameters.AddWithValue("id", id.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync();
            Console.WriteLine("\n  INCOMING attestations (subject → this), Glicko-2:");
            int n = 0;
            while (await r.ReadAsync())
            {
                n++;
                var subj = ReadHash16((byte[])r[0]);
                var kind = ReadHash16((byte[])r[1]);
                Console.WriteLine($"    {Render(subj),-24} [{Hex(kind)[..12]}…] → here  μ={r.GetInt64(3)/1e9:F3} rd={r.GetInt64(4)/1e9:F3}"
                    + $"  src={Hex(ReadHash16((byte[])r[2]))[..10]}…");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        return 0;
    }

    // === ingest: IngestRunner + per-source decomposer (ADR 0052) ===
    private static async Task<int> IngestAsync(string[] args)
    {
        string source = args.Length > 0 ? args[0] : "";
        string path   = args.Length > 1 ? args[1] : "";

        if (string.IsNullOrEmpty(source))
            return Fail("usage: laplace ingest <source> [path]  (unicode | iso639 | wordnet | omw | ud | tatoeba | atomic2020 | conceptnet | wiktionary | model)");

        return source.ToLowerInvariant() switch
        {
            "unicode"  => await IngestUnicodeViaRunnerAsync(),
            "iso639"   => await IngestISO639Async(),
            "wordnet"  => await IngestViaRunnerAsync(new WordNetDecomposer(), "/vault/Data/Wordnet", skipLayerCheck: false),
            "omw"      => await IngestViaRunnerAsync(new OMWDecomposer(), "/vault/Data/omw", skipLayerCheck: false),
            "ud"       => await IngestViaRunnerAsync(new UDDecomposer(), "/vault/Data/UD-Treebanks", skipLayerCheck: false),
            "tatoeba"  => await IngestViaRunnerAsync(new TatoebaDecomposer(), "/vault/Data/Tatoeba", skipLayerCheck: false),
            "atomic2020" => await IngestViaRunnerAsync(new Atomic2020Decomposer(), "/vault/Data/Atomic2020", skipLayerCheck: false),
            "conceptnet" => await IngestViaRunnerAsync(new ConceptNetDecomposer(), "/vault/Data/ConceptNet", skipLayerCheck: false),
            "wiktionary" => await IngestViaRunnerAsync(new WiktionaryDecomposer(), "/vault/Data/Wiktionary", skipLayerCheck: false),
            "model"    => await IngestModelAsync(path),
            _ => Fail($"unknown ingest source '{source}' (supported: unicode, iso639, wordnet, omw, ud, tatoeba, atomic2020, conceptnet, wiktionary, model)"),
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

        /* Check if this model source is already ingested — COMPLETES_TO attestations
         * (the FFN/OV key→value memories) only exist after the weight phase completes.
         * MUST be a kind the current ingest actually emits: the corrected path emits
         * COMPLETES_TO (not Q_PROJECTS — the old per-circuit bilinear kind), so the
         * guard keys on COMPLETES_TO. This is what makes re-ingest short-circuit instead
         * of hammering the DB with a second full ingest. Same attestation ID = same
         * content = ON CONFLICT DO NOTHING, so re-running is safe but wasteful. */
        await using (var chkConn = await ds.OpenConnectionAsync())
        {
            await using var chkCmd = chkConn.CreateCommand();
            chkCmd.CommandText =
                "SELECT EXISTS(SELECT 1 FROM laplace.attestations " +
                "WHERE source_id = $1 AND kind_id = $2 LIMIT 1)";
            chkCmd.Parameters.Add(new global::Npgsql.NpgsqlParameter { Value = ModelDecomposer.Source.ToBytes(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
            chkCmd.Parameters.Add(new global::Npgsql.NpgsqlParameter { Value = ModelDecomposer.CompletesToKind.ToBytes(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
            bool alreadyIngested = (bool)(await chkCmd.ExecuteScalarAsync() ?? false);
            if (alreadyIngested)
            {
                // Re-ingesting the same model would double-count its votes and
                // contaminate consensus, so it is refused by design. To re-run
                // (e.g. testing), reset the DB: `just db-fresh` (nuke + migrate +
                // seed-t0). There is intentionally no in-place override.
                Console.WriteLine($"Model already ingested — source entity: {ModelDecomposer.Source}");
                Console.WriteLine($"(re-ingest is refused to prevent consensus contamination; "
                                  + $"reset with 'just db-fresh' to test from scratch)");
                return 0;
            }
        }

        var writer = new NpgsqlSubstrateWriter(ds);
        var reader = new NpgsqlSubstrateReader(ds);
        var loggerFactory = ConsoleLoggerProvider.Factory();
        var runner = new IngestRunner(writer, reader, loggerFactory);
        var dec    = new ModelDecomposer(modelDir);

        // Model ingestion is mechanical and embarrassingly parallel: thousands
        // of weight-tensor intents. Two levers, both env-tunable (same
        // convention as LAPLACE_DB):
        //   LAPLACE_INGEST_BATCH   — max intents buffered per batched apply
        //       (default 1024): one existence pass + one staged COPY/INSERT per
        //       table per batch instead of ~6 round-trips and 3 connection-opens
        //       PER intent. Acts as the upper cap on buffered intents.
        //   LAPLACE_INGEST_COMMIT_ROWS — rows (entities+physicalities+attestations)
        //       per commit (default 100_000). This is the real throughput dial:
        //       intent fan-out is wildly uneven (a single QK head-intent ≈ thousands
        //       of attestations), so batching by intent count makes the COPY payload
        //       swing from KBs to GBs; batching by ROW count pins it (≈ rows × 200 B,
        //       so 100k ≈ 20 MB) regardless of fan-out. Dial up for fewer/larger
        //       COPYs (throughput at scale), down (e.g. 512) for finer-grained
        //       streaming commits + earlier visibility. 0 = batch purely by intent.
        //   LAPLACE_INGEST_WORKERS — concurrent batch appliers (default 1):
        //       safe because NpgsqlSubstrateWriter promotes via a TEMP staging
        //       table + INSERT … ON CONFLICT DO NOTHING, so overlapping novel
        //       ids across workers converge instead of throwing duplicate-key.
        int batchSize  = EnvInt("LAPLACE_INGEST_BATCH",       1024,    min: 1);
        int commitRows = EnvInt("LAPLACE_INGEST_COMMIT_ROWS", 100_000, min: 0);
        // Serial writers by default: model attestations reference token entities emitted
        // in EARLIER intents (the vocab phase), so parallel writers can commit a weight
        // attestation before its subject entity → FK violation. The heavy compute is
        // already multi-core inside the engine kernels (TBB); workers only parallelize
        // DB writes. Safe parallel writes need an entity-phase barrier (follow-up);
        // until then default 1. Override with LAPLACE_INGEST_WORKERS.
        int workers   = EnvInt("LAPLACE_INGEST_WORKERS", 1, min: 1);
        Console.WriteLine(
            $"ingest model {modelDir} via IngestRunner → {ConnString} "
            + $"(workers={workers}, batch={batchSize}, commitRows={commitRows:N0}) ...");
        var sw = Stopwatch.StartNew();

        // Throttled progress: one counter line per ~2s so a long run shows live
        // applied-intents + rate without flooding the log.
        long lastReportMs = 0;
        var progress = new Progress<Laplace.Ingestion.IngestProgress>(p =>
        {
            long now = sw.ElapsedMilliseconds;
            if (now - lastReportMs < 2000) return;
            lastReportMs = now;
            double rate = p.UnitsApplied / Math.Max(0.001, p.Elapsed.TotalSeconds);
            Console.Error.WriteLine(
                $"[progress] {p.UnitsApplied:N0} intents applied"
                + (p.EstimatedTotal is { } tot ? $"/{tot:N0}" : "")
                + $", {p.UnitsFailed:N0} failed, {rate:F0} intents/s, {p.Elapsed.TotalSeconds:F0}s");
        });

        var result = await runner.RunAsync(
            dec,
            IngestRunOptions.Default with
            {
                SkipLayerOrderingCheck = true,
                BatchSize              = batchSize,
                CommitRows             = commitRows,
                ParallelWorkers        = workers,
                Progress               = progress,
            },
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

    // === synthesize: substrate → model package, OR diagnostic passthrough ===
    //
    // Two universal subcommands per ADR 0011 (IArchitectureTemplate / IFormatWriter)
    // + ADR 0043 (composite ModelDecomposer per ContainerFormat × TensorDtypeDecoder
    // × IArchitectureTemplate × ModalityBinder):
    //
    //   synthesize substrate <recipe.json> [out] [--source-scope ...] [--format ...]
    //     Substrate-mediated. Reads the user-authored recipe (GLOSSARY Recipe +
    //     DESIGN VIII custom-recipe schema); queries substrate aggregated typed
    //     attestations under the recipe's source scope; uses the architecture
    //     template's `materialize_tensor(spec, substrate_view) → TensorValues`
    //     (DESIGN.md:660) to distribute consensus values into recipe slots;
    //     emits via the selected IFormatWriter (native safetensors-style per R4;
    //     GGUF / ONNX / TF / PyTorch as compatibility writers per ADR 0059).
    //
    //   synthesize passthrough <model-dir> [out] [--f32] [--sparse-tol <tol>]
    //     Diagnostic. Real weights → our GGUF writer (no substrate). Isolates
    //     format/metadata/arch_template correctness from substrate data quality.
    private static async Task<int> SynthesizeAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (sub == "substrate")
        {
            string recipePath = args.Length > 1 ? args[1] : "";
            string outputPath = args.Length > 2 ? args[2] : "/tmp/laplace-substrate-synth.gguf";
            return await SynthesizeFromSubstrateAsync(recipePath, outputPath);
        }
        if (sub == "passthrough")
        {
            string modelDir   = args.Length > 1 ? args[1] : "";
            string outputPath = "/tmp/laplace-passthrough.gguf";
            bool   allF32     = args.Contains("--f32");
            double sparseTol  = 0.0;
            int tolIdx = Array.IndexOf(args, "--sparse-tol");
            if (tolIdx >= 0 && tolIdx + 1 < args.Length
                && double.TryParse(args[tolIdx + 1], out var t))
                sparseTol = t;
            // Output path = third positional arg if present and not a flag
            if (args.Length > 2 && !args[2].StartsWith("--"))
                outputPath = args[2];
            return await SynthesizePassthroughAsync(modelDir, outputPath, allF32, sparseTol);
        }

        return Fail(
            "usage: laplace synthesize <subcommand> [args]\n"
            + "  substrate <recipe.json> [output.gguf]   substrate-mediated synthesis\n"
            + "  passthrough <model-dir> [output.gguf] [--f32] [--sparse-tol <tol>]\n"
            + "                                          diagnostic real-weights transcode\n");
    }

    /// <summary>
    /// Stream A stub. Stream B per /home/ahart/.claude/plans/replicated-hatching-stream.md
    /// replaces this with a real implementation that:
    ///   (1) parses the recipe per ADR 0009 + GLOSSARY Recipe,
    ///   (2) loads the architecture template entity from substrate (per ADR 0011 +
    ///       ADR 0043) using the recipe's `IS_A Architecture_X` attestation,
    ///   (3) iterates the template's `required_tensors(SynthesisParams) → TensorSpecs`
    ///       (DESIGN.md:659),
    ///   (4) for each TensorSpec, queries the substrate aggregated attestations
    ///       under the recipe's source scope (the `laplace_glicko2_accumulate`
    ///       aggregator handles cross-source consensus per ADR 0056:206-215),
    ///   (5) calls `materialize_tensor(spec, substrate_view) → TensorValues`
    ///       (DESIGN.md:660) — the architecture template distributes consensus
    ///       values across the recipe's per-(layer, head, dim) layout (NOT a
    ///       pseudoinverse — per Memory `project_model_decomposer_attestation_insight.md`
    ///       and ADR 0056:183 "the inverse of this aggregation" = broadcast per
    ///       recipe layout, not SVD recovery),
    ///   (6) writes via the recipe-selected IFormatWriter (native safetensors-style
    ///       per R4 + GLOSSARY:431; GGUF / ONNX / TF / PyTorch via ADR 0059's
    ///       format-writer matrix).
    /// </summary>
    private static async Task<int> SynthesizeFromSubstrateAsync(string recipePath, string outputPath)
    {
        if (string.IsNullOrEmpty(recipePath) || !File.Exists(recipePath))
            return Fail(
                "usage: laplace synthesize substrate <recipe.json> [output.gguf]\n"
                + $"  (recipe not found: {recipePath})");

        Console.WriteLine($"synthesize substrate (Stream B-minimum) → {outputPath}");
        CodepointPerfcache.Load(ResolveBlob());

        string modelDir = Path.GetDirectoryName(recipePath) ?? ".";
        string tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        if (!File.Exists(tokenizerPath))
            return Fail($"tokenizer.json not found alongside recipe: {tokenizerPath}");

        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        var recipe = LlamaRecipeExtractor.Parse(recipePath);
        int vocab = recipe.VocabSize;
        int dModel = recipe.HiddenSize;

        // entity_id → vocab_index for substrate→token-index lookup
        var entityToToken = new Dictionary<Hash128, int>(tokens.Count);
        foreach (var t in tokens.OrderBy(t => t.TokenId))
            entityToToken.TryAdd(t.EntityId, t.TokenId);

        // Load arch template + recipe handle
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

        // ── Build substrate view ──────────────────────────────────────
        // Per-token consensus for unary kinds: aggregate effective-mu across
        // ALL unary tensor-calc kinds (EMBEDS / V / O / G / U / D / OUTPUT) by
        // token. Stream B-minimum: combine via sum; Stream B-complete picks
        // per-tensor consensus via the architecture template's per-slot policy.
        Console.WriteLine($"  querying substrate per-token consensus...");
        double[] perToken = new double[vocab];
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT subject_id, rating, rd FROM laplace.attestations
                WHERE source_id = $1
                  AND kind_id = ANY($2)
                  AND object_id IS NULL
                """;
            cmd.Parameters.AddWithValue(ModelDecomposer.Source.ToBytes());
            var unaryKinds = new[] {
                ModelDecomposer.EmbedsKind, ModelDecomposer.VProjectsKind,
                ModelDecomposer.OProjectsKind, ModelDecomposer.GatesKind,
                ModelDecomposer.UpProjectsKind, ModelDecomposer.DownProjectsKind,
                ModelDecomposer.OutputProjectsKind,
            }.Select(k => k.ToBytes()).ToArray();
            cmd.Parameters.AddWithValue(unaryKinds);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var subj = Hash128FromBytes((byte[])rdr[0]);
                long rating = rdr.GetInt64(1);
                long rdVal  = rdr.GetInt64(2);
                double effMu = Math.Max(0.0, (rating - 2.0 * rdVal) / 1e9);
                if (entityToToken.TryGetValue(subj, out int t) && t < vocab)
                    perToken[t] += effMu;
            }
        }

        // Q_PROJECTS sparse adjacency (subject, object, effective-mu)
        Console.WriteLine($"  querying substrate Q_PROJECTS adjacency...");
        var qkRows = new List<int>();
        var qkCols = new List<int>();
        var qkVals = new List<double>();
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT subject_id, object_id, rating, rd FROM laplace.attestations
                WHERE source_id = $1 AND kind_id = $2 AND object_id IS NOT NULL
                """;
            cmd.Parameters.AddWithValue(ModelDecomposer.Source.ToBytes());
            cmd.Parameters.AddWithValue(ModelDecomposer.QProjectsKind.ToBytes());
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var subj = Hash128FromBytes((byte[])rdr[0]);
                var obj  = Hash128FromBytes((byte[])rdr[1]);
                long rating = rdr.GetInt64(2);
                long rdVal  = rdr.GetInt64(3);
                double effMu = Math.Max(0.0, (rating - 2.0 * rdVal) / 1e9);
                if (effMu <= 0) continue;
                if (entityToToken.TryGetValue(subj, out int qi)
                  && entityToToken.TryGetValue(obj, out int kj)
                  && qi < vocab && kj < vocab)
                {
                    qkRows.Add(qi); qkCols.Add(kj); qkVals.Add(effMu);
                }
            }
        }

        // NORMALIZES single-aggregate (unary, subject = recipe entity, object = NULL)
        double normAgg = 0.0;
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT rating, rd FROM laplace.attestations
                WHERE source_id = $1 AND kind_id = $2 AND subject_id = $3
                """;
            cmd.Parameters.AddWithValue(ModelDecomposer.Source.ToBytes());
            cmd.Parameters.AddWithValue(ModelDecomposer.NormalizesKind.ToBytes());
            cmd.Parameters.AddWithValue(recipe.RecipeEntityId.ToBytes());
            await using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                long rating = rdr.GetInt64(0);
                long rdVal  = rdr.GetInt64(1);
                normAgg = Math.Max(0.0, (rating - 2.0 * rdVal) / 1e9);
            }
        }

        Console.WriteLine($"  consensus: per-token non-zero={perToken.Count(v => v > 0)} / {vocab}, "
                        + $"Q_PROJECTS pairs={qkRows.Count}, norm_aggregate={normAgg:F3}");

        var qkRowsArr = qkRows.ToArray();
        var qkColsArr = qkCols.ToArray();
        var qkValsArr = qkVals.ToArray();

        // ── Spectral basis: eigenmaps → Procrustes → gram matrices ────────
        // basisTargetDim is capped well below dModel: computing dModel=2048
        // eigenpairs of a 32K-node sparse graph via Lanczos requires O(4K×32K)
        // workspace and rarely converges in CI time. 64 eigenpairs converge in
        // seconds. materialize_token_axis cycles the basis mod basisTargetDim so
        // every hidden dimension is still substrate-derived, just with period-64
        // spectral texture.
        const int EigenMaxDim = 64;
        int basisTargetDim = Math.Min(dModel, EigenMaxDim);

        double[]? tokenBasis   = null;
        double[]? unaryGram    = null;
        double[]? binaryGram   = null;
        if (qkRowsArr.Length > 0)
        {
            Console.WriteLine($"  computing spectral token basis (eigenmaps target_dim={basisTargetDim}, nnz={qkRowsArr.Length})...");
            var sw0 = Stopwatch.StartNew();
            double[] basis = new double[(long)vocab * basisTargetDim];
            int eigenRc;
            unsafe
            {
                fixed (int*    qrPtr    = qkRowsArr)
                fixed (int*    qcPtr    = qkColsArr)
                fixed (double* qvPtr    = qkValsArr)
                fixed (double* basisPtr = basis)
                    eigenRc = DynamicsInterop.LaplacianEigenmapsFromSparseGraph(
                        qrPtr, qcPtr, qvPtr,
                        (nuint)qkRowsArr.Length, (nuint)vocab, (nuint)basisTargetDim,
                        basisPtr);
            }
            if (eigenRc != 0)
            {
                Console.Error.WriteLine($"  WARNING: eigenmaps returned {eigenRc}; basis unavailable");
            }
            else
            {
                // GramSchmidt removed: Spectra's SymEigsShiftSolver produces orthonormal
                // eigenvectors by construction (symmetric matrix). A GS call with
                // n_vecs=basisTargetDim, dim=vocab on the [vocab×basisTargetDim] layout
                // would scramble the data due to layout mismatch.

                // Procrustes: align basis[vocab × basisTargetDim] to canonical S³ positions
                var sortedTokens = tokens.OrderBy(t => t.TokenId).ToArray();
                double[] targetPts = new double[(long)vocab * 4];
                for (int t = 0; t < vocab && t < sortedTokens.Length; t++)
                {
                    var tok = sortedTokens[t];
                    if (!tok.HasContentCoord) continue;
                    targetPts[t * 4 + 0] = tok.ContentX;
                    targetPts[t * 4 + 1] = tok.ContentY;
                    targetPts[t * 4 + 2] = tok.ContentZ;
                    targetPts[t * 4 + 3] = tok.ContentM;
                }
                IntPtr procT;
                unsafe
                {
                    fixed (double* bPtr = basis)
                    fixed (double* tPtr = targetPts)
                        procT = DynamicsInterop.ProcrustesFit(bPtr, (nuint)vocab, (nuint)basisTargetDim, tPtr);
                }
                if (procT != IntPtr.Zero)
                {
                    Console.WriteLine($"  Procrustes residual: {DynamicsInterop.ProcrustesResidual(procT):F4}");
                    DynamicsInterop.ProcrustesFree(procT);
                }

                // Gram matrices: [basisTargetDim × basisTargetDim] — interior tensors
                // cycle mod basisTargetDim so any shape is handled.
                double[] uGram = new double[(long)basisTargetDim * basisTargetDim];
                double[] bGram = new double[(long)basisTargetDim * basisTargetDim];
                int gramRc;
                unsafe
                {
                    fixed (double* bPtr  = basis)
                    fixed (double* ptPtr = perToken)
                    fixed (int*    qrPtr = qkRowsArr)
                    fixed (int*    qcPtr = qkColsArr)
                    fixed (double* qvPtr = qkValsArr)
                    fixed (double* ugPtr = uGram)
                    fixed (double* bgPtr = bGram)
                        gramRc = SynthInterop.ComputeSubstrateGram(
                            bPtr, ptPtr, (nuint)vocab, (nuint)basisTargetDim,
                            qrPtr, qcPtr, qvPtr, (nuint)qkRowsArr.Length,
                            ugPtr, bgPtr);
                }
                if (gramRc == 0) { unaryGram = uGram; binaryGram = bGram; }
                else Console.Error.WriteLine($"  WARNING: compute_substrate_gram returned {gramRc}; interior tensors use fallback");

                tokenBasis = basis;
                Console.WriteLine($"  spectral token basis ready in {sw0.Elapsed.TotalSeconds:F1}s (basis_dim={basisTargetDim})");
            }
        }

        // ── Write GGUF via materialize_tensor per slot ─────────────────
        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero) return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens, modelDir);

        var sw = Stopwatch.StartNew();
        int tensorsDone = 0;
        int rcArr = 0;

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
            byte[] tensorBytes = new byte[nElem * (dtype == 0 ? 4L : 2L)];

            rcArr = MaterializeOneTensor(tmplHandle, specs, i, perToken, vocab,
                qkRowsArr, qkColsArr, qkValsArr, normAgg,
                tokenBasis, unaryGram, binaryGram, basisTargetDim,
                tensorBytes);
            if (rcArr != 0)
                Console.Error.WriteLine($"  materialize_tensor({name}) returned {rcArr}; tensor zero-filled");

            nuint[] ggufDims = cols > 1 ? [(nuint)cols, (nuint)rows] : [(nuint)rows];
            unsafe
            {
                fixed (nuint* dimsPtr = ggufDims)
                fixed (byte*  dataPtr = tensorBytes)
                    SynthInterop.GgufWriterAddTensor(gguf, HfToGgmlName(name), dtype, dimsPtr, (nuint)ggufDims.Length, dataPtr);
            }

            tensorsDone++;
            if (tensorsDone == 1 || tensorsDone % 20 == 0)
                Console.WriteLine($"  [{tensorsDone}/{tensorCount}] {name} rows={rows} cols={cols} {sw.Elapsed.TotalSeconds:F1}s");
        }

        int rc = SynthInterop.GgufWriterFinalize(gguf);
        SynthInterop.GgufWriterFree(gguf);
        SynthInterop.ArchTemplateFree(tmplHandle);
        SynthInterop.RecipeFree(recipeHandle);
        if (rc != 0) return Fail($"gguf_writer_finalize failed (rc={rc})");

        long fileSize = new FileInfo(outputPath).Length;
        Console.WriteLine($"synthesis complete: {outputPath} ({fileSize / 1048576.0:F0} MB) in {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    /// <summary>
    /// Synchronous helper for SynthesizeFromSubstrateAsync's per-tensor call.
    /// Extracted from the async method to keep `&view` / `&spec` taken-of-locals
    /// out of an async state machine (CS9123 — async state may move locals).
    /// </summary>
    private static unsafe int MaterializeOneTensor(
        IntPtr tmplHandle, TensorSpec[] specs, int specIndex,
        double[] perToken, int vocab,
        int[] qkRowsArr, int[] qkColsArr, double[] qkValsArr,
        double normAgg,
        double[]? tokenBasis, double[]? unaryGram, double[]? binaryGram, int basisDim,
        byte[] outBytes)
    {
        // fixed() on a null array is undefined — use 1-element dummies for pinning,
        // then pass null pointers to C when the optional arrays weren't computed.
        var basisPin = tokenBasis ?? new double[1];
        var ugramPin = unaryGram  ?? new double[1];
        var bgramPin = binaryGram ?? new double[1];

        fixed (double* ptcPtr  = perToken)
        fixed (int*    qrPtr   = qkRowsArr)
        fixed (int*    qcPtr   = qkColsArr)
        fixed (double* qvPtr   = qkValsArr)
        fixed (double* bPtr    = basisPin)
        fixed (double* ugPtr   = ugramPin)
        fixed (double* bgPtr   = bgramPin)
        fixed (byte*   outPtr  = outBytes)
        fixed (TensorSpec* specPtr = specs)
        {
            var view = new SubstrateView
            {
                PerTokenConsensus = ptcPtr,
                Vocab             = (nuint)vocab,
                PerPairRows       = qrPtr,
                PerPairCols       = qcPtr,
                PerPairVals       = qvPtr,
                PerPairNnz        = (nuint)qkRowsArr.Length,
                NormAggregate     = normAgg,
                TokenBasis        = tokenBasis != null ? bPtr  : null,
                BasisDim          = tokenBasis != null ? (nuint)basisDim : 0,
                UnaryGram         = unaryGram  != null ? ugPtr : null,
                BinaryGram        = binaryGram != null ? bgPtr : null,
            };
            return SynthInterop.ArchTemplateMaterializeTensor(
                tmplHandle, &specPtr[specIndex], &view, outPtr);
        }
    }


    // === synthesize passthrough: real weights → our GGUF writer (no substrate) ===
    // Diagnostic. Isolates format/metadata/arch_template correctness from substrate
    // data quality. Each tensor is transcoded to the arch_template's declared dtype
    // (norms bf16→f32, weights bf16 passthrough) to match llama.cpp's GGUF conventions.
    // Universal across any safetensors-format model dir; not model-family-specific.
    private static async Task<int> SynthesizePassthroughAsync(
        string modelDir, string outputPath, bool allF32 = false, double sparseTol = 0.0)
    {
        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir))
            return Fail(
                "usage: laplace synthesize passthrough <model-dir> [output.gguf] [--f32] [--sparse-tol <tol>]\n"
                + $"  (model dir not found: {modelDir})");

        Console.WriteLine(
            $"synthesize passthrough {(sparseTol > 0 ? $"SPARSE(tol={sparseTol:0.000})" : "PASSTHROUGH")} "
            + $"(real weights → GGUF{(allF32 || sparseTol > 0 ? ", F32" : "")}) "
            + $"src={modelDir} → {outputPath}");
        /* LlamaTokenizerParser now requires the perfcache (TextDecomposer +
         * HashComposer in Parse). */
        CodepointPerfcache.Load(ResolveBlob());
        string configPath      = Path.Combine(modelDir, "config.json");
        string tokenizerPath   = Path.Combine(modelDir, "tokenizer.json");
        string safetensorsPath = Path.Combine(modelDir, "model.safetensors");
        if (!File.Exists(configPath) || !File.Exists(tokenizerPath) || !File.Exists(safetensorsPath))
            return Fail($"model files not found under {modelDir}");

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
        WriteGgufMetadata(gguf, recipe, tokens, modelDir);

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

    private static unsafe Hash128 Hash128FromBytes(byte[] b)
    {
        if (b.Length < 16) return Hash128.Zero;
        fixed (byte* p = b) return *(Hash128*)p;
    }

    // Write all GGUF metadata: architecture params + tokenizer vocab.
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

        // Real chat template from the source model's tokenizer_config (so the server's
        // chat endpoint uses the model's actual template, not a generic ChatML fallback).
        string cfgPath = Path.Combine(modelDir, "tokenizer_config.json");
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
        // Content-bearing decomposers (WordNet/OMW/UD/Tatoeba/ConceptNet/Atomic2020/
        // Wiktionary) route lemmas/glosses/examples/sentences through ContentEmitter →
        // TextDecomposer + HashComposer, whose atom resolver reads the T0 perf-cache. It
        // MUST be loaded before the run or every decomposition silently yields no content.
        CodepointPerfcache.Load(ResolveBlob());

        // Omni-glottal language resolution index (the "language perf-cache"): every
        // 639-1-code source (UD/OMW/Wiktionary/Tatoeba/ConceptNet) resolves its raw
        // codes/names through this to the ONE canonical 639-3 language entity, so the
        // substrate unifies languages at ingest (no runtime joins). Built from the same
        // attested ISO 639 reference the ISODecomposer seeds. Idempotent + fail-loud.
        LanguageReference.EnsureLoaded();

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

        // Show a concrete row: U+0041 'A'. Scoped block so cmd + rdr dispose
        // before the next conn-using Scalar call (a leaked reader on the shared
        // conn surfaces as NpgsqlOperationInProgressException — sees as commit
        // be99495's CI failure).
        {
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

        long modelAtts = await Scalar(
            "SELECT count(*) FROM laplace.attestations WHERE source_id = @p",
            ModelDecomposer.Source.ToBytes());
        if (modelAtts == 0)
        {
            Console.WriteLine("  model attestations    : (none — ingest model)");
            return;
        }

        Console.WriteLine($"  model attestations    : {modelAtts,9:N0}");
        async Task<long> KindCount(Hash128 kind)
        {
            await using var c = conn.CreateCommand();
            c.CommandText =
                "SELECT count(*) FROM laplace.attestations WHERE source_id = @s AND kind_id = @k";
            c.Parameters.AddWithValue("s", ModelDecomposer.Source.ToBytes());
            c.Parameters.AddWithValue("k", kind.ToBytes());
            return (long)(await c.ExecuteScalarAsync())!;
        }

        // Corrected ingest emits content records, not the per-circuit bilinear kinds:
        // COMPLETES_TO = the FFN/OV [context n-gram] ⇒ {completion} key→value memories.
        // The old Q_PROJECTS/O_PROJECTS/EMBEDS/… per-circuit kinds are no longer emitted
        // (the token×token-bilinear "disease"); kept here at the tail for visibility — they
        // read 0 on a corrected ingest, which is the point.
        (string label, Hash128 kind)[] modelKinds =
        [
            ("COMPLETES_TO",    ModelDecomposer.CompletesToKind),
            ("EMBEDS",          ModelDecomposer.EmbedsKind),
            ("Q_PROJECTS",      ModelDecomposer.QProjectsKind),
            ("K_PROJECTS",      ModelDecomposer.KProjectsKind),
            ("V_PROJECTS",      ModelDecomposer.VProjectsKind),
            ("O_PROJECTS",      ModelDecomposer.OProjectsKind),
            ("GATES",           ModelDecomposer.GatesKind),
            ("UP_PROJECTS",     ModelDecomposer.UpProjectsKind),
            ("DOWN_PROJECTS",   ModelDecomposer.DownProjectsKind),
            ("NORMALIZES",      ModelDecomposer.NormalizesKind),
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
