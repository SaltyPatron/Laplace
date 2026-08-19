using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Wiktionary;

public readonly struct WiktionarySource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("WiktionaryDecomposer");

    public static string SourceName => "WiktionaryDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCuratedWithUserInput");

    public static IReadOnlyList<string> Relations { get; } =
    [
        "HAS_POS", "HAS_DEFINITION", "HAS_EXAMPLE", "HAS_ETYMOLOGY",
        "HAS_HYPERNYM", "HAS_HYPONYM", "IS_PART_OF", "IS_SYNONYM_OF", "IS_ANTONYM_OF",
        "DERIVATIONALLY_RELATED", "RELATED_TO", "IS_COORDINATE_TERM_WITH",
        "HAS_USAGE_REGISTER", "HAS_PART", "HAS_VARIANT_OF", "TRANSCRIBES_AS",
        "IS_TRANSLATION_OF", "ETYMOLOGICALLY_DERIVED_FROM", "BORROWED_FROM",
        "INHERITED_FROM", "ETYMOLOGICALLY_RELATED_TO", "DERIVED_FROM",
        "FORM_OF", "HAS_FEATURE", "MANNER_OF",
        // Source-owned lexical and sense relations.
        "HAS_LANGUAGE", "CORRESPONDS_TO", "HAS_SENSE", "IS_SENSE_OF", "HAS_NAME_ALIAS",
    ];

    internal static readonly Hash128 CorrespondsToTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[26]);
    internal static readonly Hash128 HasLanguageTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[25]);
    internal static readonly Hash128 HasSenseTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[27]);
    internal static readonly Hash128 IsSenseOfTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[28]);
    internal static readonly Hash128 HasNameAliasTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[29]);

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["Wiktionary_Sense", "Wikidata_Item"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Wiktionary;
}
