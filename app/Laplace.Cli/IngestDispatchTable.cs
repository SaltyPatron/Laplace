using System.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.CILI;
using Laplace.Decomposers.Code;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.ISO;
using Laplace.Decomposers.OMW;
using Laplace.Decomposers.OpenSubtitles;
using Laplace.Decomposers.SemLink;
using Laplace.Decomposers.Tatoeba;
using Laplace.Decomposers.Unicode;
using Laplace.Decomposers.VerbNet;
using Laplace.Decomposers.Wiktionary;
using Laplace.Decomposers.WordNet;

namespace Laplace.Cli;

/// <summary>
/// Table-driven ingest dispatch. A selected source-generation recipe for the key is routed
/// first; otherwise the keyed routes below name a decomposer. Every route admits through
/// the same IngestRunner recipe.
/// </summary>
internal static class IngestDispatchTable
{
    internal delegate Task<int> IngestHandler(IngestCommands.IngestCliArgs cli);

    /// <summary>
    /// Sources whose dispatch is determined by their key alone: resolve the decomposer
    /// and the data path, and hand both to the runner with the layer-order precondition.
    /// </summary>
    private static readonly string[] StandardSources =
    [
        "atomic2020",
        "cili",
        "conceptnet",
        "framenet",
        "mapnet",
        "omw",
        "operational",
        "opensubtitles",
        "propbank",
        "semlink",
        "tatoeba",
        "ud",
        "verbnet",
        "wiktionary",
        "wordframenet",
        "wordnet",
    ];

    /// <summary>
    /// Same shape without the layer-order precondition: these sources resume per file
    /// and own their completion marking.
    /// </summary>
    private static readonly string[] StandardSourcesNoLayerCheck =
    [
        "stack",
        "tiny-codes",
        "rgba-image",
        "track-audio",
        "frame-video",
    ];

    private static IngestHandler Standard(string key, bool skipLayerCheck) =>
        cli => IngestCommands.IngestViaRunnerAsync(
            CliRuntime.Decomposers.Resolve(key), IngestDataPaths.Resolve(key, cli.Path),
            skipLayerCheck, cli);


    /// <summary>
    /// Sources whose dispatch is not determined by the key alone: a bespoke entry point,
    /// a decomposer constructed from CLI flags, or a lane that manages its own source
    /// completion.
    /// </summary>
    private static readonly (string Key, IngestHandler Handler)[] Exceptions =
    [
        ("unicode",  cli => IngestCommands.IngestUnicodeViaRunnerAsync(cli)),
        ("iso639",   cli => IngestCommands.IngestISO639Async(cli)),
        ("code",     cli => IngestCommands.IngestCodeAsync(cli)),
        ("repo",     cli => IngestCommands.IngestRepoAsync(cli)),
        ("tabular",  cli => IngestCommands.IngestTabularAsync(cli)),
        ("parquet",  cli => IngestCommands.IngestParquetAsync(cli)),
        ("document", cli => IngestCommands.IngestDocumentAsync(cli)),
        ("recipe",   cli => IngestCommands.IngestRecipeAsync(cli)),
        ("agents",   cli => IngestCommands.IngestAgentsAsync(cli)),
        ("omw-probe", cli => IngestCommands.OmwProbeAsync(cli)),
        ("model-corroborate", cli => IngestCommands.CorroborateSafetensorSnapshotsAsync(cli)),

        // `chess` records games and derives the calculated layer in one Compose pass over
        // the in-memory parse.
        ("chess", IngestChessRecordAndAnalyzeAsync),

        ("chess-analyze", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessAnalyzeDecomposer(cli.AnalyzeDepth), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Deposits the game trajectory physicality onto recorded games. A separate lane, not
        // a ChessAnalyze.Version bump: consensus merge accumulates, so re-deriving analysis
        // would double every observation_count. A physicality upserts, so re-running is safe.
        ("chess-trajectory", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessTrajectoryDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Transition testimony over recorded games. Separate from ChessAnalysis because
        // bumping its marker would double every standing calculated observation count.
        ("chess-transitions", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessTransitionsDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Move-outcome fold over recorded games: each witnessed line's result is deposited as
        // aggregated OUTCOME testimony on its MOVE entities, so the outcome table is a
        // consensus read, not a read-time fold. Marker-gated per line.
        ("chess-move-outcomes", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessMoveOutcomesDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Reusable board-constituent outcome fold. Search loads this bounded atom census once
        // and evaluates it throughout the tree; no per-node database reads.
        ("chess-position-outcomes", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessPositionOutcomesDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Color-normalized fork/pin/skewer outcomes over recorded games. The fused analyzer
        // writes these for newly analysed games; this marker-gated lane covers games analysed
        // without them, without bumping ChessAnalyze.Version. Search reads the resulting
        // bounded pattern census at leaf evaluation.
        ("chess-tactic-outcomes", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessTacticOutcomesDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Player-context fold. One playing contributes once per player/phase/think class, so a
        // long game gains no extra weight by lasting longer. Separately marker-gated; it never
        // bumps ChessAnalyze.Version or replays its testimony.
        ("chess-player-context-outcomes", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessPlayerContextOutcomesDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Stockfish evaluation over recorded games as calculated testimony. --depth N sets the
        // per-position search depth (default 10); --nodes N caps nodes instead. A run-level memo
        // searches each content-addressed position once however many games share it.
        ("chess-eval", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessStockfishEvalDecomposer(
                cli.AnalyzeDepth > 0 ? cli.AnalyzeDepth : 10,
                cli.AnalyzeNodes), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Syzygy tablebases: path = packaging dir (.rtbw/.rtbz), unpacked via the Fathom codec
        // into position/move/position material trajectories. An empty path resolves to
        // ChessLabPaths.SyzygyDir; a missing dir admits nothing.
        ("chess-syzygy", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessSyzygyDecomposer(), cli.Path ?? "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Names each recorded line's opening by board identity: the deepest position equal to
        // one the ChessOpenings catalog named. A witness beside the PGN header and the
        // analyzer's SAN-prefix reading; marker-gated. No path: it reads admitted structure.
        ("chess-opening-match", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessOpeningMatchDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        ("openings", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessOpeningsDecomposer(cli.Recursive), cli.Path ?? "",
            skipLayerCheck: true, cli)),

        // The book decomposer records and derives per record in one Compose over the
        // in-memory parse, stamping ANALYZED_AT itself.
        ("chess-books", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessBookDecomposer(cli.Recursive), cli.Path ?? "",
            skipLayerCheck: true, cli)),
    ];

    // Declaration order matters: static initializers run top to bottom, so Routes must
    // follow Exceptions or BuildRoutes() reads null and the type fails to initialize.
    private static readonly Dictionary<string, IngestHandler> Routes = BuildRoutes();

    private static Dictionary<string, IngestHandler> BuildRoutes()
    {
        var routes = new Dictionary<string, IngestHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in StandardSources)
            routes[key] = Standard(key, skipLayerCheck: false);
        foreach (var key in StandardSourcesNoLayerCheck)
            routes[key] = Standard(key, skipLayerCheck: true);
        foreach (var (key, handler) in Exceptions)
            routes[key] = handler;
        return routes;
    }


    // Records and derives the calculated layer in one Compose pass (ChessPgnDecomposer ->
    // DeriveFromParsed over the in-memory parse). `--no-analyze` records game grain only and
    // leaves derivation to a later `chess-analyze` run.
    private static Task<int> IngestChessRecordAndAnalyzeAsync(IngestCommands.IngestCliArgs cli)
        => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessPgnDecomposer(cli.Recursive, analyzeInline: !cli.NoAnalyze),
            cli.Path ?? "", skipLayerCheck: true, cli, skipSourceCompletion: true);

    internal static bool TryDispatch(string sourceKey, IngestCommands.IngestCliArgs cli, out Task<int> task)
    {
        if (CliRuntime.Decomposers.TryResolveGeneration(sourceKey, cli.Path, out var configured, out var sourceRoot))
        {
            // A selected generation resumes per file and owns its layer: once every
            // admitted artifact commits, the runner records layer completion for the
            // source so the next layer's precondition can see it. A scoped run commits
            // only its files and leaves the layer to a run over the whole generation.
            bool scoped = configured is Laplace.Decomposers.Structured.Decomposer<Laplace.Decomposers.Structured.SourceGenerationRecipe> { IsScoped: true };
            task = IngestCommands.IngestViaRunnerAsync(configured, sourceRoot,
                skipLayerCheck: configured.LayerOrder == 0, cli, skipSourceCompletion: scoped);
            return true;
        }
        if (Routes.TryGetValue(sourceKey, out var handler))
        {
            task = handler(cli);
            return true;
        }
        if (ModelAliases.Contains(sourceKey, StringComparer.OrdinalIgnoreCase))
        {
            task = IngestCommands.IngestSafetensorSnapshotAsync(cli.Path, cli);
            return true;
        }
        task = default!;
        return false;
    }

    /// <summary>A safetensors snapshot reaches the same handler under three names.</summary>
    private static readonly string[] ModelAliases = ["model", "safetensors", "safetensor"];

    /// <summary>
    /// Every key <see cref="TryDispatch"/> routes: selected source generations, keyed
    /// routes, and model aliases. Help and unknown-source errors read this.
    /// </summary>
    internal static IReadOnlyCollection<string> RegisteredKeys =>
        Routes.Keys
            .Concat(CliRuntime.Decomposers.SelectedGenerationKeys)
              .Concat(ModelAliases)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToArray();
}
