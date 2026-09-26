using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Unicode;

/// <summary>Compile-time Unicode UCD seed source (tier-0 codepoints + classifiers).</summary>
public readonly struct UnicodeSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("UnicodeDecomposer");

    public static string SourceName => "UnicodeDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("StandardsDerived");

    public static IReadOnlyList<string> Relations { get; } =
    [
        "HAS_SCRIPT", "HAS_CASE_MAPPING", "DECOMPOSES_TO",
        "HAS_MIRROR", "HAS_NAME",
        "CONFUSABLE_WITH", "DECODES_TO", "HAS_UTF8_ROLE",
        "HAS_NORMALIZATION_FORM", "HAS_CHARACTER_PROPERTY",
    ];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
    [
        "Codepoint", "UcdClassifier", "OrdinalContext",
        "Byte", "Utf8Role", "CharacterEncoding",
    ];

    public static SourceLicense License { get; } = new(
        Name: "Unicode Character Database",
        Spdx: "Unicode-DFS-2016",
        Url: "https://www.unicode.org/copyright.html",
        Copyright: "Unicode, Inc.",
        Citation: "Unicode Standard Version 17.0.0",
        Version: "17.0.0");

    public static IngestSourceProfile Profile => IngestSourceProfile.Unicode;
}
