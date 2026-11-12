using Laplace.Decomposers.Abstractions;

namespace Laplace.Cli;

/// <summary>
/// Canonical corpus names and aliases execute the Native recipe engine.
/// Explicit operator and derivation commands are dispatched separately.
/// </summary>
internal static class IngestDispatchTable
{
    internal delegate Task<int> IngestHandler(IngestCommands.IngestCliArgs cli);

    /// <summary>
    /// Sources whose dispatch is determined by their key alone: resolve the decomposer
    /// and the data path, and hand both to the runner with the layer-order precondition.
    /// </summary>
    private static readonly string[] StandardSources = [];

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
        // Files outside the recipe estate's corpora run through the Engine's recipes, matched by
        // file name; each file's trunk is its own witness. A verified Git corpus still writes its
        // admission receipt through the managed repository reader.
        ("code",     cli => IngestCommands.IngestNativeFilesAsync("code", cli)),
        ("repo",     cli => cli.GitCorpusSelection is null && cli.GitCorpusReceipt is null
                                ? IngestCommands.IngestNativeFilesAsync("repo", cli)
                                : IngestCommands.IngestRepoAsync(cli)),
        ("tabular",  cli => IngestCommands.IngestNativeFilesAsync("tabular", cli)),
        ("document", cli => IngestCommands.IngestNativeFilesAsync("document",
                                cli with { Path = IngestDataPaths.Resolve("document", cli.Path) })),
        ("parquet",  cli => IngestCommands.IngestParquetAsync(cli)),
        ("recipe",   cli => IngestCommands.IngestRecipeAsync(cli)),
        ("omw-probe", cli => IngestCommands.OmwProbeAsync(cli)),
        ("model-corroborate", cli => IngestCommands.CorroborateSafetensorSnapshotsAsync(cli)),

        // PGN files of no corpus of the estate: the Engine reads each as games (recipes/pgn.recipe),
        // the file's trunk its witness. A corpus (twic, lumbras-gigabase, chess-com, openings, syzygy,
        // laplace-chess) is a canonical source and dispatches by its name.
        ("chess", cli => IngestCommands.IngestNativeFilesAsync("chess", cli)),
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


    // Inspect the same inventory without invoking a handler. Chain preflight must
    // validate every source before any handler can open a database or publish.
    internal static bool IsRegistered(string sourceKey)
        => CanonicalSourceKeys.Contains(CanonicalSourceName(sourceKey), StringComparer.OrdinalIgnoreCase)
           || Routes.ContainsKey(sourceKey)
           || ModelAliases.Contains(sourceKey, StringComparer.OrdinalIgnoreCase);

    internal static bool TryDispatch(string sourceKey, IngestCommands.IngestCliArgs cli, out Task<int> task)
    {
        string canonical = CanonicalSourceName(sourceKey);
        if (EtlManifest.Catalog.Sources.Any(source => source.Name == canonical))
        {
            task = IngestCommands.IngestNativeRecipeAsync(canonical, cli);
            return true;
        }
        if (cli.NativeMode != NativeRecipeMode.Ingest)
        {
            task = Task.FromResult(CliRuntime.Fail("Recipe preview requires a canonical source from recipes/order."));
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

    internal static string CanonicalSourceName(string sourceKey) => EtlManifest.CanonicalSourceName(sourceKey);

    /// <summary>The corpus inventory is exactly the Native directory declaration.</summary>
    internal static IReadOnlyCollection<string> CanonicalSourceKeys => EtlManifest.Catalog.Sources.Select(source => source.Name).ToArray();

    /// <summary>A safetensors snapshot reaches the same handler under three names.</summary>
    private static readonly string[] ModelAliases = ["model", "safetensors", "safetensor"];

    /// <summary>
    /// Canonical corpus keys and explicit operator commands. Input aliases are normalized before dispatch.
    /// Help and unknown-source errors read the same dispatch inventory.
    /// </summary>
    internal static IReadOnlyCollection<string> RegisteredKeys =>
        CanonicalSourceKeys.Concat(Routes.Keys)
              .Concat(ModelAliases)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToArray();

    internal static IReadOnlyCollection<string> DisplayKeys
    {
        get
        {
            var catalog = EtlManifest.Catalog;
            var nativeNames = catalog.Sources.Select(source => source.Name).ToHashSet(StringComparer.Ordinal);
            return catalog.Sources.Select(source => source.Aliases.FirstOrDefault() ?? source.Name)
                .Concat(Routes.Keys.Where(key => !nativeNames.Contains(catalog.Resolve(key))))
                .Append("safetensors")
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        }
    }
}
