using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Code;

public readonly struct TinyCodesSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("TinyCodesDecomposer");

    public static string SourceName => "TinyCodesDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_EXAMPLE", "HAS_DEFINITION", "CALLS", "DEFINES", "REFERENCES"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["CodeConcept"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
