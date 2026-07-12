using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.WordNet;

public readonly struct WordNetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/WordNetDecomposer/v1");

    public static string SourceName => "WordNetDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");

    public static readonly Dictionary<string, string> PointerTypes = new()
    {
        ["!"] = "IS_ANTONYM_OF",
        ["@"] = "HAS_HYPERNYM",
        ["@i"] = "IS_INSTANCE_OF",
        ["~"] = "HAS_HYPONYM",
        ["~i"] = "HAS_INSTANCE",
        ["#m"] = "IS_MEMBER_OF",
        ["#s"] = "IS_SUBSTANCE_OF",
        ["#p"] = "IS_PART_OF",
        ["%m"] = "HAS_MEMBER",
        ["%s"] = "HAS_SUBSTANCE",
        ["%p"] = "HAS_PART",
        ["="] = "HAS_ATTRIBUTE",
        ["+"] = "DERIVATIONALLY_RELATED",
        [";c"] = "HAS_DOMAIN_TOPIC",
        ["-c"] = "IS_DOMAIN_TOPIC_MEMBER",
        [";r"] = "HAS_DOMAIN_REGION",
        ["-r"] = "IS_DOMAIN_REGION_MEMBER",
        [";u"] = "HAS_DOMAIN_USAGE",
        ["-u"] = "IS_DOMAIN_USAGE_MEMBER",
        ["*"] = "ENTAILS",
        [">"] = "CAUSES",
        ["^"] = "ALSO_SEE",
        ["$"] = "IN_VERB_GROUP_WITH",
        ["&"] = "IS_SIMILAR_TO",
        ["<"] = "IS_PARTICIPLE_OF",
        ["\\"] = "PERTAINS_TO",
    };

    public static IReadOnlyList<string> Relations { get; } = BuildRelations();

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["WordNet_Synset", "WordNet_Sense"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.WordNet;

    private static IReadOnlyList<string> BuildRelations()
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            "IS_SYNONYM_OF", "HAS_POS", "HAS_DEFINITION", "HAS_EXAMPLE", "HAS_LEX_CATEGORY",
            "HAS_DOMAIN_TOPIC", "HAS_VERB_FRAME", "IS_LEMMA_OF", "HAS_SENSE", "IS_SENSE_OF",
            "HAS_NAME_ALIAS", "MANNER_OF",
        };
        foreach (var name in PointerTypes.Values)
            set.Add(name);
        return set.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}
