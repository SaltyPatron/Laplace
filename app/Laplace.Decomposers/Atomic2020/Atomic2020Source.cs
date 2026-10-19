using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Atomic2020;

public readonly struct Atomic2020Source : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("Atomic2020Decomposer");

    public static string SourceName => "Atomic2020Decomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("StructuredCorpus");

    // The TSV column is the predicate. NotDesires stays NotDesires.
    public static readonly (string Rel, string Type)[] RelPairs =
    [
        ("oEffect", "oEffect"), ("oReact", "oReact"), ("oWant", "oWant"),
        ("xAttr", "xAttr"), ("xEffect", "xEffect"), ("xIntent", "xIntent"),
        ("xNeed", "xNeed"), ("xReact", "xReact"), ("xWant", "xWant"), ("xReason", "xReason"),
        ("HinderedBy", "HinderedBy"), ("isAfter", "isAfter"), ("isBefore", "isBefore"),
        ("isFilledBy", "isFilledBy"), ("Causes", "Causes"), ("ObjectUse", "ObjectUse"),
        ("AtLocation", "AtLocation"), ("HasSubEvent", "HasSubEvent"),
        ("CapableOf", "CapableOf"), ("Desires", "Desires"), ("HasProperty", "HasProperty"),
        ("MadeUpOf", "MadeUpOf"), ("NotDesires", "NotDesires"),
    ];

    public static readonly HashSet<string> NegatedRelations = new(StringComparer.Ordinal);

    public static IReadOnlyList<string> Relations { get; } =
        RelPairs.Select(r => r.Type).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["Atomic_Marker", "Atomic_Split"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.RelationTriple;
}
