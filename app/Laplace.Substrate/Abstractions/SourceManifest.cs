using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Instance form of a source descriptor, read by the sealed InitializeAsync bootstrap.
/// Runtime-configured sources build it from their config instead of static abstracts.
/// </summary>
public interface ISourceManifest
{
    Hash128 SourceId { get; }
    string SourceName { get; }
    Hash128 TrustClass { get; }
    IReadOnlyList<string> Relations { get; }
    SourceLicense License { get; }
    IngestSourceProfile Profile { get; }
    IReadOnlyList<string>? TypeNodeNames => null;
}

/// <summary>
/// Compile-time source descriptor. Static abstracts monomorphize into the sealed
/// Initialize path; instance forwarding goes through <see cref="SeedSourceManifest{TSource}"/>.
/// </summary>
public interface ISeedSource
{
    static abstract Hash128 SourceId { get; }
    static abstract string SourceName { get; }
    static abstract Hash128 TrustClass { get; }
    static abstract IReadOnlyList<string> Relations { get; }
    static abstract SourceLicense License { get; }
    static abstract IngestSourceProfile Profile { get; }
    static abstract IReadOnlyList<string>? TypeNodeNames { get; }
}

/// <summary>Forwards <see cref="ISeedSource"/> statics into an <see cref="ISourceManifest"/> instance.</summary>
public sealed class SeedSourceManifest<TSource> : ISourceManifest
    where TSource : ISeedSource
{
    public static SeedSourceManifest<TSource> Instance { get; } = new();

    public Hash128 SourceId => TSource.SourceId;
    public string SourceName => TSource.SourceName;
    public Hash128 TrustClass => TSource.TrustClass;
    public IReadOnlyList<string> Relations => TSource.Relations;
    public SourceLicense License => TSource.License;
    public IngestSourceProfile Profile => TSource.Profile;
    public IReadOnlyList<string>? TypeNodeNames => TSource.TypeNodeNames;
}
