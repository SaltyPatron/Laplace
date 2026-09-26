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
using Laplace.Decomposers.Wiktionary;
using Laplace.Decomposers.OpenSubtitles;
using Laplace.Decomposers.VerbNet;
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

internal static partial class IngestCommands
{
    internal sealed record IngestCliArgs(
        string Source,
        string Path,
        string SecondPath,
        LanguageFilter? LangOverride,
        bool? EmitCrossLanguageLinks,
        bool SkipEvidence,
        bool RegisterOnly,
        bool Force = false,
        bool NoAnalyze = false,
        bool Recursive = false,
        int AnalyzeDepth = 0,
        long AnalyzeNodes = 0,
        string? GitCorpusSelection = null,
        string? GitCorpusReceipt = null);

    internal static IngestCliArgs ParseIngestCliArgs(string[] args)
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
        string? gitCorpusSelection = null, gitCorpusReceipt = null;
        for (int i = 0; i < rest.Count;)
        {
            if (rest[i] is "--git-corpus-selection" or "--git-corpus-receipt")
            {
                if (i + 1 >= rest.Count) throw new ArgumentException("Git corpus flag requires a path.");
                if (rest[i] == "--git-corpus-selection") gitCorpusSelection = rest[i + 1];
                else gitCorpusReceipt = rest[i + 1];
                rest.RemoveAt(i + 1); rest.RemoveAt(i);
            }
            else if (rest[i] == "--langs" && i + 1 < rest.Count)
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
                throw new ArgumentException(
                    "Consensus is folded testimony. --no-evidence is not an ingest mode.");
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
        var parsed = new IngestCliArgs(
            rest.Count > 0 ? rest[0] : "",
            rest.Count > 1 ? rest[1] : "",
            rest.Count > 2 ? rest[2] : "",
            langs,
            emitCross,
            skipEvidence,
            registerOnly,
            force,
            noAnalyze,
            recursive,
            analyzeDepth,
            analyzeNodes, gitCorpusSelection, gitCorpusReceipt);
        ValidateVerifiedGitArguments(parsed);
        return parsed;
    }

    private static void ValidateVerifiedGitArguments(IngestCliArgs cli)
    {
        if ((cli.GitCorpusSelection is not null || cli.GitCorpusReceipt is not null)
            && (!cli.Source.Equals("repo", StringComparison.OrdinalIgnoreCase) || cli.GitCorpusSelection is null || cli.GitCorpusReceipt is null
                || cli.Force || cli.SkipEvidence || cli.RegisterOnly || cli.Recursive || cli.SecondPath.Length > 0))
            throw new ArgumentException("Verified Git corpus requires repo, both selection/receipt paths, and ordinary evidence with no force/recursive/register-only override.");
    }

    private static bool ResolvePersistEvidence(IngestCliArgs? cli)
        => cli?.SkipEvidence != true;

    public static async Task<int> IngestAsync(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("chain", StringComparison.OrdinalIgnoreCase))
            return await IngestChainAsync(args[1..]);

        var cli = ParseIngestCliArgs(args);
        if (string.IsNullOrEmpty(cli.Source))
            return Fail("usage: laplace ingest <source> [path] [--langs en,...] [--emit-cross-lang]\n"
                        + "       laplace ingest chain \"<source [path] [flags]>\" ...\n"
                        // Source list comes from the dispatch table TryDispatch routes through.
                        + "  sources: " + string.Join(" | ", IngestDispatchTable.RegisteredKeys.OrderBy(k => k)) + "\n"
                        + "  --langs: language scope for this run\n"
                        + "  chain: run several ingests sequentially in ONE process; Unicode admits its\n"
                        + "         floor before T0 runtime acceleration is mapped; stops at the first failing spec");

        string sourceKey = cli.Source.ToLowerInvariant();

        if (IngestDispatchTable.TryDispatch(sourceKey, cli, out var task))
            return await task;

        return Fail($"unknown ingest source '{cli.Source}' (supported: {string.Join(", ", IngestDispatchTable.RegisteredKeys.OrderBy(k => k))})");
    }

    /// <summary>
    /// Sequential multi-source ingest in one process: each spec is a complete
    /// `ingest` argument vector ("wordnet", "document D:\\data\\text",
    /// "wiktionary --langs en"). Runtime accelerators are loaded only when their
    /// prerequisite foundation has been admitted.
    /// Specs split on whitespace, so a path containing spaces needs its own
    /// single-source invocation. The first nonzero exit stops the chain.
    /// </summary>
    private static async Task<int> IngestChainAsync(string[] specs)
    {
        if (specs.Length == 0)
            return Fail("usage: laplace ingest chain \"<source [path] [flags]>\" ...\n"
                        + "  example: laplace ingest chain unicode iso639 cili wordnet \"document D:\\Data\\Ingest\\test-data\\text\"");

        var parsed = specs.Select(spec => ParseIngestCliArgs(spec.Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))).ToArray();
        for (int i = 0; i < specs.Length; i++)
        {
            var cli = parsed[i];
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
            bool alreadyIngested = await new NpgsqlSubstrateReader(ds)
                .HasSourceCompletedAsync(modelSource, dec.LayerOrder);
            if (alreadyIngested)
            {
                Console.WriteLine($"Safetensor snapshot already deposited — source {modelName}: {modelSource}");
                Console.WriteLine($"(re-deposition refused to prevent consensus contamination; "
                                  + $"reset with db-fresh to test from scratch)");
                return 0;
            }
        }

        // Analyzer modes are calculated re-passes over an already-recorded model
        // (doc 08's chess-analyze pattern): the recorder's completion marker must
        // neither block them nor be re-written.
        return await IngestViaRunnerAsync(dec, ecosystemPath: null, skipLayerCheck: true, cli,
            skipSourceCompletion:
                Laplace.Decomposers.Model.ModelTokenEdgeETL.ResolvePlanesMode() != "structure");
    }

    internal static async Task<int> CorroborateSafetensorSnapshotsAsync(IngestCliArgs cli)
    {
        if (string.IsNullOrWhiteSpace(cli.Path) || string.IsNullOrWhiteSpace(cli.SecondPath))
            return Fail(
                "usage: laplace ingest model-corroborate <first-snapshot> <second-snapshot>\n"
                + "  Both content-distinct snapshots must already be deposited model sources.");
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();

        using SelectedModelAnalysisEstate estate =
            SelectedModelAnalysisEstate.Open(cli.Path, cli.SecondPath);
        await using var ds = LaplaceDataSource.Create(
            SubstrateAccess.Ingest,
            b => b.ConnectionStringBuilder.CommandTimeout = 0,
            ConnString);
        var reader = new NpgsqlSubstrateReader(ds);
        IReadOnlySet<Hash128> completed = await reader.HasSourcesCompletedAsync(
            [estate.Left.SourceId, estate.Right.SourceId], layerOrder: 10);
        if (completed.Count != 2)
            return Fail(
                "model-corroborate requires both selected checkpoint identities to be deposited "
                + "before their calculated joint analysis can be admitted");

        var loggerFactory = CliRuntime.LoggerFactory;
        var inner = new NpgsqlSubstrateWriter(
            ds, logger: loggerFactory.CreateLogger<NpgsqlSubstrateWriter>());
        var writer = new ConsensusAccumulatingWriter(
            inner, ds, persistEvidence: ResolvePersistEvidence(cli),
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        var sw = Stopwatch.StartNew();
        ModelJointCorroborationResult result = await estate.AnalyzeAndApplyAsync(
            commitEpoch: 1, reader, writer, CancellationToken.None);
        await writer.DrainFoldsAsync();
        sw.Stop();
        Console.WriteLine(
            $"model corroboration: sources={result.LeftSource},{result.RightSource} "
            + $"proposed={result.ProposedPairs:N0} admitted={result.AdmittedPairs:N0} "
            + $"receipts_inserted={result.AttestationsInserted:N0} "
            + $"working_sets={result.WorkingSets:N0} replay={result.AnyJournalReplay} "
            + $"native_peak_bytes={result.PeakNativeResidentBytes:N0} "
            + $"score_peak_bytes={result.PeakTransientScoreBytes:N0} "
            + $"elapsed_s={sw.Elapsed.TotalSeconds:F1}");
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

        // The document decomposer records completion per file; skipSourceCompletion stays
        // false so the terminal source-level marker is also written for layer ordering.
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

    // code/repo/agents/tabular/parquet are path-parameterized: each invocation admits
    // whatever the path names, and content addressing makes a re-run over the same or
    // different content converge. They pass skipSourceCompletion: true, because the
    // source-level completion marker would turn every later run into a no-op regardless
    // of path. That marker belongs to fixed global corpora (wordnet, conceptnet, ...).

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
        ValidateVerifiedGitArguments(cli);
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace ingest repo <repository-root>");
        IDecomposer decomposer = CliRuntime.Decomposers.Resolve("repo");
        if (cli.GitCorpusSelection is not null)
        {
            var selection = System.Text.Json.JsonSerializer.Deserialize<VerifiedGitRepository.Selection>(
                File.ReadAllBytes(cli.GitCorpusSelection)) ?? throw new InvalidDataException("Missing Git selection.");
            ValidateGitCorpusOutputPaths(path, cli.GitCorpusSelection, cli.GitCorpusReceipt!);
            var snapshot = VerifiedGitRepository.Capture(path, selection, (file, bytes) =>
            {
                string? modality = RepoDecomposer.VerifiedModalityFor(file, selection.RequiredModality);
                if (modality is not null) return (modality, null);
                return GrammarSourceFileSupport.IsExactNativeText(bytes)
                    ? ("text", null)
                    : (null, "No native grammar and bytes are not an exact nonempty UTF-8/NFC text representation.");
            });
            decomposer = new VerifiedGitRepoDecomposer(snapshot);
        }
        return await IngestViaRunnerAsync(
            decomposer, path, skipLayerCheck: true, cli, skipSourceCompletion: true);
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
    /// Line-delimited formats, where mean line length is the record size. Other formats
    /// (XML, Parquet, ...) keep the declared EstBytesPerRecord.
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
        // LAPLACE_INGEST_MAX_UNITS caps input volume (an operator scope, like --langs).
        // Batch/commit sizing comes from IngestSizing/MemoryTopology only.
        long maxUnits =
            long.TryParse(Environment.GetEnvironmentVariable("LAPLACE_INGEST_MAX_UNITS"),
                out var mu) && mu > 0 ? mu : 0;
        var profile = sizingProfile ?? IngestSourceProfile.Default;
        // EstBytesPerRecord is the denominator of the per-worker memory budget. For a
        // line-delimited input file it is measured from that file, since one source can
        // admit files with very different record sizes; otherwise the declared profile stands.
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
            // Suppressing completion and bypassing its pre-run guard are separate:
            // path-parameterized sources suppress the marker, while --force only bypasses
            // the guard and still records terminal layer completion.
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
        IDecomposer dec, string? ecosystemPath, bool skipLayerCheck, IngestCliArgs? cli = null,
        bool skipSourceCompletion = false)
    {
        // The codepoint perfcache is a read-only map over admitted Tier-0; content
        // witnessing composes through it, so it is loaded for every source, including
        // Unicode, whose admission still writes the Tier-0 rows from UCD.
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();
        var topo = IngestTopology.EnsureReady();

        NativeCorpusRuntime? corpusRuntime = dec is RepoDecomposer { VerifiedRepository: not null }
            ? ObserveCorpusRuntime() : null;
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
        // Every source runs the generic ingest order: extract the whole source into
        // staging, then load the staged set once (novelty trunk to leaf, new records leaf
        // to trunk, merged claims) and score it (StagedSourceWriter).
        await using var staged = new StagedSourceWriter(accumulator, ds, innerWriter.Durability,
            IngestTopology.Current.ApplyPartitions, loggerFactory.CreateLogger<StagedSourceWriter>());
        writer = staged;
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader, loggerFactory,
            new NpgsqlIngestObservability(ds, persistEvidence));

        string destination = dec is RepoDecomposer { VerifiedRepository: not null }
            ? "configured PostgreSQL (verified Git corpus)" : ConnString;
        Console.WriteLine($"ingest {dec.GetType().Name} source={dec.SourceName} via IngestRunner → {destination} ..."
            + (persistEvidence ? "" : " (consensus-only, no attestation writes)"));
        var sw = Stopwatch.StartNew();
        var options = BuildIngestOptions(sw, dec.SourceName, skipLayerCheck, ecosystemPath, cli,
            skipSourceCompletion,
            sizingProfile: dec.SizingProfile);
        // A language scope admits every tag of the languages it names, by ISO 639 testimony.
        options = options with
        {
            DecomposerOptions = options.DecomposerOptions with
            {
                Languages = await LanguageTagExpansion.ExpandAsync(
                    options.DecomposerOptions.Languages, ds).ConfigureAwait(false),
            },
        };
        var result = await runner.RunAsync(dec, options, CancellationToken.None);
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
        Console.WriteLine($"consensus: {((IConsensusFoldMetrics)writer).CellsFolded:N0} cells materialized during ingest "
                        + $"from {((IConsensusFoldMetrics)writer).ObservationsAccumulated:N0} observations "
                        + "(queued folds drained before success)");

        // ANALYZE and validation counts are not part of the fold; they run only when the
        // ingest added rows.
        long novelRows = result.EntitiesInserted + result.PhysicalitiesInserted
            + result.AttestationsInserted;
        if (novelRows > 0)
        {
            try { await PrintIngestValidationAsync(ds, dec, exactSourceValidation: false); }
            catch (Exception ex)
            { Console.Error.WriteLine($"warn: ingest validation failed (ingest itself is complete): {ex.Message}"); }
        }
        if (dec is RepoDecomposer { VerifiedRepository: not null } repository)
            await WriteVerifiedGitCorpusReceiptAsync(ds, repository, result,
                accumulator.ObservationsAccumulated, accumulator.CellsFolded,
                cli?.GitCorpusReceipt ?? throw new InvalidDataException("Missing corpus receipt path."),
                corpusRuntime ?? throw new InvalidDataException("Missing loaded runtime identity."));
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
    // installed op. The op refuses non-running rows; it does not check liveness, so
    // the caller must know no other process still owns the run.
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

    // Checks through the installed op that a source's relation-law bootstrap rows are
    // present. The law relation is supplied by the caller, not embedded here.
    // Exit 0 present, 1 absent.
    public static async Task<int> SourceBootstrapAsync(string sourceName, string lawRelation)
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();
        bool present = await NpgsqlIngestOps.SourceBootstrapPresentAsync(
            conn, sourceName, lawRelation);
        Console.WriteLine($"{sourceName}: bootstrap_present={(present ? "true" : "false")}");
        return present ? 0 : 1;
    }

    public static async Task<int> RebuildPhysIndexesAsync()
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        // Every definition is issued each time (CREATE INDEX IF NOT EXISTS), so a
        // partially-indexed database gets whatever is missing.
        Console.WriteLine("ensuring physicalities indexes (CREATE IF NOT EXISTS) ...");
        var sw = Stopwatch.StartNew();
        await SecondaryIndexPolicy.EnsureIndexesAsync(ds, SchemaPhysIndexDefs, CancellationToken.None);
        sw.Stop();
        Console.WriteLine($"physicalities secondary indexes ensured in {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    // The physicalities secondary index set declared by
    // extension/laplace_substrate/sql/indexes/*.sql.in, which is the authority. This
    // recovery command writes exactly this list to a live database, so it must match the
    // extension. The Hilbert btree serves Hilbert-locality equality joins (e.g.
    // structural.anagrams_of) under a primary key of (id).
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
        // An explicitly supplied witness id need not use the conventional source
        // namespace. Register only names whose canonical identity is that witness.
        if (SubstrateCanonicalIds.Source(decomposer.SourceName) == decomposer.SourceId)
            names.Add($"substrate/source/{decomposer.SourceName}/v1");
        else if (Hash128.OfCanonical(decomposer.SourceName) == decomposer.SourceId)
            names.Add(decomposer.SourceName);
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

        // After bulk COPY the planner statistics may predate the load. Refresh the columns
        // the read paths use; column-scoped so PostGIS ND-stats on
        // physicalities.coord/trajectory are not recomputed.
        await NpgsqlIngestOps.AnalyzePostIngestValidationAsync(conn);
        long analyzeMs = phase.ElapsedMilliseconds;
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
            $"LAPSIGHT_POST_INGEST analyze_ms={analyzeMs} "
            + $"summary_ms={summaryMs} exact_source_validation={exactSourceValidation.ToString().ToLowerInvariant()}");

        // Exact per-source content attribution is an unbounded diagnostic scan, not part of
        // committing an ingest; after an ingest it is deferred to `laplace stats <cli-source>`.
        if (decomposer is not null && !exactSourceValidation)
        {
            Console.WriteLine(
                $"  witness [{decomposer.SourceName}] exact source counts deferred "
                + "(workflow gate; explicit: laplace stats <cli-source>)");
            return;
        }

        if (decomposer is null)
        {
            // All-source view reads evidence per source only (attestations_source_btree),
            // bounded by a 120 s timeout. Per-source content is `stats <source>`.
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
        bool layerOk = await NpgsqlIngestOps.LayerCompletedAsync(conn, decomposer.LayerOrder, decomposer.SourceId);
        Console.WriteLine($"  witness [{srcKey}] L{decomposer.LayerOrder}: {att:N0} attestations, {content:N0} content, layer_complete={layerOk}");

        // Source-content receipt: every relation the decomposer declares, counted under the
        // source id stamped on its evidence, so a declared-but-empty relation prints evidence=0.
        foreach (string relation in decomposer.DeclaredRelations.Distinct(StringComparer.Ordinal))
        {
            long evidence = await RelationEvidenceForSourceId(relation, decomposer.SourceId);
            Console.WriteLine(
                $"SEED_CONTENT_RECEIPT source={srcKey} source_id={decomposer.SourceId} "
                + $"relation={relation} evidence={evidence}");
        }

        // Predicate Matrix is a distinct witness admitted by the SemLink seed operation;
        // its relations are receipted under its own source id.
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
            // A model's source id is the content hash of its snapshot; its evidence is
            // counted per relation under that id, alongside circuit trajectories.
            byte[] srcId = decomposer.SourceId.ToBytes();
            Task<long> Rel(string rel) =>
                NpgsqlIngestOps.EvidenceCountForRelationAndSourceIdAsync(conn, rel, srcId);
            long merges = await Rel("MERGES_WITH");
            long occ = await Rel("APPEARS_IN");
            long structure = await Rel("CONTAINS") + await Rel("PRECEDES");
            long evidence = await Rel("SIMILAR_TO") + await Rel("ATTENDS")
                            + await Rel("OV_RELATES") + await Rel("COMPLETES_TO");
            long circuits = await NpgsqlIngestOps.ModelCircuitTrajectoryCountAsync(conn);
            Console.WriteLine(
                $"  check model deposition: circuit_evidence={evidence:N0} merges={merges:N0} "
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
                    // Every ISO 639 code is one relation; the scheme is the claim's qualifier.
                    long langs = await RelationEvidence(ISOSource.CodeRelation, srcKey);
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
            case "PropBank":
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
                                + $"{CILISource.Relations[2]}={await RelationEvidence(CILISource.Relations[2], srcKey):N0} "
                                + $"IS_TYPED_AS={await RelationEvidence("IS_TYPED_AS", srcKey):N0}");
                break;
            case "FrameNet":
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
