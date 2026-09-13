using System.Linq;
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

    /// <summary>
    /// Sources whose dispatch is ENTIRELY determined by their key: resolve the
    /// decomposer, resolve the data path, hand both to the runner. Seventeen rows
    /// of this table were byte-identical apart from one repeated string, which is a
    /// switch wearing a dictionary's clothes — the key appeared three times per row
    /// and a typo in any one of them was a runtime-only failure. Listing the keys
    /// once and generating the handlers leaves only genuine exceptions visible
    /// below (chess fusion, model/recipe, code lanes, ETL).
    /// </summary>
    private static readonly string[] StandardSources =
    [
        "atomic2020",
        "cili",
        "conceptnet",
        "framenet",
        "mapnet",
        "omw",
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
    /// Same shape, but the source owns its own completion marking, so the
    /// layer-order precondition does not apply (bulk corpora that resume per file).
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
    /// Sources whose dispatch is NOT determined by the key alone — a bespoke entry
    /// point, a constructed decomposer carrying CLI flags, or a lane that manages
    /// its own source completion. These are the rows that justify a table at all.
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

        // GH #600: `chess` records AND derives the calculated layer in ONE fused
        // Compose pass, reusing the in-memory parse — no second Postgres hydrate.
        ("chess", IngestChessRecordAndAnalyzeAsync),

        ("chess-analyze", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessAnalyzeDecomposer(cli.AnalyzeDepth), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Geometry-only backfill: deposits the game trajectory (spec 11 §2) onto games
        // recorded before it existed. Deliberately NOT a ChessAnalyze.Version bump — that
        // would re-derive ~29M attestations and double every observation_count, since
        // merge accumulates. A physicality is an upsert, so this is safe to re-run.
        ("chess-trajectory", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessTrajectoryDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Testimony-only transition backfill. Separate from ChessAnalysis because bumping its
        // marker would double every standing calculated observation count.
        ("chess-transitions", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessTransitionsDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Move-outcome fold over recorded games: each witnessed line's result deposited as
        // aggregated OUTCOME testimony on its MOVE objects (bounded move vocabulary), so the
        // learned table is a consensus lookup, never a read-time fold. Marker-gated per line.
        ("chess-move-outcomes", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessMoveOutcomesDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Reusable board-constituent outcome fold. Search loads this bounded atom census once
        // and evaluates it throughout the tree; no per-node database reads.
        ("chess-position-outcomes", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessPositionOutcomesDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Reusable tactical-pattern outcome fold. Fork/pin/skewer subjects are color-normalized
        // and bounded, so historical games teach a compact pattern->OUTCOME plane that Search
        // can consume at every static leaf. This is marker-gated and additive; never bump the
        // main analyzer version merely to backfill it.
        ("chess-tactic-outcomes", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessTacticOutcomesDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Stockfish eval pass over recorded games (GH #573). --depth N sets the per-position
        // search depth (default 10 — the v1 census budget); --nodes N switches to a
        // node-capped search (bounded worst case). A run-level memo searches each unique
        // content-addressed position once regardless of how many games share it.
        ("chess-eval", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessStockfishEvalDecomposer(
                cli.AnalyzeDepth > 0 ? cli.AnalyzeDepth : 10,
                cli.AnalyzeNodes), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Syzygy tablebase ingest: path = packaging dir (.rtbw/.rtbz). Unpack via
        // Fathom codec → compact position/move/position material trajectories. Empty path
        // falls back to ChessLabPaths.SyzygyDir. Missing dir = clean no-op.
        ("chess-syzygy", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessSyzygyDecomposer(), cli.Path ?? "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        // Names each recorded line's opening by BOARD IDENTITY -- the deepest position
        // that collides with one the ChessOpenings catalog named. A third witness beside
        // the PGN header and the analyzer's SAN-prefix guess; additive and marker-gated,
        // never a re-derivation of ChessAnalysis. No path: the substrate is the source.
        ("chess-opening-match", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessOpeningMatchDecomposer(), "",
            skipLayerCheck: true, cli, skipSourceCompletion: true)),

        ("openings", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessOpeningsDecomposer(cli.Recursive), cli.Path ?? "",
            skipLayerCheck: true, cli)),

        // Single pass: the book decomposer records AND derives per record in one Compose
        // (in-memory parse; no hydrate read-back), stamping ANALYZED_AT itself.
        ("chess-books", cli => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessBookDecomposer(cli.Recursive), cli.Path ?? "",
            skipLayerCheck: true, cli)),
    ];

    // DECLARATION ORDER IS LOAD-BEARING. Static field initializers run top to
    // bottom, so Routes must be declared AFTER Exceptions — initialized above it,
    // BuildRoutes() reads a null Exceptions and the whole type fails to initialize
    // with a TypeInitializationException on first dispatch. (The MCP tool catalog
    // documents the same trap for the same reason.)
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


    // GH #600: `chess` records AND derives the calculated layer in ONE fused Compose pass
    // (ChessPgnDecomposer -> DeriveFromParsed, reusing the in-memory parse) — no second
    // Postgres hydrate + re-parse. `--no-analyze` records game-grain only and leaves
    // derivation to a later `chess-analyze` backfill (the pre-fusion two-step, opt-in now).
    private static Task<int> IngestChessRecordAndAnalyzeAsync(IngestCommands.IngestCliArgs cli)
        => IngestCommands.IngestViaRunnerAsync(
            new Laplace.Chess.Service.ChessPgnDecomposer(cli.Recursive, analyzeInline: !cli.NoAnalyze),
            cli.Path ?? "", skipLayerCheck: true, cli, skipSourceCompletion: true);

    internal static bool TryDispatch(string sourceKey, IngestCommands.IngestCliArgs cli, out Task<int> task)
    {
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

    private static readonly string[] ModelAliases =
    [
        "safetensors", "hf-model", "bge-m3", "qwen", "qwen3", "minilm", "model",
    ];
}
