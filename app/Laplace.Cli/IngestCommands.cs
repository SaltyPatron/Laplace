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
using Laplace.Decomposers.OpenSubtitles;
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
        string? GitCorpusReceipt = null,
        NativeRecipeMode NativeMode = NativeRecipeMode.Ingest,
        bool NativeExpandPrerequisites = true);

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
        var nativeMode = NativeRecipeMode.Ingest;
        int analyzeDepth = 0;
        long analyzeNodes = 0;
        string? gitCorpusSelection = null, gitCorpusReceipt = null;
        for (int i = 0; i < rest.Count;)
        {
            if (rest[i] is "--plan" or "--claims" or "--no-load")
            {
                if (nativeMode != NativeRecipeMode.Ingest)
                    throw new ArgumentException("Choose one recipe preview mode.");
                nativeMode = rest[i] switch
                {
                    "--plan" => NativeRecipeMode.Plan,
                    "--claims" => NativeRecipeMode.Claims,
                    _ => NativeRecipeMode.NoLoad,
                };
                rest.RemoveAt(i);
            }
            else if (rest[i] is "--git-corpus-selection" or "--git-corpus-receipt")
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
            analyzeNodes, gitCorpusSelection, gitCorpusReceipt, nativeMode);
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
        if (args.Length > 0 && args[0] is "catalog" or "--catalog" or "--list-sources")
            return IngestCatalogCommands.Run(args);
        if (args.Length > 0 && args[0].Equals("chain", StringComparison.OrdinalIgnoreCase))
            return await IngestChainAsync(args[1..]);

        var cli = ParseIngestCliArgs(args);
        if (string.IsNullOrEmpty(cli.Source))
            return Fail("usage: laplace-mono ingest <source> [path] [--langs en,...] [--emit-cross-lang]\n"
                        + "       laplace-mono ingest catalog [--json]  — sources, aliases, content, decomposers and prerequisites\n"
                        + "       laplace ingest chain \"<source [path] [flags]>\" ...\n"
                        // Source list comes from the dispatch table TryDispatch routes through.
                        + "  sources: " + string.Join(" | ", IngestDispatchTable.DisplayKeys) + "\n"
                        + "  --langs: language scope for this run\n"
                        + "  chain: run several ingests sequentially in ONE process; Unicode admits its\n"
                        + "         floor before T0 runtime acceleration is mapped; stops at the first failing spec");

        string sourceKey = cli.Source.ToLowerInvariant();

        if (IngestDispatchTable.TryDispatch(sourceKey, cli, out var task))
            return await task;

        return Fail($"unknown ingest source '{cli.Source}' (supported: {string.Join(", ", IngestDispatchTable.DisplayKeys)})");
    }

    /// <summary>
    /// Explicitly sequenced source jobs in one process; Native entries do not
    /// start nested prerequisite campaigns. Each spec is a complete
    /// `ingest` argument vector ("wordnet", "document D:\\data\\text",
    /// "wiktionary --langs en"). Runtime accelerators are loaded only when their
    /// prerequisite foundation has been admitted.
    /// Specs split on whitespace, so a path containing spaces needs its own
    /// single-source invocation. The first nonzero exit stops the chain.
    /// </summary>
    private static async Task<int> IngestChainAsync(string[] specs)
    {
        if (specs.Length == 0)
            return Fail("usage: laplace-mono ingest chain \"<source [path] [flags]>\" ...\n"
                        + "  example: laplace ingest chain unicode iso639 cili wordnet \"document D:\\Data\\Ingest\\test-data\\text\"");

        var parsed = specs.Select(spec => ParseIngestCliArgs(spec.Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))).ToArray();
        for (int i = 0; i < parsed.Length; i++)
        {
            if (string.IsNullOrEmpty(parsed[i].Source))
                return Fail($"ingest chain: spec {i + 1} is empty; no sources started");
            if (!IngestDispatchTable.IsRegistered(parsed[i].Source))
                return Fail($"ingest chain: unknown source '{parsed[i].Source}' in spec {i + 1}; no sources started "
                            + $"(supported: {string.Join(", ", IngestDispatchTable.DisplayKeys)})");
        }
        for (int i = 0; i < specs.Length; i++)
        {
            // The caller supplied this sequence; do not start a second
            // prerequisite campaign inside each of its Native source jobs.
            var cli = parsed[i] with { NativeExpandPrerequisites = false };
            Console.WriteLine($"==== chain [{i + 1}/{specs.Length}]: ingest {specs[i]} ====");
            if (!IngestDispatchTable.TryDispatch(cli.Source.ToLowerInvariant(), cli, out var task))
                return Fail($"ingest chain: unknown source '{cli.Source}' in spec {i + 1} "
                            + $"(supported: {string.Join(", ", IngestDispatchTable.DisplayKeys)})");
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

    internal static Task<int> OmwProbeAsync(IngestCliArgs cli)
        => IngestNativeRecipeAsync("open-multilingual-wordnet", cli with { NativeMode = NativeRecipeMode.NoLoad });

    internal static async Task<int> IngestSafetensorSnapshotAsync(string modelDir, IngestCliArgs cli)
    {
        if (string.IsNullOrEmpty(modelDir))
            return Fail("usage: laplace-mono ingest safetensors <snapshot-dir>\n"
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
                "usage: laplace-mono ingest model-corroborate <first-snapshot> <second-snapshot>\n"
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
        string docPath = string.IsNullOrEmpty(cli.Path)
            ? IngestDataPaths.Resolve("document")
            : cli.Path;
        if (!File.Exists(docPath) && !Directory.Exists(docPath))
            return Fail($"ingest document: path not found: {docPath}");

        // The document decomposer records completion per file; skipSourceCompletion stays
        // false so the terminal source-level marker is also written for layer ordering.
        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve("document"),
            Path.GetFullPath(docPath),
            skipLayerCheck: true,
            cli,
            skipSourceCompletion: false);
    }

    internal static async Task<int> IngestProjectGutenbergAsync(IngestCliArgs cli)
    {
        string docPath = IngestDataPaths.Resolve("project-gutenberg", cli.Path);
        if (!File.Exists(docPath) && !Directory.Exists(docPath))
            return Fail($"ingest project-gutenberg: path not found: {docPath}");

        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve("document"),
            Path.GetFullPath(docPath),
            skipLayerCheck: true,
            cli,
            skipSourceCompletion: false);
    }

    internal static async Task<int> IngestRecipeAsync(IngestCliArgs cli)
    {
        if (string.IsNullOrEmpty(cli.Path))
            return Fail("usage: laplace-mono ingest recipe <recipe.json>\n"
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
            return Fail("usage: laplace-mono ingest code <file-or-directory>");
        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve("code"), path, skipLayerCheck: true, cli, skipSourceCompletion: true);
    }

    internal static async Task<int> IngestRepoAsync(IngestCliArgs cli)
    {
        ValidateVerifiedGitArguments(cli);
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace-mono ingest repo <repository-root>");
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
            return Fail("usage: laplace-mono ingest tabular <file-or-directory>");
        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve("tabular"), path, skipLayerCheck: true, cli, skipSourceCompletion: true);
    }

    internal static async Task<int> IngestParquetAsync(IngestCliArgs cli)
    {
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace-mono ingest parquet <file-or-directory>");
        return await IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve("parquet"), path, skipLayerCheck: true, cli, skipSourceCompletion: true);
    }
}
