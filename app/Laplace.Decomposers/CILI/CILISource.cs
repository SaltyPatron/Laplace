using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.CILI;

public readonly struct CILISource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("CILIDecomposer");

    public static string SourceName => "CILIDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        ["IS_TYPED_AS", "HAS_DEFINITION", "HAS_NAME_ALIAS", "HAS_SYNSET_KEY"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["WordNet_Synset"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
