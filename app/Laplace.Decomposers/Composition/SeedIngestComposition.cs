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
    /// <summary>
    /// The key -> decomposer binding, declared ONCE. A decomposer is a class, so
    /// this binding is the one part of the ingest roster that genuinely cannot come
    /// from the substrate — but it was written twice: as 24 AddTransient&lt;T&gt;()
    /// lines and again as a 24-arm switch mapping the same key to the same T. Adding
    /// a decomposer meant editing both, and editing only one produced either an
    /// unresolvable service at runtime or a key the resolver rejected.
    /// </summary>
    internal static readonly (string Key, Type Decomposer)[] Registry =
    [
        ("unicode", typeof(UnicodeDecomposer)),
        ("iso639", typeof(ISODecomposer)),
        ("atomic2020", typeof(Atomic2020Decomposer)),
        ("conceptnet", typeof(ConceptNetDecomposer)),
        ("wiktionary", typeof(WiktionaryDecomposer)),
        ("omw", typeof(OMWDecomposer)),
        ("wordnet", typeof(WordNetDecomposer)),
        ("ud", typeof(UDDecomposer)),
        ("tatoeba", typeof(TatoebaDecomposer)),
        ("framenet", typeof(FrameNetDecomposer)),
        ("opensubtitles", typeof(OpenSubtitlesDecomposer)),
        ("verbnet", typeof(VerbNetDecomposer)),
        ("propbank", typeof(PropBankDecomposer)),
        ("semlink", typeof(SemLinkDecomposer)),
        ("mapnet", typeof(MapNetDecomposer)),
        ("wordframenet", typeof(WordFrameNetDecomposer)),
        ("cili", typeof(CILIDecomposer)),
        ("code", typeof(CodeDecomposer)),
        ("repo", typeof(RepoDecomposer)),
        ("tabular", typeof(TabularDecomposer)),
        ("parquet", typeof(ParquetDecomposer)),
        ("tiny-codes", typeof(TinyCodesDecomposer)),
        ("stack", typeof(StackDecomposer)),
        ("document", typeof(DocumentDecomposer)),
    ];

    public static IServiceCollection AddLaplaceSeedIngest(this IServiceCollection services)
    {
        services.AddSingleton<IContentRecordAdapter, TreeSitterTextAdapter>();
        services.AddSingleton<IContentRecordAdapter, SafetensorsContentAdapter>();

        foreach (var (_, decomposer) in Registry)
            services.AddTransient(decomposer);


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

    public IDecomposer Resolve(string sourceKey)
    {
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

    public IDecomposer ResolveEtl(EtlSource src) =>
        new EtlDecomposer(src);

    public IContentRecordAdapter? FindAdapter(string path) =>
        _adapters.FirstOrDefault(a => a.CanHandle(path));
}
