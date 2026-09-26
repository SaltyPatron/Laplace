using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;

/// <summary>Runtime recipe ingest — SourceId is content-addressed from recipe name.</summary>
public sealed class RecipeRuntimeManifest : ISourceManifest
{
    public RecipeRuntimeManifest(Hash128 sourceId, string sourceName)
    {
        SourceId = sourceId;
        SourceName = sourceName;
    }

    public Hash128 SourceId { get; }
    public string SourceName { get; }
    public Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("UserCuratedResource");

    public IReadOnlyList<string> Relations { get; } =
        ["HAS_ATTRIBUTE"];

    public IReadOnlyList<string>? TypeNodeNames { get; } =
        ["Model_Recipe", "Property_Value"];

    public SourceLicense License => SourceLicense.Unknown;
    public IngestSourceProfile Profile => IngestSourceProfile.Default;
}
