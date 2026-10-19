using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Code;

public readonly struct RepoSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("RepoDecomposer");

    public static string SourceName => "RepoDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } = [];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["RepoRoot", "SourceFile"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
