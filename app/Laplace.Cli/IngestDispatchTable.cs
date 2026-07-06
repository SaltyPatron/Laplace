using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.Audio;
using Laplace.Decomposers.CILI;
using Laplace.Decomposers.Code;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.FrameNet;
using Laplace.Decomposers.Image;
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
                new Atomic2020Decomposer(), IngestDataPaths.Resolve("atomic2020", cli.Path),
                skipLayerCheck: false, cli),
            ["conceptnet"] = cli => IngestCommands.IngestViaRunnerAsync(
                new ConceptNetDecomposer(), IngestDataPaths.Resolve("conceptnet", cli.Path),
                skipLayerCheck: false, cli),
            ["wiktionary"] = cli => IngestCommands.IngestViaRunnerAsync(
                new WiktionaryDecomposer(), IngestDataPaths.Resolve("wiktionary", cli.Path),
                skipLayerCheck: false, cli),
            ["omw"] = cli => IngestCommands.IngestViaRunnerAsync(
                new OMWDecomposer(), IngestDataPaths.Resolve("omw", cli.Path),
                skipLayerCheck: false, cli),
            ["wordnet"] = cli => IngestCommands.IngestViaRunnerAsync(
                new WordNetDecomposer(), IngestDataPaths.Resolve("wordnet", cli.Path),
                skipLayerCheck: false, cli),
            ["ud"] = cli => IngestCommands.IngestViaRunnerAsync(
                new UDDecomposer(), IngestDataPaths.Resolve("ud", cli.Path),
                skipLayerCheck: false, cli),
            ["tatoeba"] = cli => IngestCommands.IngestViaRunnerAsync(
                new TatoebaDecomposer(), IngestDataPaths.Resolve("tatoeba", cli.Path),
                skipLayerCheck: false, cli),
            ["framenet"] = cli => IngestCommands.IngestViaRunnerAsync(
                new FrameNetDecomposer(), IngestDataPaths.Resolve("framenet", cli.Path),
                skipLayerCheck: false, cli),
            ["opensubtitles"] = cli => IngestCommands.IngestViaRunnerAsync(
                new OpenSubtitlesDecomposer(), IngestDataPaths.Resolve("opensubtitles", cli.Path),
                skipLayerCheck: false, cli),
            ["verbnet"] = cli => IngestCommands.IngestViaRunnerAsync(
                new VerbNetDecomposer(), IngestDataPaths.Resolve("verbnet", cli.Path),
                skipLayerCheck: false, cli),
            ["propbank"] = cli => IngestCommands.IngestViaRunnerAsync(
                new PropBankDecomposer(), IngestDataPaths.Resolve("propbank", cli.Path),
                skipLayerCheck: false, cli),
            ["semlink"] = cli => IngestCommands.IngestViaRunnerAsync(
                new SemLinkDecomposer(), IngestDataPaths.Resolve("semlink", cli.Path),
                skipLayerCheck: false, cli),
            ["mapnet"] = cli => IngestCommands.IngestViaRunnerAsync(
                new MapNetDecomposer(), IngestDataPaths.Resolve("mapnet", cli.Path),
                skipLayerCheck: false, cli),
            ["wordframenet"] = cli => IngestCommands.IngestViaRunnerAsync(
                new WordFrameNetDecomposer(), IngestDataPaths.Resolve("wordframenet", cli.Path),
                skipLayerCheck: false, cli),
            ["cili"] = cli => IngestCommands.IngestViaRunnerAsync(
                new CILIDecomposer(), IngestDataPaths.Resolve("cili", cli.Path),
                skipLayerCheck: false, cli),
            ["code"] = cli => IngestCommands.IngestCodeAsync(cli),
            ["repo"] = cli => IngestCommands.IngestRepoAsync(cli),
            ["tabular"] = cli => IngestCommands.IngestTabularAsync(cli),
            ["tiny-codes"] = cli => IngestCommands.IngestViaRunnerAsync(
                new TinyCodesDecomposer(), IngestDataPaths.Resolve("tiny-codes", cli.Path),
                skipLayerCheck: true, cli),
            ["stack"] = cli => IngestCommands.IngestViaRunnerAsync(
                new StackDecomposer(), IngestDataPaths.Resolve("stack", cli.Path),
                skipLayerCheck: true, cli),
            ["image"] = cli => IngestCommands.IngestViaRunnerAsync(
                new ImageDecomposer(), IngestDataPaths.Resolve("image", cli.Path),
                skipLayerCheck: true, cli),
            ["audio"] = cli => IngestCommands.IngestViaRunnerAsync(
                new AudioDecomposer(), IngestDataPaths.Resolve("audio", cli.Path),
                skipLayerCheck: true, cli),
            ["document"] = cli => IngestCommands.IngestDocumentAsync(cli),
            ["recipe"] = cli => IngestCommands.IngestRecipeAsync(cli),
            ["chess"] = cli => IngestCommands.IngestViaRunnerAsync(
                new Laplace.Chess.Service.ChessPgnDecomposer(), cli.Path ?? "",
                skipLayerCheck: true, cli),
            ["chess-analyze"] = cli => IngestCommands.IngestViaRunnerAsync(
                new Laplace.Chess.Service.ChessAnalyzeDecomposer(), cli.Path ?? "",
                skipLayerCheck: true, cli, skipSourceCompletion: true),
            ["openings"] = cli => IngestCommands.IngestViaRunnerAsync(
                new Laplace.Chess.Service.ChessOpeningsDecomposer(), cli.Path ?? "",
                skipLayerCheck: true, cli),
            ["omw-probe"] = cli => IngestCommands.OmwProbeAsync(cli),
        };

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
                new EtlDecomposer(EtlManifest.Get(sourceKey)),
                IngestDataPaths.Resolve(sourceKey, cli.Path),
                skipLayerCheck: false, cli);
            return true;
        }
        task = default!;
        return false;
    }

    internal static IReadOnlyCollection<string> RegisteredKeys => Routes.Keys;
}
