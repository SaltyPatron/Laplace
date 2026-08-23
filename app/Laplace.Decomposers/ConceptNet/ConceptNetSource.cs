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
    // Spelled once each: the positive mapping and the Not* denial that refutes it must
    // never be able to drift onto different relations.
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
        ["HasA"] = "HAS_A",
        ["UsedFor"] = UsedFor,
        ["CapableOf"] = CapableOf,
        ["AtLocation"] = "AT_LOCATION",
        ["Causes"] = "CAUSES",
        ["HasSubevent"] = "HAS_SUBEVENT",
        ["HasFirstSubevent"] = "HAS_FIRST_SUBEVENT",
        ["HasLastSubevent"] = "HAS_LAST_SUBEVENT",
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
        ["MadeOf"] = "MADE_UP_OF",
        ["ReceivesAction"] = "RECEIVES_ACTION",
        ["InstanceOf"] = "IS_INSTANCE_OF",
        // A DENIAL IS AN OUTCOME, NOT A DIFFERENT RELATION.
        //
        // These four mapped to NOT_DESIRES / NOT_USED_FOR / NOT_CAPABLE_OF /
        // NOT_HAS_PROPERTY -- separate POSITIVE relation types. "a fish cannot walk" then
        // folded into a different consensus cell than "a fish can swim", so the denial
        // could never contest the assertion it denies: 29,547 rows of negative evidence
        // that adjudicated nothing.
        //
        // They now map onto the relation they deny, and NegatedRelations below flips the
        // sign of the source's own weight. laplace_score_fp(v, m) = 0.5*(1 + v/(m+|v|)),
        // so a negative magnitude scores below 0.5 and folds as a Refute whose strength is
        // ConceptNet's own confidence -- the sign channel the format always had and that
        // the full 34M-row file never once used (0 negative weights).
        ["NotDesires"] = Desires,
        ["NotUsedFor"] = UsedFor,
        ["NotCapableOf"] = CapableOf,
        ["NotHasProperty"] = HasProperty,
        ["Entails"] = "ENTAILS",
    };

    /// <summary>
    /// ConceptNet relations whose assertion is a DENIAL of the mapped relation. The
    /// magnitude is negated so the row folds as a Refute against the very cell the
    /// positive form asserts.
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
