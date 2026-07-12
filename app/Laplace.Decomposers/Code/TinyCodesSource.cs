using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Code;

public readonly struct TinyCodesSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/TinyCodesDecomposer/v1");

    public static string SourceName => "TinyCodesDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_EXAMPLE", "HAS_DEFINITION", "CALLS", "DEFINES", "REFERENCES"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["CodeConcept"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
