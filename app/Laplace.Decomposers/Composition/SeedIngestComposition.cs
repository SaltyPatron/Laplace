using Laplace.Engine.Core;
using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.Code;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.Media;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.OpenSubtitles;
using Laplace.Decomposers.Operational;
using Laplace.Decomposers.SemLink;
using Laplace.Decomposers.Tatoeba;
using Laplace.Decomposers.Wiktionary;
using Laplace.Decomposers.WordNet;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Structured;
using Microsoft.Extensions.DependencyInjection;

namespace Laplace.Decomposers.Composition;

/// <summary>
/// Seed-ingest composition root shared by CLI and API. Decomposers and content adapters
/// are resolved at the host edge, never inside per-record handlers.
/// </summary>
public static class SeedIngestComposition
{
    public static IReadOnlyList<string> RegisteredSourceKeys => Registry.Select(entry => entry.Key).ToArray();
    /// <summary>
    /// Built-in providers by ingest key. A source that has a checked-in source generation
    /// (recipes/**/*.source.json: unicode, iso639, omw, verbnet, cili, oewn, ud, ...) has
    /// NO entry here: it is read only by its recipe, and when that recipe cannot be found
    /// or its root is missing the ingest fails instead of silently running a legacy C#
    /// decomposer (SourceGenerationGateTests). Add a key here only for a source no
    /// recipe reads yet.
    /// </summary>
    internal static readonly (string Key, Type Decomposer)[] Registry =
    [
        ("operational", typeof(OperationalDecomposer)),
        ("atomic2020", typeof(Atomic2020Decomposer)),
        ("conceptnet", typeof(ConceptNetDecomposer)),
        ("wiktionary", typeof(WiktionaryDecomposer)),
        ("wordnet", typeof(WordNetDecomposer)),
        ("tatoeba", typeof(TatoebaDecomposer)),
        ("opensubtitles", typeof(OpenSubtitlesDecomposer)),
        ("semlink", typeof(SemLinkDecomposer)),
        ("mapnet", typeof(MapNetDecomposer)),
        ("wordframenet", typeof(WordFrameNetDecomposer)),
        ("code", typeof(CodeDecomposer)),
        ("repo", typeof(RepoDecomposer)),
        ("tabular", typeof(TabularDecomposer)),
        ("parquet", typeof(ParquetDecomposer)),
        ("tiny-codes", typeof(TinyCodesDecomposer)),
        ("stack", typeof(StackDecomposer)),
        ("document", typeof(DocumentDecomposer)),
        ("rgba-image", typeof(RgbaImageDecomposer)),
        ("track-audio", typeof(TrackAudioDecomposer)),
        ("frame-video", typeof(FrameVideoDecomposer)),
        ("agents", typeof(AgentTrace.AgentTraceDecomposer)),
    ];

    public static IServiceCollection AddLaplaceSeedIngest(this IServiceCollection services)
    {
        services.AddSingleton<IContentRecordAdapter, TreeSitterTextAdapter>();
        services.AddSingleton<IContentRecordAdapter, SafetensorsContentAdapter>();
        services.AddSingleton<IContentRecordAdapter, RgbaContentAdapter>();
        services.AddSingleton<IContentRecordAdapter, WavContentAdapter>();

        foreach (var (_, decomposer) in Registry)
            services.AddTransient(decomposer);


        services.AddSingleton<SourceGenerationCatalog>();
        services.AddSingleton<ISeedDecomposerResolver, SeedDecomposerResolver>();
        return services;
    }
}

/// <summary>Edge resolver — keyed by ingest source name. No DI inside record loops.</summary>
public interface ISeedDecomposerResolver
{
    IDecomposer Resolve(string sourceKey);
    IReadOnlyCollection<string> SelectedGenerationKeys => Array.Empty<string>();
    bool TryResolveGeneration(string sourceKey, string? root, out IDecomposer decomposer, out string resolvedRoot)
    { decomposer = null!; resolvedRoot = ""; return false; }
    IDecomposer ResolveModel(string modelDir, bool? persistEvidence = null);
    IDecomposer ResolveRecipe(string recipePath);
    IContentRecordAdapter? FindAdapter(string path);
}

public sealed class SeedDecomposerResolver : ISeedDecomposerResolver
{
    private readonly IServiceProvider _sp;
    private readonly IEnumerable<IContentRecordAdapter> _adapters;
    private readonly SourceGenerationCatalog _generations;

    public SeedDecomposerResolver(IServiceProvider sp, IEnumerable<IContentRecordAdapter> adapters)
    {
        _sp = sp;
        _adapters = adapters;
        _generations = sp.GetService<SourceGenerationCatalog>() ?? new SourceGenerationCatalog();
    }

    public IReadOnlyCollection<string> SelectedGenerationKeys => _generations.Keys;

    public bool TryResolveGeneration(string sourceKey, string? root, out IDecomposer decomposer, out string resolvedRoot)
    {
        if (!_generations.TryGet(sourceKey, out var recipe))
        { decomposer = null!; resolvedRoot = ""; return false; }
        string? requested = string.IsNullOrWhiteSpace(root) ? null : root;
        string? generationRoot = string.IsNullOrWhiteSpace(recipe.Root) ? null : recipe.Root;
        string declared = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(generationRoot ?? Path.GetDirectoryName(recipe.ManifestPath)!));
        string? scope = null;
        resolvedRoot = declared;
        if (requested is not null)
        {
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
            // A path beneath the declared root selects part of the same generation; an
            // ancestor of an existing declared root resolves to that declared root; any
            // other path is another copy of the generation and becomes its root.
            if (path.StartsWith(declared + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                scope = path;
            else if (declared.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && Directory.Exists(declared))
                resolvedRoot = declared;
            else if (!string.Equals(path, declared, StringComparison.Ordinal))
                resolvedRoot = path;
        }
        decomposer = new Structured.Decomposer<SourceGenerationRecipe>(recipe, resolvedRoot, scope: scope);
        return true;
    }

    /// <summary>
    /// A selected generation reads exactly one root. A root that does not exist is a
    /// configuration error (wrong manifest root, data not fetched): fail now rather than
    /// admit an empty or partial generation, and never fall back to another provider.
    /// </summary>
    public static void RequireGenerationRoot(string sourceKey, string resolvedRoot)
    {
        if (!Directory.Exists(resolvedRoot))
            throw new DirectoryNotFoundException(
                $"Source generation '{sourceKey}' root '{resolvedRoot}' does not exist. Fetch the set "
                + "(Laplace-Operations data.sh / data.tsv) or pass the generation's root; the built-in "
                + "decomposer is not a fallback for a recipe-owned source.");
    }

    public IDecomposer Resolve(string sourceKey)
    {
        if (TryResolveGeneration(sourceKey, null, out var configured, out _)) return configured;
        foreach (var (key, decomposer) in SeedIngestComposition.Registry)
            if (string.Equals(key, sourceKey, StringComparison.OrdinalIgnoreCase))
                return (IDecomposer) _sp.GetRequiredService(decomposer);
        throw new ArgumentException($"No registered decomposer for source '{sourceKey}'", nameof(sourceKey));
    }

    public IDecomposer ResolveModel(string modelDir, bool? persistEvidence = null)
    {
        var resolved = SafetensorSnapshotWitness.ResolveCompleteDir(modelDir) ?? modelDir;
        return new ModelDecomposer(resolved, persistEvidence);
    }

    public IDecomposer ResolveRecipe(string recipePath) =>
        new RecipeDecomposer(recipePath);

    public IContentRecordAdapter? FindAdapter(string path) =>
        _adapters.FirstOrDefault(a => a.CanHandle(path));
}
