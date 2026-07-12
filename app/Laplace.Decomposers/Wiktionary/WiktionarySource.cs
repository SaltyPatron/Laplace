using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Wiktionary;

public readonly struct WiktionarySource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/WiktionaryDecomposer/v1");

    public static string SourceName => "WiktionaryDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/AcademicCuratedWithUserInput/v1");

    public static IReadOnlyList<string> Relations { get; } =
    [
        "HAS_POS", "HAS_DEFINITION", "HAS_EXAMPLE", "HAS_ETYMOLOGY",
        "HAS_HYPERNYM", "HAS_HYPONYM", "IS_PART_OF", "IS_SYNONYM_OF", "IS_ANTONYM_OF",
        "DERIVATIONALLY_RELATED", "RELATED_TO", "IS_COORDINATE_TERM_WITH",
        "HAS_USAGE_REGISTER", "HAS_PART", "HAS_VARIANT_OF", "TRANSCRIBES_AS",
        "IS_TRANSLATION_OF", "ETYMOLOGICALLY_DERIVED_FROM", "BORROWED_FROM",
        "INHERITED_FROM", "ETYMOLOGICALLY_RELATED_TO", "DERIVED_FROM",
        "FORM_OF", "HAS_FEATURE", "MANNER_OF",
    ];

    public static IReadOnlyList<string>? TypeNodeNames => null;

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Wiktionary;
}
