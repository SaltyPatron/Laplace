using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Atomic2020;

public readonly struct Atomic2020Source : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("Atomic2020Decomposer");

    public static string SourceName => "Atomic2020Decomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static readonly (string Rel, string Type)[] RelPairs =
    [
        ("oEffect", "O_EFFECT"), ("oReact", "O_REACT"), ("oWant", "O_WANT"),
        ("xAttr", "X_ATTR"), ("xEffect", "X_EFFECT"), ("xIntent", "X_INTENT"),
        ("xNeed", "X_NEED"), ("xReact", "X_REACT"), ("xWant", "X_WANT"), ("xReason", "X_REASON"),
        ("HinderedBy", "OBSTRUCTED_BY"), ("isAfter", "IS_AFTER"), ("isBefore", "IS_BEFORE"),
        ("isFilledBy", "X_FILLED_BY"), ("Causes", "CAUSES"), ("ObjectUse", "OBJECT_USE"),
        ("AtLocation", "AT_LOCATION"), ("HasSubEvent", "HAS_SUBEVENT"),
        ("CapableOf", "CAPABLE_OF"), ("Desires", "DESIRES"), ("HasProperty", "HAS_PROPERTY"),
        ("MadeUpOf", "MADE_UP_OF"), ("NotDesires", "NOT_DESIRES"),
    ];

    public static IReadOnlyList<string> Relations { get; } =
        RelPairs.Select(r => r.Type).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["Atomic_Marker", "Atomic_Split"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.RelationTriple;
}
