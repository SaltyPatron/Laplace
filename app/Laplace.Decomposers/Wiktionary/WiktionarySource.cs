using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Wiktionary;

public readonly struct WiktionarySource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("WiktionaryDecomposer");

    public static string SourceName => "WiktionaryDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("AcademicCuratedWithUserInput");

    public static IReadOnlyList<string> Relations { get; } =
    [
        "glosses", "examples", "etymology_text",
        "hypernyms", "hyponyms", "holonyms", "synonyms", "antonyms",
        "related", "coordinate_terms", "meronyms", "translations",
        "derived", "forms", "tags", "ipa", "senses", "links", "wikidata",
    ];

    internal static readonly Hash128 SensesTypeId = RelationTypeRegistry.RelationTypeId("senses");
    internal static readonly Hash128 LinksTypeId = RelationTypeRegistry.RelationTypeId("links");
    internal static readonly Hash128 WikidataTypeId = RelationTypeRegistry.RelationTypeId("wikidata");

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["Wiktionary_Sense", "Wikidata_Item"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Wiktionary;
}
