using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.OMW;

public readonly struct OMWSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("OMWDecomposer");

    public static string SourceName => "OMWDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_DEFINITION", "HAS_EXAMPLE", "IS_SYNONYM_OF", "HAS_LANGUAGE", "HAS_POS"];

    public static IReadOnlyList<string>? TypeNodeNames => null;

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
