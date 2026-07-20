using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Code;

public readonly struct RepoSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("RepoDecomposer");

    public static string SourceName => "RepoDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        ["CONTAINS", "CALLS", "DEFINES", "REFERENCES", "HAS_EXAMPLE", "HAS_DEFINITION"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["RepoRoot", "SourceFile"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
