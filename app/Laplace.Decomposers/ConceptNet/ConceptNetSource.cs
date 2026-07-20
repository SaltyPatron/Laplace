using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.ConceptNet;

public readonly struct ConceptNetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("ConceptNetDecomposer");

    public static string SourceName => "ConceptNetDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("UserCuratedResource");

    /// <summary>ConceptNet /r/ name → substrate relation canonical.</summary>
    public static readonly Dictionary<string, string> RelMap = new(StringComparer.Ordinal)
    {
        ["RelatedTo"] = "RELATED_TO",
        ["FormOf"] = "FORM_OF",
        ["IsA"] = "IS_A",
        ["PartOf"] = "IS_PART_OF",
        ["HasA"] = "HAS_A",
        ["UsedFor"] = "USED_FOR",
        ["CapableOf"] = "CAPABLE_OF",
        ["AtLocation"] = "AT_LOCATION",
        ["Causes"] = "CAUSES",
        ["HasSubevent"] = "HAS_SUBEVENT",
        ["HasFirstSubevent"] = "HAS_FIRST_SUBEVENT",
        ["HasLastSubevent"] = "HAS_LAST_SUBEVENT",
        ["HasPrerequisite"] = "HAS_PREREQUISITE",
        ["HasProperty"] = "HAS_PROPERTY",
        ["MotivatedByGoal"] = "MOTIVATED_BY_GOAL",
        ["ObstructedBy"] = "OBSTRUCTED_BY",
        ["Desires"] = "DESIRES",
        ["CreatedBy"] = "CREATED_BY",
        ["Synonym"] = "IS_SYNONYM_OF",
        ["Antonym"] = "IS_ANTONYM_OF",
        ["DistinctFrom"] = "DISTINCT_FROM",
        ["DerivedFrom"] = "DERIVED_FROM",
        ["SymbolOf"] = "SYMBOL_OF",
        ["DefinedAs"] = "DEFINED_AS",
        ["MannerOf"] = "MANNER_OF",
        ["LocatedNear"] = "LOCATED_NEAR",
        ["HasContext"] = "HAS_CONTEXT",
        ["SimilarTo"] = "SIMILAR_TO",
        ["EtymologicallyRelatedTo"] = "ETYMOLOGICALLY_RELATED_TO",
        ["EtymologicallyDerivedFrom"] = "ETYMOLOGICALLY_DERIVED_FROM",
        ["CausesDesire"] = "CAUSES_DESIRE",
        ["MadeOf"] = "MADE_UP_OF",
        ["ReceivesAction"] = "RECEIVES_ACTION",
        ["InstanceOf"] = "IS_INSTANCE_OF",
        ["NotDesires"] = "NOT_DESIRES",
        ["NotUsedFor"] = "NOT_USED_FOR",
        ["NotCapableOf"] = "NOT_CAPABLE_OF",
        ["NotHasProperty"] = "NOT_HAS_PROPERTY",
        ["Entails"] = "ENTAILS",
    };

    public static IReadOnlyList<string> Relations { get; } = BuildRelations();

    public static IReadOnlyList<string>? TypeNodeNames => null;

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.RelationTriple;

    private static IReadOnlyList<string> BuildRelations()
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            "HAS_EXAMPLE", "HAS_LANGUAGE", "HAS_POS", "CORRESPONDS_TO",
        };
        foreach (var typeName in RelMap.Values)
            set.Add(typeName);
        return set.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}
