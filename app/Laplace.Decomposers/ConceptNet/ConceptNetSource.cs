using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.ConceptNet;

public readonly struct ConceptNetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("ConceptNetDecomposer");

    public static string SourceName => "ConceptNetDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("UserCuratedResource");

    /// <summary>ConceptNet /r/ name → substrate relation canonical.</summary>
    // Spelled once each, so a positive mapping and the Not* denial that refutes it cannot
    // drift onto different relations.
    private const string Desires = "DESIRES";
    private const string UsedFor = "USED_FOR";
    private const string CapableOf = "CAPABLE_OF";
    private const string HasProperty = "HAS_PROPERTY";

    public static readonly Dictionary<string, string> RelMap = new(StringComparer.Ordinal)
    {
        ["RelatedTo"] = "RELATED_TO",
        ["FormOf"] = "FORM_OF",
        ["IsA"] = "IS_A",
        ["PartOf"] = "IS_PART_OF",
        ["HasA"] = "HasA",
        ["UsedFor"] = UsedFor,
        ["CapableOf"] = CapableOf,
        ["AtLocation"] = "AT_LOCATION",
        ["Causes"] = "CAUSES",
        ["HasSubevent"] = "HAS_SUBEVENT",
        ["HasFirstSubevent"] = "HasFirstSubevent",
        ["HasLastSubevent"] = "HasLastSubevent",
        ["HasPrerequisite"] = "HAS_PREREQUISITE",
        ["HasProperty"] = HasProperty,
        ["MotivatedByGoal"] = "MOTIVATED_BY_GOAL",
        ["ObstructedBy"] = "OBSTRUCTED_BY",
        ["Desires"] = Desires,
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
        ["MadeOf"] = "MadeOf",
        ["ReceivesAction"] = "RECEIVES_ACTION",
        ["InstanceOf"] = "IS_INSTANCE_OF",
        // A denial is an outcome, not a different relation: each Not* maps onto the relation
        // it denies and NegatedRelations flips the sign of ConceptNet's weight.
        // laplace_score_fp(v, m) = 0.5*(1 + v/(m+|v|)), so a negative magnitude scores below
        // 0.5 and folds as a refutation into the same cell the positive form confirms.
        ["NotDesires"] = Desires,
        ["NotUsedFor"] = UsedFor,
        ["NotCapableOf"] = CapableOf,
        ["NotHasProperty"] = HasProperty,
        ["Entails"] = "ENTAILS",
    };

    /// <summary>
    /// ConceptNet relations whose assertion denies the mapped relation. The magnitude is
    /// negated so the row folds as a refutation into the cell the positive form confirms.
    /// </summary>
    public static readonly HashSet<string> NegatedRelations = new(StringComparer.Ordinal)
    {
        "NotDesires", "NotUsedFor", "NotCapableOf", "NotHasProperty",
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
