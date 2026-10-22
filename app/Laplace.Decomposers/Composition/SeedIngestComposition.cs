using Laplace.Engine.Core;
using Laplace.Decomposers.Code;
using Laplace.Decomposers.Media;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.Operational;
using Laplace.Decomposers.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Laplace.Decomposers.Composition;

/// <summary>
/// Seed-ingest composition root shared by CLI and API. Decomposers and content adapters
/// are resolved at the host edge, never inside per-record handlers.
/// </summary>
public static class SeedIngestComposition
{
    public static IReadOnlyList<string> RegisteredSourceKeys => Registry.Select(entry => entry.Key).ToArray();
    // Canonical corpora run as Native jobs, not managed per-record providers.
    // This registry contains only the remaining explicit operator/format lanes.
    internal static readonly (string Key, Type Decomposer)[] Registry =
    [
        ("operational", typeof(OperationalDecomposer)),
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


        services.AddSingleton<ISeedDecomposerResolver, SeedDecomposerResolver>();
        return services;
    }
}

/// <summary>Edge resolver — keyed by ingest source name. No DI inside record loops.</summary>
public interface ISeedDecomposerResolver
{
    IDecomposer Resolve(string sourceKey);
    IReadOnlyCollection<string> SelectedGenerationKeys { get; }
    IDecomposer ResolveModel(string modelDir, bool? persistEvidence = null);
    IDecomposer ResolveRecipe(string recipePath);
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

    public IReadOnlyCollection<string> SelectedGenerationKeys => EtlManifest.SourceOrder.ToArray();

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

    public IContentRecordAdapter? FindAdapter(string path) =>
        _adapters.FirstOrDefault(a => a.CanHandle(path));
}
