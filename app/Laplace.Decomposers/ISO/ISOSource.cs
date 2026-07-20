using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.ISO;

public readonly struct ISOSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("ISO639Decomposer");

    public static string SourceName => "ISO639Decomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StandardsDerived");

    public static IReadOnlyList<string> Relations { get; } =
    [
        "IS_LANGUAGE_CODE", "HAS_ISO639_1_CODE", "USES_SCRIPT",
        "MEMBER_OF_MACROLANGUAGE", "HAS_ISO639_2_CODE", "HAS_LANGUAGE_SCOPE",
        "HAS_LANGUAGE_TYPE", "HAS_VARIANT_OF", "HAS_DEFINITION", "HAS_NAME_ALIAS",
    ];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["Language", "ISO639Code", "LanguageVariant"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
