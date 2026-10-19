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

    /// <summary>The /r/ name is the predicate. NotDesires is not Desires with a minus sign.</summary>
    public static readonly Dictionary<string, string> RelMap = new(StringComparer.Ordinal)
    {
        ["RelatedTo"] = "RelatedTo",
        ["FormOf"] = "FormOf",
        ["IsA"] = "IsA",
        ["PartOf"] = "PartOf",
        ["HasA"] = "HasA",
        ["UsedFor"] = "UsedFor",
        ["CapableOf"] = "CapableOf",
        ["AtLocation"] = "AtLocation",
        ["Causes"] = "Causes",
        ["HasSubevent"] = "HasSubevent",
        ["HasFirstSubevent"] = "HasFirstSubevent",
        ["HasLastSubevent"] = "HasLastSubevent",
        ["HasPrerequisite"] = "HasPrerequisite",
        ["HasProperty"] = "HasProperty",
        ["MotivatedByGoal"] = "MotivatedByGoal",
        ["ObstructedBy"] = "ObstructedBy",
        ["Desires"] = "Desires",
        ["CreatedBy"] = "CreatedBy",
        ["Synonym"] = "Synonym",
        ["Antonym"] = "Antonym",
        ["DistinctFrom"] = "DistinctFrom",
        ["DerivedFrom"] = "DerivedFrom",
        ["SymbolOf"] = "SymbolOf",
        ["DefinedAs"] = "DefinedAs",
        ["MannerOf"] = "MannerOf",
        ["LocatedNear"] = "LocatedNear",
        ["HasContext"] = "HasContext",
        ["SimilarTo"] = "SimilarTo",
        ["EtymologicallyRelatedTo"] = "EtymologicallyRelatedTo",
        ["EtymologicallyDerivedFrom"] = "EtymologicallyDerivedFrom",
        ["CausesDesire"] = "CausesDesire",
        ["MadeOf"] = "MadeOf",
        ["ReceivesAction"] = "ReceivesAction",
        ["InstanceOf"] = "InstanceOf",
        ["NotDesires"] = "NotDesires",
        ["NotUsedFor"] = "NotUsedFor",
        ["NotCapableOf"] = "NotCapableOf",
        ["NotHasProperty"] = "NotHasProperty",
        ["Entails"] = "Entails",
    };

    public static readonly string[] DbpediaRelations =
    [
        "dbpedia/capital", "dbpedia/language", "dbpedia/genus", "dbpedia/genre",
        "dbpedia/occupation", "dbpedia/knownFor", "dbpedia/influencedBy", "dbpedia/field",
    ];

    /// <summary>Kept so a Not* row is not a second predicate and a negated weight.</summary>
    public static readonly HashSet<string> NegatedRelations = new(StringComparer.Ordinal);

    public static IReadOnlyList<string> Relations { get; } = BuildRelations();

    public static IReadOnlyList<string>? TypeNodeNames => null;

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.RelationTriple;

    private static IReadOnlyList<string> BuildRelations()
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            "HAS_POS", "Sense",
        };
        foreach (var typeName in RelMap.Values)
            set.Add(typeName);
        foreach (string name in DbpediaRelations)
            set.Add(name);
        return set.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}
