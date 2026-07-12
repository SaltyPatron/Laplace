using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Unicode;

/// <summary>Compile-time Unicode UCD seed source (tier-0 codepoints + classifiers).</summary>
public readonly struct UnicodeSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/UnicodeDecomposer/v1");

    public static string SourceName => "UnicodeDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");

    public static IReadOnlyList<string> Relations { get; } =
    [
        "HAS_GENERAL_CATEGORY", "HAS_COMBINING_CLASS", "HAS_SCRIPT",
        "HAS_BLOCK", "HAS_UPPERCASE_MAPPING", "HAS_LOWERCASE_MAPPING",
        "CANONICAL_DECOMPOSES_TO", "HAS_TITLECASE_MAPPING",
        "COMPATIBILITY_DECOMPOSES_TO", "HAS_NUMERIC_VALUE", "HAS_BIDI_CLASS",
        "HAS_MIRROR", "HAS_AGE", "HAS_NAME", "HAS_LINE_BREAK",
        "HAS_EAST_ASIAN_WIDTH", "HAS_JOINING_TYPE", "HAS_NUMERIC_TYPE",
        "HAS_NAME_ALIAS", "CONFUSABLE_WITH", "HAS_EMOJI_PROPERTY",
        "DECODES_TO", "HAS_UTF8_ROLE",
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
