using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Code;

public readonly struct RepoSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/RepoDecomposer/v1");

    public static string SourceName => "RepoDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    public static IReadOnlyList<string> Relations { get; } =
        ["CONTAINS", "CALLS", "DEFINES", "REFERENCES", "HAS_EXAMPLE", "HAS_DEFINITION"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["RepoRoot", "SourceFile"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
