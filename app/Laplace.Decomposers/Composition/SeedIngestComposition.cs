using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.CILI;
using Laplace.Decomposers.Code;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.FrameNet;
using Laplace.Decomposers.ISO;
using Laplace.Decomposers.Model;
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
using Laplace.Decomposers.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Laplace.Decomposers.Composition;

/// <summary>
/// Shared seed-ingest composition root for CLI + API. Resolve decomposers and
/// content adapters at the host edge only — never inside per-record handlers.
/// </summary>
public static class SeedIngestComposition
{
    public static IServiceCollection AddLaplaceSeedIngest(this IServiceCollection services)
    {
        services.AddSingleton<IContentRecordAdapter, TreeSitterTextAdapter>();
        services.AddSingleton<IContentRecordAdapter, SafetensorsContentAdapter>();

        services.AddTransient<UnicodeDecomposer>();
        services.AddTransient<ISODecomposer>();
        services.AddTransient<Atomic2020Decomposer>();
        services.AddTransient<ConceptNetDecomposer>();
        services.AddTransient<WiktionaryDecomposer>();
        services.AddTransient<OMWDecomposer>();
        services.AddTransient<WordNetDecomposer>();
        services.AddTransient<UDDecomposer>();
        services.AddTransient<TatoebaDecomposer>();
        services.AddTransient<FrameNetDecomposer>();
        services.AddTransient<OpenSubtitlesDecomposer>();
        services.AddTransient<VerbNetDecomposer>();
        services.AddTransient<PropBankDecomposer>();
        services.AddTransient<SemLinkDecomposer>();
        services.AddTransient<MapNetDecomposer>();
        services.AddTransient<WordFrameNetDecomposer>();
        services.AddTransient<CILIDecomposer>();
        services.AddTransient<CodeDecomposer>();
        services.AddTransient<RepoDecomposer>();
        services.AddTransient<TabularDecomposer>();
        services.AddTransient<TinyCodesDecomposer>();
        services.AddTransient<StackDecomposer>();
        services.AddTransient<DocumentDecomposer>();

        services.AddSingleton<ISeedDecomposerResolver, SeedDecomposerResolver>();
        return services;
    }
}

/// <summary>Edge resolver — keyed by ingest source name. No DI inside record loops.</summary>
public interface ISeedDecomposerResolver
{
    IDecomposer Resolve(string sourceKey);
    IDecomposer ResolveModel(string modelDir, bool? persistEvidence = null);
    IDecomposer ResolveRecipe(string recipePath);
    IDecomposer ResolveEtl(EtlSource src);
    IContentRecordAdapter? FindAdapter(string path);
}

public sealed class SeedDecomposerResolver : ISeedDecomposerResolver
{
    private readonly IServiceProvider _sp;
    private readonly IEnumerable<IContentRecordAdapter> _adapters;

    public SeedDecomposerResolver(IServiceProvider sp, IEnumerable<IContentRecordAdapter> adapters)
    {
        _sp = sp;
        _adapters = adapters;
    }

    public IDecomposer Resolve(string sourceKey) => sourceKey.ToLowerInvariant() switch
    {
        "unicode" => _sp.GetRequiredService<UnicodeDecomposer>(),
        "iso639" => _sp.GetRequiredService<ISODecomposer>(),
        "atomic2020" => _sp.GetRequiredService<Atomic2020Decomposer>(),
        "conceptnet" => _sp.GetRequiredService<ConceptNetDecomposer>(),
        "wiktionary" => _sp.GetRequiredService<WiktionaryDecomposer>(),
        "omw" => _sp.GetRequiredService<OMWDecomposer>(),
        "wordnet" => _sp.GetRequiredService<WordNetDecomposer>(),
        "ud" => _sp.GetRequiredService<UDDecomposer>(),
        "tatoeba" => _sp.GetRequiredService<TatoebaDecomposer>(),
        "framenet" => _sp.GetRequiredService<FrameNetDecomposer>(),
        "opensubtitles" => _sp.GetRequiredService<OpenSubtitlesDecomposer>(),
        "verbnet" => _sp.GetRequiredService<VerbNetDecomposer>(),
        "propbank" => _sp.GetRequiredService<PropBankDecomposer>(),
        "semlink" => _sp.GetRequiredService<SemLinkDecomposer>(),
        "mapnet" => _sp.GetRequiredService<MapNetDecomposer>(),
        "wordframenet" => _sp.GetRequiredService<WordFrameNetDecomposer>(),
        "cili" => _sp.GetRequiredService<CILIDecomposer>(),
        "code" => _sp.GetRequiredService<CodeDecomposer>(),
        "repo" => _sp.GetRequiredService<RepoDecomposer>(),
        "tabular" => _sp.GetRequiredService<TabularDecomposer>(),
        "tiny-codes" => _sp.GetRequiredService<TinyCodesDecomposer>(),
        "stack" => _sp.GetRequiredService<StackDecomposer>(),
        "document" => _sp.GetRequiredService<DocumentDecomposer>(),
        _ => throw new ArgumentException($"No registered decomposer for source '{sourceKey}'", nameof(sourceKey)),
    };

    public IDecomposer ResolveModel(string modelDir, bool? persistEvidence = null) =>
        new ModelDecomposer(modelDir, persistEvidence);

    public IDecomposer ResolveRecipe(string recipePath) =>
        new RecipeDecomposer(recipePath);

    public IDecomposer ResolveEtl(EtlSource src) =>
        new EtlDecomposer(src);

    public IContentRecordAdapter? FindAdapter(string path) =>
        _adapters.FirstOrDefault(a => a.CanHandle(path));
}
