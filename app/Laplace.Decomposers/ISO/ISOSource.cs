using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.ISO;

public readonly struct ISOSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("ISO639Decomposer");

    public static string SourceName => "ISO639Decomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("StandardsDerived");

    public static IReadOnlyList<string> Relations { get; } =
    [
        "IS_LANGUAGE_CODE", "USES_SCRIPT",
        "HAS_PART", "HAS_LANGUAGE_SCOPE",
        // HAS_DEFINITION dropped: ISO 639-3 publishes codes and names, no glosses. Both
        // emit sites were depositing the language's own NAME as its definition.
        "HAS_LANGUAGE_TYPE", "HAS_VARIANT_OF",
        // One relation per meaning: a language's ISO 639-1/-2B/-2T codes are
        // HAS_EXTERNAL_ID qualified identifier/iso639-*, and its reference and print
        // names are HAS_NAME qualified name/reference and name/print.
        "HAS_EXTERNAL_ID", "HAS_NAME",
        // SUPERSEDED_BY is the retirement lane's edge.
        "SUPERSEDED_BY",
    ];

    /// <summary>The one relation binding a language to each of its ISO 639 codes; the
    /// scheme (identifier/iso639-1, -2b, -2t, -3) is the claim's qualifier.</summary>
    public static string CodeRelation => Relations[^3];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["Language", "ISO639Code", "LanguageVariant"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Iso;
}
