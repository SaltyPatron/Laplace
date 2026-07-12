using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Code;

public readonly struct StackSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/StackDecomposer/v1");

    public static string SourceName => "StackDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_EXAMPLE", "HAS_DEFINITION", "CALLS", "DEFINES", "REFERENCES"];

    public static IReadOnlyList<string>? TypeNodeNames => null;

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
