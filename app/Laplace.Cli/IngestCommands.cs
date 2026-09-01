using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.CILI;
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

internal static class IngestCommands
{
    internal sealed record IngestCliArgs(
        string Source,
        string Path,
        LanguageFilter? LangOverride,
        bool? EmitCrossLanguageLinks,
        bool SkipEvidence,
        bool RegisterOnly,
        bool Force = false,
        bool NoAnalyze = false,
        bool Recursive = false,
        int AnalyzeDepth = 0,
        long AnalyzeNodes = 0);


    private static IngestCliArgs ParseIngestCliArgs(string[] args)
    {
        var rest = new List<string>(args);
        LanguageFilter? langs = null;
        bool? emitCross = null;
        bool skipEvidence = false;
        bool registerOnly = false;
        bool force = false;
        bool noAnalyze = false;
        bool recursive = false;
        int analyzeDepth = 0;
        long analyzeNodes = 0;
        for (int i = 0; i < rest.Count;)
        {
            if (rest[i] == "--langs" && i + 1 < rest.Count)
            {
                langs = LanguageFilter.FromSpec(rest[i + 1]);
                rest.RemoveAt(i + 1);
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--emit-cross-lang")
            {
                emitCross = true;
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--no-evidence")
            {
                skipEvidence = true;
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--register-only")
            {
                registerOnly = true;
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--force")
            {
                force = true;
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--no-analyze")
            {
                noAnalyze = true;
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--recursive")
            {
                recursive = true;
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--depth" && i + 1 < rest.Count)
            {
                int.TryParse(rest[i + 1], out analyzeDepth);
                rest.RemoveAt(i + 1);
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--nodes" && i + 1 < rest.Count)
            {
                long.TryParse(rest[i + 1], out analyzeNodes);
                rest.RemoveAt(i + 1);
                rest.RemoveAt(i);
            }
            else i++;
        }
        return new(
            rest.Count > 0 ? rest[0] : "",
            rest.Count > 1 ? rest[1] : "",
            langs,
            emitCross,
            skipEvidence,
            registerOnly,
            force,
            noAnalyze,
            recursive,
            analyzeDepth,
            analyzeNodes);
    }

    private static bool ResolvePersistEvidence(IngestCliArgs? cli)
        => cli?.SkipEvidence != true;

    public static async Task<int> IngestAsync(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("chain", StringComparison.OrdinalIgnoreCase))
            return await IngestChainAsync(args[1..]);

        var cli = ParseIngestCliArgs(args);
        if (string.IsNullOrEmpty(cli.Source))
            return Fail("usage: laplace ingest <source> [path] [--langs en,...] [--emit-cross-lang] [--no-evidence]\n"
                        + "       laplace ingest chain \"<source [path] [flags]>\" ...\n"
                        // ASK THE REGISTRY. This line used to hand-list the sources, and it
                        // lied in both directions: it advertised `image` and `audio`, which
                        // no dispatch route can reach, and omitted every chess lane plus
                        // omw-probe and recipe. The authority is two lines below in the same
                        // method — TryDispatch's own table, already used for the unknown-source
                        // error — so help and error can never again disagree about what this
                        // binary supports.
                        + "  sources: " + string.Join(" | ", IngestDispatchTable.RegisteredKeys.OrderBy(k => k)) + "\n"
                        + "  --langs: language scope for this run\n"
                        + "  --no-evidence: fold consensus only; skip laplace.attestations\n"
                        + "  chain: run several ingests sequentially in ONE process (one startup, one\n"
                        + "         perfcache load); stops at the first failing spec");




        CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();

        string sourceKey = cli.Source.ToLowerInvariant();

        if (IngestDispatchTable.TryDispatch(sourceKey, cli, out var task))
            return await task;

        return Fail($"unknown ingest source '{cli.Source}' (supported: {string.Join(", ", IngestDispatchTable.RegisteredKeys.OrderBy(k => k))})");
    }

    /// <summary>
    /// Sequential multi-source ingest in one process: each spec is a complete
    /// `ingest` argument vector ("wordnet", "document D:\\data\\text",
    /// "wiktionary --langs en"). One process start, one perfcache map, one
    /// native runtime init for the whole ladder instead of one per source.
    /// Specs split on whitespace — a path containing spaces needs its own
    /// single-source invocation. First nonzero exit stops the chain.
    /// </summary>
    private static async Task<int> IngestChainAsync(string[] specs)
    {
        if (specs.Length == 0)
            return Fail("usage: laplace ingest chain \"<source [path] [flags]>\" ...\n"
                        + "  example: laplace ingest chain unicode iso639 cili wordnet \"document D:\\Data\\Ingest\\test-data\\text\"");

        CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();

        for (int i = 0; i < specs.Length; i++)
        {
            var tokens = specs[i].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cli = ParseIngestCliArgs(tokens);
            if (string.IsNullOrEmpty(cli.Source))
                return Fail($"ingest chain: spec {i + 1} is empty");
            Console.WriteLine($"==== chain [{i + 1}/{specs.Length}]: ingest {specs[i]} ====");
            if (!IngestDispatchTable.TryDispatch(cli.Source.ToLowerInvariant(), cli, out var task))
                return Fail($"ingest chain: unknown source '{cli.Source}' in spec {i + 1} "
                            + $"(supported: {string.Join(", ", IngestDispatchTable.RegisteredKeys.OrderBy(k => k))})");
            int rc = await task;
            if (rc != 0)
            {
                Console.Error.WriteLine($"==== chain [{i + 1}/{specs.Length}] '{specs[i]}' exited {rc} — chain stopped ====");
                return rc;
            }
        }
        Console.WriteLine($"==== chain complete: {specs.Length} source(s) ====");
        return 0;
    }

    internal static async Task<int> OmwProbeAsync(IngestCliArgs cli)
    {
        string wns = IngestDataPaths.Resolve("omw", cli.Path);
        if (!Directory.Exists(wns))
            return Fail($"OMW path not found: {wns}");

        long start = 0;
        long max = 0;
        // IngestAsync already loaded the blob when dispatching here; don't pay it twice.
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();

        Console.Error.WriteLine($"omw-probe: scanning {wns} start_row={start} max_rows={(max > 0 ? max.ToString() : "all")}");
        var fail = await OmwComposeProbe.ScanFirstFailureAsync(wns, cli.LangOverride, start, max);
        if (fail is null)
        {
            Console.Error.WriteLine("omw-probe: all rows passed probe+materialize_phys");
            return 0;
        }

        Console.Error.WriteLine(
            $"omw-probe: FAIL row={fail.RowIndex} file={fail.FilePath}\n"
            + $"  error={fail.Error}\n"
            + $"  bytes={fail.LineBytes} preview={fail.LinePreview}");
        return 1;
    }

    internal static async Task<int> IngestSafetensorSnapshotAsync(string modelDir, IngestCliArgs cli)
    {
        if (string.IsNullOrEmpty(modelDir))
            return Fail("usage: laplace ingest safetensors <snapshot-dir>\n"
                        + "  HF snapshot: config.json + tokenizer.json + *.safetensors\n"
                        + "  (safetensors are not self-contained like GGUF — the directory is the witness unit)\n"
                        + "  Also accepts an HF hub cache root or models--* family dir (resolves snapshots/<rev>).");

        var resolved = SafetensorSnapshotWitness.ResolveCompleteDir(modelDir) ?? modelDir;
        var snapshotCheck = SafetensorSnapshotWitness.Validate(resolved);
        if (!snapshotCheck.Ok)
            return Fail($"invalid safetensor snapshot: {snapshotCheck.Error}\n"
                        + $"path: {modelDir}"
                        + (resolved != modelDir ? $"\nresolved: {resolved}" : ""));
        modelDir = resolved;

        // IngestAsync already loaded the blob when dispatching here; don't pay it twice.
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();







        // Explicit unbounded timeout: the Ingest policy passes the base string through
        // untouched, so it inherits Command Timeout=0 only when LAPLACE_DB carries it.
        await using var ds = LaplaceDataSource.Create(
            SubstrateAccess.Ingest,
            b => b.ConnectionStringBuilder.CommandTimeout = 0,
            ConnString);

        var dec = CliRuntime.Decomposers.ResolveModel(modelDir, persistEvidence: ResolvePersistEvidence(cli));




        if (cli.RegisterOnly)
        {
            await RegisterDynamicCanonicalsAsync(ds, dec);
            return 0;
        }

        var (modelSource, modelName) = ModelDecomposer.SourceForModel(modelDir);
        // Analyzer modes (LAPLACE_MODEL_PLANES != "structure") are calculated
        // re-passes over an already-recorded model; the recorder's re-deposition
        // guard must not block them.
        if (Laplace.Decomposers.Model.ModelTokenEdgeETL.ResolvePlanesMode() == "structure")
        {
            bool alreadyIngested = await NpgsqlIngestOps.EvidenceExistsForTypeAndSourceAsync(
                ds,
                modelSource.ToBytes(),
                Laplace.Ingestion.LayerCompletion.RelationTypeId(dec.LayerOrder).ToBytes());
            if (alreadyIngested)
            {
                Console.WriteLine($"Safetensor snapshot already deposited — source {modelName}: {modelSource}");
                Console.WriteLine($"(re-deposition refused to prevent consensus contamination; "
                                  + $"reset with db-fresh to test from scratch)");
                return 0;
            }
        }

        var loggerFactory = CliRuntime.LoggerFactory;
        var inner = new NpgsqlSubstrateWriter(ds,
            logger: loggerFactory.CreateLogger<NpgsqlSubstrateWriter>());





        bool persistEvidenceResolved = ResolvePersistEvidence(cli);
        var accumulator = new ConsensusAccumulatingWriter(inner, ds,
            persistEvidence: persistEvidenceResolved,
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        ISubstrateWriter writer = accumulator;
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader, loggerFactory,
            new NpgsqlIngestObservability(ds, persistEvidenceResolved));
        Console.WriteLine("mode: safetensor snapshot apply (anti-join merge; consensus accumulates at ingest)");

        Console.WriteLine($"deposit safetensor snapshot {modelDir} via IngestRunner → {ConnString} ...");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await runner.RunAsync(
                dec,
                BuildIngestOptions(sw, dec.SourceName, skipLayerCheck: true, ecosystemPath: null, cli,
                    // Analyzer modes are calculated re-passes over an already-recorded
                    // model (doc 08's chess-analyze pattern): the recorder's
                    // completion marker must neither block them nor be re-written.
                    skipSourceCompletion:
                        Laplace.Decomposers.Model.ModelTokenEdgeETL.ResolvePlanesMode() != "structure")
                with
                {
                    DecomposerOptions =                     DecomposerOptions.ForWitness(
                    dec.SourceName,
                    IngestSizing.Resolve(
                        IngestTopology.Current.PerformanceCoreCount,
                        IngestTopology.Current.FileWorkers,
                        IngestTopology.Current.ApplyPartitions).RecordBatchSize,
                    cli.LangOverride,
                    cli.EmitCrossLanguageLinks)
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





            await RegisterDynamicCanonicalsAsync(ds, dec);

            Console.WriteLine(
                $"consensus: {accumulator.CellsFolded:N0} cells materialized during ingest from "
                + $"{accumulator.ObservationsAccumulated:N0} observations "
                + $"(queued folds drained before success; evidence = provenance-only)");
        }
        finally
        {
            sw.Stop();
        }
        try { await PrintIngestValidationAsync(ds, dec, exactSourceValidation: false); }
        catch (Exception ex)
        { Console.Error.WriteLine($"warn: safetensor deposition validation failed: {ex.Message}"); }
        return 0;
    }

    internal static async Task<int> IngestDocumentAsync(IngestCliArgs cli)
    {
        if (string.IsNullOrEmpty(cli.Path))
            return Fail("usage: laplace ingest document <file-or-directory>\n"
                        + "  Deposits whole documents (entities + physicalities + PRECEDES bigrams).\n"
                        + "  Bit-perfect proof: laplace db-roundtrip <file>  (reconstruct + compare).");
        if (!File.Exists(cli.Path) && !Directory.Exists(cli.Path))
            return Fail($"ingest document: path not found: {cli.Path}");

        // Pillar 0: the document lane's completion is PER FILE (PerFileCompletion), so the
        // runner's source-level guard is skipped by capability, not by flag — and the
        // terminal source-level marker now mints, satisfying layer ordering.
        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve("document"),
            Path.GetFullPath(cli.Path),
            skipLayerCheck: true,
            cli,
            skipSourceCompletion: false);
    }

    internal static async Task<int> IngestRecipeAsync(IngestCliArgs cli)
    {
        if (string.IsNullOrEmpty(cli.Path))
            return Fail("usage: laplace ingest recipe <recipe.json>\n"
                        + "  Deposits a Mold-A-Model recipe (the simulated UI POST) as a content-addressed\n"
                        + "  Model_Recipe entity, fetchable by export via structural.model_recipes() / --recipe-from.");
        if (!File.Exists(cli.Path))
            return Fail($"ingest recipe: file not found: {cli.Path}");
        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.ResolveRecipe(Path.GetFullPath(cli.Path)),
            Path.GetFullPath(cli.Path),
            skipLayerCheck: true,
            cli,
            skipSourceCompletion: true);
    }

    internal static async Task<int> IngestUnicodeViaRunnerAsync(IngestCliArgs cli)
        => await IngestViaRunnerAsync(CliRuntime.Decomposers.Resolve("unicode"), IngestDataPaths.Resolve("unicode", cli.Path), skipLayerCheck: true, cli);

    internal static async Task<int> IngestISO639Async(IngestCliArgs cli)
        => await IngestViaRunnerAsync(CliRuntime.Decomposers.Resolve("iso639"), IngestDataPaths.Resolve("iso639", cli.Path), skipLayerCheck: false, cli);

    private static string ResolveIngestPath(string? cliPath, string defaultPath)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(cliPath) ? defaultPath : cliPath);

    private static string? ResolveRequiredIngestPath(string? cliPath)
        => string.IsNullOrWhiteSpace(cliPath) ? null : Path.GetFullPath(cliPath);

    // code/repo/tabular/parquet are multi-invocation, path-parameterized sources —
    // the CLI's own <path> argument means "run this again against something new"
    // is the intended usage (validate one file, then ingest the full corpus; feed
    // one repo today and another tomorrow). The default source-completion marker
    // is for genuinely one-shot global corpora (wordnet, conceptnet, ...) where a
    // second run over the SAME dataset would double-witness identical bootstrap
    // testimony; it has no such meaning here, since content-addressing already
    // makes re-running against different (or even the same) content safe. Without
    // skipSourceCompletion: true, the FIRST successful run of any of these silently
    // no-ops every later call regardless of path — measured 2026-07-23: a 19-repo
    // batch ingest ran as 19 back-to-back 0-row short-circuits after the first.

    internal static async Task<int> IngestCodeAsync(IngestCliArgs cli)
    {
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace ingest code <file-or-directory>");
        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve("code"), path, skipLayerCheck: true, cli, skipSourceCompletion: true);
    }

    internal static async Task<int> IngestRepoAsync(IngestCliArgs cli)
    {
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace ingest repo <repository-root>");
        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve("repo"), path, skipLayerCheck: true, cli, skipSourceCompletion: true);
    }

    internal static async Task<int> IngestAgentsAsync(IngestCliArgs cli)
    {
        // Path optional: an explicit file/dir is the witness boundary; empty discovers
        // the current user's provider roots (~/.claude, ~/.codex, ~/.gemini, …).
        var path = cli.Path is { Length: > 0 } p ? Path.GetFullPath(p) : "";
        if (path.Length > 0 && !File.Exists(path) && !Directory.Exists(path))
            return Fail($"agents: path not found: {path}");
        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve("agents"), path, skipLayerCheck: true, cli, skipSourceCompletion: true);
    }

    internal static async Task<int> IngestTabularAsync(IngestCliArgs cli)
    {
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace ingest tabular <file-or-directory>");
        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve("tabular"), path, skipLayerCheck: true, cli, skipSourceCompletion: true);
    }

    internal static async Task<int> IngestParquetAsync(IngestCliArgs cli)
    {
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace ingest parquet <file-or-directory>");
        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve("parquet"), path, skipLayerCheck: true, cli, skipSourceCompletion: true);
    }

    /// <summary>
    /// Line-delimited formats where the mean line length IS the record size. Deliberately a
    /// whitelist: measuring an XML or Parquet file this way yields a number with no relation
    /// to a record, and sizing a batch from it would be worse than the declared constant.
    /// </summary>
    private static readonly string[] LineDelimitedExtensions =
        [".jsonl", ".ndjson", ".csv", ".tsv", ".tab", ".conllu", ".txt"];

    private static IngestSourceProfile ApplyMeasuredRecordSize(
        IngestSourceProfile profile, string? path, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return profile;
        string ext = Path.GetExtension(path);
        if (!LineDelimitedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return profile;

        int measured = IngestSizing.MeasureBytesPerRecord(
            path, fallback: profile.EstBytesPerRecord);
        if (measured == profile.EstBytesPerRecord) return profile;

        Console.Error.WriteLine(
            $"ingest_record_size: source={sourceName} file={Path.GetFileName(path)} "
            + $"declared={profile.EstBytesPerRecord} measured={measured} "
            + $"ratio={(double)profile.EstBytesPerRecord / measured:F2}x — sizing from the file");
        return profile with { EstBytesPerRecord = measured };
    }

    private static IngestRunOptions BuildIngestOptions(
        Stopwatch sw, string sourceName, bool skipLayerCheck, string? ecosystemPath,
        IngestCliArgs? cli = null, bool skipSourceCompletion = false,
        IngestSourceProfile? sizingProfile = null)
    {
        IngestTopology.EnsureReady();
        long lastMs = -10_000;
        int progressIntervalMs = Laplace.Decomposers.Abstractions.IngestConsoleMode.ProgressMinIntervalMs;
        var progress = new Progress<Laplace.Ingestion.IngestProgress>(p =>
        {
            long now = sw.ElapsedMilliseconds;
            if (now - lastMs < progressIntervalMs) return;
            lastMs = now;
            double secs = Math.Max(0.001, p.Elapsed.TotalSeconds);
            long rowsNew = p.EntitiesInserted + p.PhysicalitiesInserted + p.AttestationsInserted;
            long inputProgress = Math.Max(p.InputUnitsDone, p.InputUnitsComposed);
            string filePart = p.FilesTotal > 0 ? $"files={p.FilesDone}/{p.FilesTotal} file_pct={p.FilePercent:F1}" : "";
            string inputPart = p.InputUnitsTotal > 0
                ? $"input={inputProgress}/{p.InputUnitsTotal} input_pct={p.InputPercent:F1}"
                  + (p.InputUnitsComposed > p.InputUnitsDone
                      ? $" composed={p.InputUnitsComposed:N0} committed={p.InputUnitsDone:N0}"
                      : "")
                : $"intents={p.UnitsApplied}/{p.UnitsProduced} intent_pct={p.InputPercent:F1}";
            string cur = string.IsNullOrEmpty(p.CurrentFile) ? "" : $" current={p.CurrentFile}";
            Console.Error.WriteLine(
                $"INGEST_PROGRESS source={p.SourceName} layer={p.LayerOrder} unit_type={p.UnitType} "
                + $"{inputPart} {filePart}{cur} "
                + $"rows_new={rowsNew:N0} rate_input_s={inputProgress / secs:N0} rate_rows_new_s={rowsNew / secs:N0} "
                + $"round_trips={p.RoundTrips:N0} elapsed_s={p.Elapsed.TotalSeconds:F0}"
                + (p.UnitsFailed > 0 ? $" failed={p.UnitsFailed:N0} status=failed" : " status=running"));
        });
        // LAPLACE_INGEST_MAX_UNITS caps INPUT VOLUME (smoke/bench scoping — an
        // operator decision, like --langs). It is not a machine-sizing knob:
        // batch/commit sizing stays owned by IngestSizing/MemoryTopology and
        // deliberately has no env override.
        long maxUnits =
            long.TryParse(Environment.GetEnvironmentVariable("LAPLACE_INGEST_MAX_UNITS"),
                out var mu) && mu > 0 ? mu : 0;
        var profile = sizingProfile ?? IngestSourceProfile.Default;
        // MEASURE the record size from the file in hand instead of trusting the per-source
        // constant. EstBytesPerRecord is the denominator of the per-worker memory
        // calculation, so a wrong value silently shrinks or expands every batch.
        //
        // The constants are demonstrably wrong, and worse, one constant cannot be right for
        // one source: MEASURED 2026-08-01, WiktionaryDecomposer ingests BOTH
        // raw-wiktextract-data.jsonl at 6,158 bytes/record AND
        // kaikki.org-dictionary-English.jsonl at 26,719 -- a 4.3x spread behind a single
        // IngestSourceProfile.Wiktionary. Whatever number sits in that constant is badly
        // wrong for one of the two files. Only the file itself knows.
        //
        // Guarded to line-delimited formats: for XML or Parquet the mean LINE length is not
        // the record size, and sizing from it would be worse than the constant. Anything
        // else keeps the declared profile untouched.
        profile = ApplyMeasuredRecordSize(profile, ecosystemPath, sourceName);
        var sized = IngestSizing.ResolveForSource(profile);
        sized.Log(sourceName);
        int batch = sized.RecordBatchSize;
        int commitRows = sized.CommitRows;
        var decoOpts = DecomposerOptions.ForWitness(
            sourceName, batch, cli?.LangOverride, cli?.EmitCrossLanguageLinks);
        if (cli?.Force ?? false)
            decoOpts = decoOpts with { ReObservePresent = true };
        if (maxUnits > 0)
            decoOpts = decoOpts with { MaxInputUnits = maxUnits };
        return IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = skipLayerCheck,
            // Suppressing completion and bypassing its pre-run guard are different
            // operations. Incremental lanes deliberately own their own completion
            // protocol; --force merely re-runs an ordinary source and must still
            // publish its terminal HasLayerCompleted marker.
            SkipSourceCompletion = skipSourceCompletion,
            BypassSourceCompletionGuard = cli?.Force ?? false,
            EcosystemPath = ecosystemPath,
            BatchSize = batch,
            DecomposerOptions = decoOpts,
            CommitRows = commitRows,
            Progress = progress,
            RetryPolicy = TransientErrorRetryPolicy.Default,
            AbortOnTransientExhaustion = true,
        };
    }

    internal static async Task<int> IngestViaRunnerAsync(
        IDecomposer dec, string ecosystemPath, bool skipLayerCheck, IngestCliArgs? cli = null,
        bool skipSourceCompletion = false)
    {
        // IngestAsync already loaded the blob when dispatching here; don't pay it twice.
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();

        LanguageReference.EnsureLoaded();
        var topo = IngestTopology.EnsureReady();

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        var loggerFactory = CliRuntime.LoggerFactory;
        bool force = cli?.Force ?? false;
        var innerWriter = new NpgsqlSubstrateWriter(ds,
            logger: loggerFactory.CreateLogger<NpgsqlSubstrateWriter>());
        bool persistEvidence = ResolvePersistEvidence(cli);
        await using var accumulator = new ConsensusAccumulatingWriter(innerWriter, ds,
            persistEvidence: persistEvidence,
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        var writer = (ISubstrateWriter)accumulator;
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader, loggerFactory,
            new NpgsqlIngestObservability(ds, persistEvidence));

        Console.WriteLine($"ingest {dec.SourceName} via IngestRunner → {ConnString} ..."
            + (persistEvidence ? "" : " (consensus-only, no attestation writes)"));
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            dec,
            BuildIngestOptions(sw, dec.SourceName, skipLayerCheck, ecosystemPath, cli,
                skipSourceCompletion,
                sizingProfile: dec.SizingProfile),
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


        await RegisterDynamicCanonicalsAsync(ds, dec);
        Console.WriteLine($"consensus: {accumulator.CellsFolded:N0} cells materialized during ingest "
                        + $"from {accumulator.ObservationsAccumulated:N0} observations "
                        + "(queued folds drained before success)");

        // Zero-novel re-ingest: ANALYZE + validation counts are multi-second (or hang) on a
        // populated box and are not part of the fold. Skip them so process exit matches the
        // ingest envelope (measured hang after "done:" on OTB 2025 re-ingest).
        long novelRows = result.EntitiesInserted + result.PhysicalitiesInserted
            + result.AttestationsInserted;
        if (novelRows > 0)
        {
            try { await PrintIngestValidationAsync(ds, dec, exactSourceValidation: false); }
            catch (Exception ex)
            { Console.Error.WriteLine($"warn: ingest validation failed (ingest itself is complete): {ex.Message}"); }
        }
        return 0;
    }

    public static async Task<int> StatsAsync(string? sourceKey = null)
    {
        IDecomposer? decomposer = null;
        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            try { decomposer = CliRuntime.Decomposers.Resolve(sourceKey); }
            catch (ArgumentException ex) { return Fail(ex.Message); }
        }
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await PrintIngestValidationAsync(ds, decomposer, exactSourceValidation: true);
        return 0;
    }

    // Close a cut-off journal row ('running' with no live process) through the
    // installed op. The op refuses non-running rows; the operator owes the
    // liveness check first — never close a run another process still owns.
    public static async Task<int> CloseRunAsync(string runId, string status)
    {
        if (!Guid.TryParse(runId, out var run))
        { Console.Error.WriteLine($"close-run: '{runId}' is not a run_id (uuid)"); return 2; }
        if (status is not ("cancelled" or "failed"))
        { Console.Error.WriteLine($"close-run: status must be cancelled|failed, got '{status}'"); return 2; }
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();
        try
        {
            await NpgsqlIngestOps.CloseIngestRunAsync(conn, run, status);
        }
        catch (PostgresException ex)
        {
            Console.Error.WriteLine($"close-run: {ex.MessageText}");
            return 1;
        }
        Console.WriteLine($"closed run {run} -> {status}");
        return 0;
    }

    // Verify a source's relation-law bootstrap rows landed (#760's positive
    // control) through the installed op. The law relation is the OPERATOR's
    // declaration, supplied at invocation (G3: production code embeds no
    // governed vocabulary). Exit 0 present, 1 absent.
    public static async Task<int> SourceBootstrapAsync(string sourceName, string lawRelation)
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();
        bool present = await NpgsqlIngestOps.SourceBootstrapPresentAsync(
            conn, sourceName, lawRelation);
        Console.WriteLine($"{sourceName}: bootstrap_present={(present ? "true" : "false")}");
        return present ? 0 : 1;
    }

    // Restore secondary indexes a legacy killed/crashed index-cycle ingest left absent and
    // journaled. Current ingest never drops indexes; this remains only to repair an upgraded
    // database already in that state. Refresh planner statistics after recovery.
    public static async Task<int> RecoverCycledIndexesAsync()
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);

        long pending = await NpgsqlIngestOps.IndexCycleJournalCountAsync(ds);
        if (pending == 0)
        {
            Console.WriteLine("index-cycle journal empty — nothing to recover");
            return 0;
        }

        Console.WriteLine($"recovering {pending} journaled secondary index(es) — serial builds ...");
        var log = CliRuntime.LoggerFactory.CreateLogger("index-cycle");
        var sw = Stopwatch.StartNew();
        await NpgsqlIndexCycle.RebuildJournaledAsync(ds, log, CancellationToken.None);

        Console.WriteLine("refreshing planner statistics ...");
        await NpgsqlIngestOps.AnalyzeCoreWriteTablesAsync(ds);
        sw.Stop();

        long remaining = await NpgsqlIngestOps.IndexCycleJournalCountAsync(ds);
        Console.WriteLine(
            $"recovered {pending - remaining}/{pending} index(es) in {sw.Elapsed.TotalSeconds:F0}s"
            + (remaining > 0 ? $" — {remaining} still journaled (rerun)" : ""));
        return remaining == 0 ? 0 : 1;
    }

    public static async Task<int> RebuildPhysIndexesAsync()
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        // Always EnsureIndexesAsync (Copilot #859): an early return on "any
        // secondary index exists" skips newly-added defs on a partially-recovered
        // database. DDL is CREATE INDEX IF NOT EXISTS, so re-running is cheap.
        Console.WriteLine("ensuring physicalities indexes (CREATE IF NOT EXISTS) ...");
        var sw = Stopwatch.StartNew();
        await SecondaryIndexPolicy.EnsureIndexesAsync(ds, SchemaPhysIndexDefs, CancellationToken.None);
        sw.Stop();
        Console.WriteLine($"physicalities secondary indexes ensured in {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    // Mirrors extension/laplace_substrate/sql/indexes/*.sql.in for the recovery command
    // that rebuilds physicalities indexes on an existing database. Keep in sync with the
    // extension — it is the deployment unit and the authority. radius_origin and
    // alignment_residual were removed 2026-07-28 (0 scans; see those .sql.in files).
    //
    // physicalities_hilbert_btree belongs here. It was in this list before the schema had
    // a .sql.in for it, and that was CORRECT rather than drift: the primary key used to be
    // (hilbert_index, id), so hilbert was covered by the PK's leading column and a separate
    // btree would have been redundant in the schema while this recovery path still had to
    // create it. Repartitioning to HASH(id) forced the PK to (id) and silently removed the
    // only hilbert coverage, which broke structural.anagrams_of()'s equality join into a sequential
    // scan of all 64 partitions. The index is now declared in the schema too
    // (indexes/physicalities_hilbert_btree.sql.in), so both agree.
    //
    // physicalities_traj_first_id_btree IS in the schema and was missing here, so a
    // recovery run left the database short an index it is supposed to have.
    //
    // Drift in this list is not cosmetic: the command exists to restore the schema's index
    // set, so whatever is wrong here gets written to a live database as if it were the
    // schema.
    private static readonly string[] SchemaPhysIndexDefs =
    [
        "CREATE INDEX IF NOT EXISTS physicalities_entity_btree ON laplace.physicalities USING btree (entity_id)",
        "CREATE INDEX IF NOT EXISTS physicalities_type_btree ON laplace.physicalities USING btree (type)",
        "CREATE INDEX IF NOT EXISTS physicalities_coord_gist ON laplace.physicalities USING gist (coord gist_geometry_ops_nd)",
        "CREATE INDEX IF NOT EXISTS physicalities_direction_gist ON laplace.physicalities USING gist (public.laplace_direction_4d(coord) gist_geometry_ops_nd) WHERE type = 1 AND public.laplace_direction_4d(coord) IS NOT NULL",
        "CREATE INDEX IF NOT EXISTS physicalities_hilbert_btree ON laplace.physicalities USING btree (hilbert_index)",
        "CREATE INDEX IF NOT EXISTS physicalities_observed_brin ON laplace.physicalities USING brin (observed_at)",
        "CREATE INDEX IF NOT EXISTS physicalities_traj_probe ON laplace.physicalities USING btree (observed_at) WHERE type = 1 AND trajectory IS NOT NULL",
        "CREATE INDEX IF NOT EXISTS physicalities_traj_first_id_btree ON laplace.physicalities USING btree ((public.laplace_trajectory_constituent_ids(trajectory))[1]) WHERE trajectory IS NOT NULL AND type = 1",
        "CREATE INDEX IF NOT EXISTS physicalities_constituents_gin ON laplace.physicalities USING gin (public.laplace_trajectory_constituent_ids(trajectory)) WHERE type = 1 AND trajectory IS NOT NULL",
    ];

    private static async Task RegisterDynamicCanonicalsAsync(
        NpgsqlDataSource ds, IDecomposer decomposer)
    {
        var names = new HashSet<string>(decomposer.CanonicalNamesForReadback, StringComparer.Ordinal);
        names.Add($"substrate/source/{decomposer.SourceName}/v1");
        if (names.Count == 0) return;
        await NpgsqlCanonicalRegistry.RegisterCanonicalsAsync(ds, names);
        Console.WriteLine($"registered {names.Count:N0} canonical names");
    }

    private static async Task PrintIngestValidationAsync(
        NpgsqlDataSource ds,
        IDecomposer? decomposer,
        bool exactSourceValidation)
    {
        await using var conn = await ds.OpenConnectionAsync();
        var phase = Stopwatch.StartNew();

        // Immediately after a bulk COPY ingest the just-loaded tables can still carry
        // pre-load planner statistics (autoanalyze has not necessarily caught up). Refresh
        // the columns used by the UI and read paths. Column-scoped so we skip the
        // minutes-long PostGIS ND-stats on physicalities.coord/trajectory.
        await NpgsqlIngestOps.AnalyzePostIngestValidationAsync(conn);
        long analyzeMs = phase.ElapsedMilliseconds;
        phase.Restart();

        // The write burst is over: drain the GIN pending lists so the first reader
        // after a seed does not scan them linearly. See CleanGinPendingListsAsync —
        // this is what lets gin_pending_list_limit be sized for bulk-load batching
        // without taxing the containment probe the read model runs on.
        await NpgsqlIngestOps.CleanGinPendingListsAsync(conn);
        long ginMs = phase.ElapsedMilliseconds;
        phase.Restart();

        Task<long> EvidenceForSource(string sourceKey) =>
            NpgsqlIngestOps.EvidenceCountForSourceNameAsync(conn, sourceKey);
        Task<long> ContentForSource(string sourceKey) =>
            NpgsqlIngestOps.ContentCountForSourceNameAsync(conn, sourceKey);
        Task<long> RelationEvidence(string relationType, string? sourceKey = null) =>
            NpgsqlIngestOps.EvidenceCountForRelationAsync(conn, relationType, sourceKey);
        Task<long> RelationEvidenceForSourceId(string relationType, Hash128 sourceId) =>
            NpgsqlIngestOps.EvidenceCountForRelationAndSourceIdAsync(
                conn, relationType, sourceId.ToBytes());

        Console.WriteLine("substrate counts (pg_class.reltuples ESTIMATE — not count(*); run ANALYZE, ops.evidence_count(), or ops.substrate_counts() for exact):");
        {
            var counts = await NpgsqlSubstrateReads.SubstrateCountsAsync(conn, CancellationToken.None);
            foreach (var row in counts)
                Console.WriteLine($"  {row.Metric,-32}: {row.Value,12:N0}");
        }
        long summaryMs = phase.ElapsedMilliseconds;
        Console.WriteLine(
            $"LAPSIGHT_POST_INGEST analyze_ms={analyzeMs} gin_drain_ms={ginMs} "
            + $"summary_ms={summaryMs} exact_source_validation={exactSourceValidation.ToString().ToLowerInvariant()}");

        // Keep automatic ingest completion bounded. The stats refresh above is necessary
        // for the UI and planner, and the GIN drain protects the first reader. Exact
        // source-content attribution is a diagnostic scan, not part of committing an
        // ingest. MEASURED 2026-08-19: ChessPgn finished 655,255 games in 3,655s, then
        // ops.content_count(source) read for another 815s until cancelled; the journal and
        // workflow were already waiting on a successful ingest. The workflow's decomposer
        // gate proves the indexed witness relations. Operators can request the unbounded
        // exact diagnostic explicitly with `laplace stats <cli-source>`.
        if (decomposer is not null && !exactSourceValidation)
        {
            Console.WriteLine(
                $"  witness [{decomposer.SourceName}] exact source counts deferred "
                + "(workflow gate; explicit: laplace stats <cli-source>)");
            return;
        }

        if (decomposer is null)
        {
            // ops.source_counts() joins a count(DISTINCT physicalities) per source —
            // unbounded at 135M attestations; `stats` hung for minutes (Issue 52). The
            // evidence half alone walks attestations_source_btree in ~30s live. Content
            // per source stays exact via `stats <source>` (content_count is per-source
            // bounded).
            Console.WriteLine("  witnesses (evidence per source; content: run `stats <source>`):");
            try
            {
                foreach (var row in await NpgsqlIngestOps.AttestationCountsBySourceAsync(conn, timeoutSeconds: 120))
                    Console.WriteLine($"    {row.Source,-44}: {row.Evidence,12:N0} att");
            }
            catch (Exception ex) when (ex is NpgsqlException { InnerException: TimeoutException } or TimeoutException)
            {
                Console.WriteLine("    (source grouping exceeded 120s — per-source: `stats <source>`, exact via ops.evidence_count)");
            }
            return;
        }

        string srcKey = decomposer.SourceName;
        long att = await EvidenceForSource(srcKey);
        long content = await ContentForSource(srcKey);
        bool layerOk = await NpgsqlIngestOps.LayerMarkedCompleteAsync(conn, decomposer.LayerOrder, srcKey);
        Console.WriteLine($"  witness [{srcKey}] L{decomposer.LayerOrder}: {att:N0} attestations, {content:N0} content, layer_complete={layerOk}");

        // Executable source-content receipt. This is generated from the SAME static/runtime
        // manifest Initialize registered, then counted by the source id actually stamped on
        // evidence. It therefore exposes declared-but-empty relations as evidence=0 instead
        // of letting a broad source total or a hand-picked smoke relation stand in for them.
        foreach (string relation in decomposer.DeclaredRelations.Distinct(StringComparer.Ordinal))
        {
            long evidence = await RelationEvidenceForSourceId(relation, decomposer.SourceId);
            Console.WriteLine(
                $"SEED_CONTENT_RECEIPT source={srcKey} source_id={decomposer.SourceId} "
                + $"relation={relation} evidence={evidence}");
        }

        // Predicate Matrix is a distinct witness carried by the SemLink seed operation.
        // Reporting only SemLinkDecomposer hid 641k PM attestations and made it impossible
        // to distinguish "SemLink ran" from "the matrix was actually admitted".
        if (srcKey == "SemLinkDecomposer")
        {
            foreach (string relation in PredicateMatrixSource.Relations.Distinct(StringComparer.Ordinal))
            {
                long evidence = await RelationEvidenceForSourceId(
                    relation, PredicateMatrixSource.SourceId);
                Console.WriteLine(
                    $"SEED_CONTENT_RECEIPT source=PredicateMatrixDecomposer "
                    + $"parent=SemLinkDecomposer source_id={PredicateMatrixSource.SourceId} "
                    + $"relation={relation} evidence={evidence}");
            }
        }

        if (decomposer.LayerOrder == 10)
        {
            // Lane-true model validation (Issue 53b): a model's source id is a CONTENT
            // hash (never source_id(name)), and the recorder/analyzer emit
            // TOKEN_MAPS_TO/MERGES_WITH/APPEARS_IN + per-circuit Projection
            // trajectories — not the retired tensor-role relations.
            byte[] srcId = decomposer.SourceId.ToBytes();
            Task<long> Rel(string rel) =>
                NpgsqlIngestOps.EvidenceCountForRelationAndSourceIdAsync(conn, rel, srcId);
            long maps = await Rel("TOKEN_MAPS_TO");
            long merges = await Rel("MERGES_WITH");
            long occ = await Rel("APPEARS_IN");
            long structure = await Rel("CONTAINS") + await Rel("PRECEDES");
            long circuits = await NpgsqlIngestOps.ModelCircuitTrajectoryCountAsync(conn);
            Console.WriteLine(
                $"  check model deposition: maps_to={maps:N0} merges={merges:N0} "
                + $"appears_in={occ:N0} structure={structure:N0} circuit_trajectories={circuits:N0} "
                + "(source = content hash, trust=AIModelProbe)");
            return;
        }

        switch (srcKey)
        {
            case "UnicodeDecomposer":
                {
                    var probe = await NpgsqlIngestOps.UnicodeCapitalAContentProbeAsync(conn);
                    if (probe.Count > 0)
                    {
                        var r = probe[0];
                        Console.WriteLine("  check U+0041 'A':");
                        Console.WriteLine($"    render  : {r.Render}  tier={r.Tier}");
                        Console.WriteLine($"    coord   : ({r.X:F6}, {r.Y:F6}, {r.Z:F6}, {r.M:F6})");
                    }
                    else Console.WriteLine("  FAIL: no Unicode CONTENT for U+0041");
                    long uniProv = await EvidenceForSource("UnicodeDecomposer");
                    Console.WriteLine($"    provenance: {uniProv:N0} UnicodeDecomposer attestations");
                    break;
                }
            case "ISO639Decomposer":
                {
                    long langs = await RelationEvidence("HAS_ISO639_3_CODE", srcKey)
                               + await RelationEvidence("HAS_ISO639_1_CODE", srcKey)
                               + await RelationEvidence("HAS_ISO639_2_CODE", srcKey);
                    Console.WriteLine($"  check languages: {langs:N0} ISO code attestations");
                    break;
                }
            case "WordNetDecomposer":
                Console.WriteLine($"  check wordnet: IS_A={await RelationEvidence("IS_A", srcKey):N0} "
                                + $"HAS_SENSE={await RelationEvidence("HAS_SENSE", srcKey):N0} "
                                + $"HAS_DEFINITION={await RelationEvidence("HAS_DEFINITION", srcKey):N0}");
                break;
            case "VerbNetDecomposer":
                Console.WriteLine($"  check verbnet: HAS_VERB_FRAME={await RelationEvidence("HAS_VERB_FRAME", srcKey):N0} "
                                + $"HAS_THEMATIC_ROLE={await RelationEvidence("HAS_THEMATIC_ROLE", srcKey):N0}");
                break;
            case "PropBankDecomposer":
                Console.WriteLine($"  check propbank: HAS_SEMANTIC_ROLE={await RelationEvidence("HAS_SEMANTIC_ROLE", srcKey):N0} "
                                + $"HAS_SENSE={await RelationEvidence("HAS_SENSE", srcKey):N0}");
                break;
            case "Atomic2020Decomposer":
                Console.WriteLine($"  check atomic: CAUSES={await RelationEvidence("CAUSES", srcKey):N0} "
                                + $"X_WANT={await RelationEvidence("X_WANT", srcKey):N0}");
                break;
            case "ConceptNetDecomposer":
                Console.WriteLine($"  check conceptnet: RelatedTo={await RelationEvidence("RELATED_TO", srcKey):N0} "
                                + $"IsA={await RelationEvidence("IS_A", srcKey):N0}");
                break;
            case "UDDecomposer":
                string udParse = UDSource.Relations[2];
                string udLanguage = UDSource.Relations[0];
                Console.WriteLine($"  check ud: {udParse}={await RelationEvidence(udParse, srcKey):N0} "
                                + $"{udLanguage}={await RelationEvidence(udLanguage, srcKey):N0}");
                break;
            case "TatoebaDecomposer":
                Console.WriteLine($"  check tatoeba: IS_TRANSLATION_OF={await RelationEvidence("IS_TRANSLATION_OF", srcKey):N0} "
                                + $"HAS_LANGUAGE={await RelationEvidence("HAS_LANGUAGE", srcKey):N0}");
                break;
            case "WiktionaryDecomposer":
                Console.WriteLine($"  check wiktionary: HAS_DEFINITION={await RelationEvidence("HAS_DEFINITION", srcKey):N0} "
                                + $"HAS_EXAMPLE={await RelationEvidence("HAS_EXAMPLE", srcKey):N0}");
                break;
            case "OMWDecomposer":
                Console.WriteLine($"  check omw: HAS_DEFINITION={await RelationEvidence("HAS_DEFINITION", srcKey):N0}");
                break;
            case "CILIDecomposer":
                Console.WriteLine($"  check cili: HAS_DEFINITION={await RelationEvidence("HAS_DEFINITION", srcKey):N0} "
                                + $"HAS_NAME_ALIAS={await RelationEvidence("HAS_NAME_ALIAS", srcKey):N0} "
                                + $"IS_TYPED_AS={await RelationEvidence("IS_TYPED_AS", srcKey):N0}");
                break;
            case "FrameNetDecomposer":
                Console.WriteLine($"  check framenet: HAS_FRAME_ELEMENT={await RelationEvidence("HAS_FRAME_ELEMENT", srcKey):N0}");
                break;
            case "SemLinkDecomposer":
                Console.WriteLine($"  check semlink: CORRESPONDS_TO={await RelationEvidence("CORRESPONDS_TO", srcKey):N0}");
                break;
            case "MapNetDecomposer":
                Console.WriteLine($"  check mapnet: CORRESPONDS_TO={await RelationEvidence("CORRESPONDS_TO", srcKey):N0}");
                break;
            case "WordFrameNetDecomposer":
                Console.WriteLine($"  check wordframenet: CORRESPONDS_TO={await RelationEvidence("CORRESPONDS_TO", srcKey):N0}");
                break;
            case "OpenSubtitlesDecomposer":
                Console.WriteLine($"  check opensubtitles: IS_TRANSLATION_OF={await RelationEvidence("IS_TRANSLATION_OF", srcKey):N0}");
                break;
            default:
                Console.WriteLine($"  check: {att:N0} attestations from this witness");
                break;
        }
    }

}
