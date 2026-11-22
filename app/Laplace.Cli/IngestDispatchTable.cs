namespace Laplace.Cli;

/// <summary>
/// Canonical corpus names, aliases, and operational lanes dispatch directly to the native Engine CLI.
/// </summary>
internal static class IngestDispatchTable
{
    internal delegate Task<int> IngestHandler(IngestCommands.IngestCliArgs cli);

    private static readonly (string Key, IngestHandler Handler)[] Routes =
    [
        ("code",              cli => IngestCommands.IngestNativeFilesAsync("code", cli)),
        ("repo",              cli => IngestCommands.IngestRepoAsync(cli)),
        ("tabular",           cli => IngestCommands.IngestNativeFilesAsync("tabular", cli)),
        ("document",          cli => IngestCommands.IngestNativeFilesAsync("document",
                                  cli with { Path = IngestDataPaths.Resolve("document", cli.Path) })),
        ("parquet",           cli => IngestCommands.IngestParquetAsync(cli)),
        ("recipe",            cli => IngestCommands.IngestRecipeAsync(cli)),
        ("omw-probe",         cli => IngestCommands.OmwProbeAsync(cli)),
        ("model-corroborate", cli => IngestCommands.CorroborateSafetensorSnapshotsAsync(cli)),
        ("chess",             cli => IngestCommands.IngestNativeFilesAsync("chess", cli)),
        ("rgba-image",        cli => IngestCommands.IngestNativeFilesAsync("rgba-image",
                                  cli with { Path = IngestDataPaths.Resolve("rgba-image", cli.Path) })),
        ("track-audio",       cli => IngestCommands.IngestNativeFilesAsync("track-audio",
                                  cli with { Path = IngestDataPaths.Resolve("track-audio", cli.Path) })),
        ("frame-video",       cli => IngestCommands.IngestNativeFilesAsync("frame-video",
                                  cli with { Path = IngestDataPaths.Resolve("frame-video", cli.Path) })),
        ("stack",             cli => IngestCommands.IngestNativeFilesAsync("stack",
                                  cli with { Path = IngestDataPaths.Resolve("stack", cli.Path) })),
        ("tiny-codes",        cli => IngestCommands.IngestNativeFilesAsync("tiny-codes",
                                  cli with { Path = IngestDataPaths.Resolve("tiny-codes", cli.Path) })),
    ];

    private static readonly Dictionary<string, IngestHandler> RouteMap =
        Routes.ToDictionary(static r => r.Key, static r => r.Handler, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ModelAliases = ["model", "safetensors", "safetensor"];

    internal static bool IsRegistered(string sourceKey)
        => CanonicalSourceKeys.Contains(CanonicalSourceName(sourceKey), StringComparer.OrdinalIgnoreCase)
           || RouteMap.ContainsKey(sourceKey)
           || ModelAliases.Contains(sourceKey, StringComparer.OrdinalIgnoreCase);

    internal static bool TryDispatch(string sourceKey, IngestCommands.IngestCliArgs cli, out Task<int> task)
    {
        string canonical = CanonicalSourceName(sourceKey);
        if (NativeRecipeCatalog.Catalog.Sources.Any(source => source.Name == canonical))
        {
            task = IngestCommands.IngestNativeRecipeAsync(canonical, cli);
            return true;
        }
        if (cli.NativeMode != NativeRecipeMode.Ingest)
        {
            task = Task.FromResult(CliRuntime.Fail("Recipe preview requires a canonical source from recipes/order."));
            return true;
        }
        if (RouteMap.TryGetValue(sourceKey, out var handler))
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

    internal static string CanonicalSourceName(string sourceKey) => NativeRecipeCatalog.CanonicalSourceName(sourceKey);

    internal static IReadOnlyCollection<string> CanonicalSourceKeys
        => NativeRecipeCatalog.Catalog.Sources.Select(source => source.Name).ToArray();

    internal static IReadOnlyCollection<string> RegisteredKeys =>
        CanonicalSourceKeys.Concat(RouteMap.Keys)
              .Concat(ModelAliases)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToArray();

    internal static IReadOnlyCollection<string> DisplayKeys
    {
        get
        {
            var catalog = NativeRecipeCatalog.Catalog;
            var nativeNames = catalog.Sources.Select(source => source.Name).ToHashSet(StringComparer.Ordinal);
            return catalog.Sources.Select(source => source.Aliases.FirstOrDefault() ?? source.Name)
                .Concat(RouteMap.Keys.Where(key => !nativeNames.Contains(catalog.Resolve(key))))
                .Append("safetensors")
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        }
    }
}
