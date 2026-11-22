using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

internal static partial class IngestCommands
{
    internal sealed record IngestCliArgs(
        string Source,
        string Path,
        string SecondPath,
        string[]? Langs,
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
        string[]? langs = null;
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
                langs = rest[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
            analyzeNodes,
            gitCorpusSelection,
            gitCorpusReceipt,
            nativeMode);
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

    public static async Task<int> IngestAsync(string[] args)
    {
        if (args.Length > 0 && args[0] is "catalog" or "--catalog" or "--list-sources")
            return IngestCatalogCommands.Run(args);
        if (args.Length > 0 && args[0].Equals("chain", StringComparison.OrdinalIgnoreCase))
            return await IngestChainAsync(args[1..]);

        var cli = ParseIngestCliArgs(args);
        if (string.IsNullOrEmpty(cli.Source))
            return Fail("usage: laplace ingest <source> [path]\n"
                        + "       laplace ingest catalog [--json]\n"
                        + "       laplace ingest chain \"<source [path] [flags]>\" ...\n"
                        + "  sources: " + string.Join(" | ", IngestDispatchTable.DisplayKeys));

        string sourceKey = cli.Source.ToLowerInvariant();
        if (IngestDispatchTable.TryDispatch(sourceKey, cli, out var task))
            return await task;

        return Fail($"unknown ingest source '{cli.Source}' (supported: {string.Join(", ", IngestDispatchTable.DisplayKeys)})");
    }

    private static async Task<int> IngestChainAsync(string[] specs)
    {
        if (specs.Length == 0)
            return Fail("usage: laplace ingest chain \"<source [path] [flags]>\" ...");

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
}
