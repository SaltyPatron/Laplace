using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.CILI;
using Laplace.Decomposers.Code;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.FrameNet;
using Laplace.Decomposers.ISO;
using Laplace.Decomposers.OMW;
using Laplace.Decomposers.OpenSubtitles;
using Laplace.Decomposers.PropBank;
using Laplace.Decomposers.SemLink;
using Laplace.Decomposers.Tatoeba;
using Laplace.Decomposers.UD;
using Laplace.Decomposers.Unicode;
using Laplace.Decomposers.VerbNet;
using Laplace.Decomposers.Wiktionary;
using Laplace.Decomposers.WordNet;

namespace Laplace.Cli;

/// <summary>
/// Table-driven ingest dispatch (doc 13 Phase 1). One registry; no special-case
/// ordering forks. Dedicated decomposers win over EtlDecomposer; EtlDecomposer
/// only for manifest rows with <see cref="EtlSource.IsRoutableViaEtl"/>.
/// </summary>
internal static class IngestDispatchTable
{
    internal delegate Task<int> IngestHandler(IngestCommands.IngestCliArgs cli);

    private static readonly Dictionary<string, IngestHandler> Routes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["unicode"] = cli => IngestCommands.IngestUnicodeViaRunnerAsync(cli),
            ["iso639"] = cli => IngestCommands.IngestISO639Async(cli),
            ["atomic2020"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("atomic2020"), IngestDataPaths.Resolve("atomic2020", cli.Path),
                skipLayerCheck: false, cli),
            ["conceptnet"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("conceptnet"), IngestDataPaths.Resolve("conceptnet", cli.Path),
                skipLayerCheck: false, cli),
            ["wiktionary"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("wiktionary"), IngestDataPaths.Resolve("wiktionary", cli.Path),
                skipLayerCheck: false, cli),
            ["omw"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("omw"), IngestDataPaths.Resolve("omw", cli.Path),
                skipLayerCheck: false, cli),
            ["wordnet"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("wordnet"), IngestDataPaths.Resolve("wordnet", cli.Path),
                skipLayerCheck: false, cli),
            ["ud"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("ud"), IngestDataPaths.Resolve("ud", cli.Path),
                skipLayerCheck: false, cli),
            ["tatoeba"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("tatoeba"), IngestDataPaths.Resolve("tatoeba", cli.Path),
                skipLayerCheck: false, cli),
            ["framenet"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("framenet"), IngestDataPaths.Resolve("framenet", cli.Path),
                skipLayerCheck: false, cli),
            ["opensubtitles"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("opensubtitles"), IngestDataPaths.Resolve("opensubtitles", cli.Path),
                skipLayerCheck: false, cli),
            ["verbnet"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("verbnet"), IngestDataPaths.Resolve("verbnet", cli.Path),
                skipLayerCheck: false, cli),
            ["propbank"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("propbank"), IngestDataPaths.Resolve("propbank", cli.Path),
                skipLayerCheck: false, cli),
            ["semlink"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("semlink"), IngestDataPaths.Resolve("semlink", cli.Path),
                skipLayerCheck: false, cli),
            ["mapnet"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("mapnet"), IngestDataPaths.Resolve("mapnet", cli.Path),
                skipLayerCheck: false, cli),
            ["wordframenet"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("wordframenet"), IngestDataPaths.Resolve("wordframenet", cli.Path),
                skipLayerCheck: false, cli),
            ["cili"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("cili"), IngestDataPaths.Resolve("cili", cli.Path),
                skipLayerCheck: false, cli),
            ["code"] = cli => IngestCommands.IngestCodeAsync(cli),
            ["repo"] = cli => IngestCommands.IngestRepoAsync(cli),
            ["tabular"] = cli => IngestCommands.IngestTabularAsync(cli),
            ["tiny-codes"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("tiny-codes"), IngestDataPaths.Resolve("tiny-codes", cli.Path),
                skipLayerCheck: true, cli),
            ["stack"] = cli => IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.Resolve("stack"), IngestDataPaths.Resolve("stack", cli.Path),
                skipLayerCheck: true, cli),
            ["document"] = cli => IngestCommands.IngestDocumentAsync(cli),
            ["recipe"] = cli => IngestCommands.IngestRecipeAsync(cli),
            ["chess"] = IngestChessRecordAndAnalyzeAsync,
            ["chess-analyze"] = cli => IngestCommands.IngestViaRunnerAsync(
                new Laplace.Chess.Service.ChessAnalyzeDecomposer(cli.AnalyzeDepth), "",
                skipLayerCheck: true, cli, skipSourceCompletion: true),
            ["openings"] = cli => IngestCommands.IngestViaRunnerAsync(
                new Laplace.Chess.Service.ChessOpeningsDecomposer(cli.Recursive), cli.Path ?? "",
                skipLayerCheck: true, cli),
            // Single pass: the book decomposer records AND derives per record in one Compose
            // (in-memory parse; no hydrate read-back), stamping ANALYZED_AT itself.
            ["chess-books"] = cli => IngestCommands.IngestViaRunnerAsync(
                new Laplace.Chess.Service.ChessBookDecomposer(cli.Recursive), cli.Path ?? "",
                skipLayerCheck: true, cli),
            ["omw-probe"] = cli => IngestCommands.OmwProbeAsync(cli),
        };

    private static async Task<int> IngestChessRecordAndAnalyzeAsync(IngestCommands.IngestCliArgs cli)
    {
        int rc = await IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessPgnDecomposer(cli.Recursive), cli.Path ?? "",
            skipLayerCheck: true, cli, skipSourceCompletion: true);
        if (rc != 0 || cli.NoAnalyze) return rc;

        Console.WriteLine("chess record complete — running substrate analyze pass on witnessed games…");
        return await IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessAnalyzeDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true);
    }

    internal static bool TryDispatch(string sourceKey, IngestCommands.IngestCliArgs cli, out Task<int> task)
    {
        if (Routes.TryGetValue(sourceKey, out var handler))
        {
            task = handler(cli);
            return true;
        }
        if (sourceKey is "model" or "safetensors" or "safetensor")
        {
            task = IngestCommands.IngestSafetensorSnapshotAsync(cli.Path, cli);
            return true;
        }
        if (EtlManifest.IsRoutable(sourceKey))
        {
            task = IngestCommands.IngestViaRunnerAsync(
                CliRuntime.Decomposers.ResolveEtl(EtlManifest.Get(sourceKey)),
                IngestDataPaths.Resolve(sourceKey, cli.Path),
                skipLayerCheck: false, cli);
            return true;
        }
        task = default!;
        return false;
    }

    internal static IReadOnlyCollection<string> RegisteredKeys => Routes.Keys;
}
