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

    // Spelled once: the assertion and the denial that refutes it share one relation.

    private const string Desires = "DESIRES";


    public static readonly (string Rel, string Type)[] RelPairs =
    [
        ("oEffect", "O_EFFECT"), ("oReact", "O_REACT"), ("oWant", "O_WANT"),
        ("xAttr", "X_ATTR"), ("xEffect", "X_EFFECT"), ("xIntent", "X_INTENT"),
        ("xNeed", "X_NEED"), ("xReact", "X_REACT"), ("xWant", "X_WANT"), ("xReason", "X_REASON"),
        ("HinderedBy", "OBSTRUCTED_BY"), ("isAfter", "IS_AFTER"), ("isBefore", "IS_BEFORE"),
        ("isFilledBy", "X_FILLED_BY"), ("Causes", "CAUSES"), ("ObjectUse", "OBJECT_USE"),
        ("AtLocation", "AT_LOCATION"), ("HasSubEvent", "HAS_SUBEVENT"),
        ("CapableOf", "CAPABLE_OF"), ("Desires", Desires), ("HasProperty", "HAS_PROPERTY"),
        // A denial is an outcome, not a different relation: NotDesires maps onto DESIRES
        // and the row folds with a negative magnitude, so it contests the very cell the
        // positive form asserts. As NOT_DESIRES it landed in a separate positive type
        // where it could never meet what it denies. Same fix as ConceptNet's four Not*
        // relations; see docs/evidence-flattening-2026-08-23.md.
        ("MadeUpOf", "MADE_UP_OF"), ("NotDesires", Desires),
    ];

    /// <summary>Relations whose assertion DENIES the relation they map to.</summary>
    public static readonly HashSet<string> NegatedRelations = new(StringComparer.Ordinal)
    {
        "NotDesires",
    };

    public static IReadOnlyList<string> Relations { get; } =
        RelPairs.Select(r => r.Type).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["Atomic_Marker", "Atomic_Split"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.RelationTriple;
}
