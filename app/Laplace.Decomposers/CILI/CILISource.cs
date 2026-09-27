using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.CILI;

public readonly struct CILISource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("CILIDecomposer");

    public static string SourceName => "CILIDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        ["IS_TYPED_AS", "HAS_DEFINITION", "HAS_SYNSET_KEY"];

    internal static readonly Hash128 IsTypedAsTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[0]);
    internal static readonly Hash128 HasDefinitionTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[1]);
    internal static readonly Hash128 HasSynsetKeyTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[2]);

    /// <summary>
    /// An ILI's status in one release, from changes-in-wn31.csv.
    ///
    /// Not a refutation: a consensus cell is keyed by (subject, type, object) without
    /// context, and a deprecation is version-scoped ("gone in wn31, was 00023074-r in
    /// wn30"), so refuting <c>ili IS_TYPED_AS concept</c> would contradict the wn30
    /// testimony in the same cell. The status is recorded under an inline meta-type that
    /// is not in relation_types.toml, has no highway bit and is never folded, so absence
    /// from ili-map-wn31 becomes a stated, readable fact without becoming a verdict.
    /// </summary>
    internal static readonly Hash128 IliStatusMetaTypeId =
        SubstrateCanonicalIds.OfVersioned("type", "HasIliStatus");

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["WordNet_Synset", "CILI_Concept", "CILI_Instance", "Source_Reference", "Source_Version"];

    public static SourceLicense License { get; } = new(
        "Creative Commons Attribution 4.0 International",
        Spdx: "CC-BY-4.0",
        Url: "https://github.com/globalwordnet/cili",
        Copyright: "Copyright Francis Bond; attribution to the Global Wordnet Association",
        Citation: "Bond, Vossen, McCrae, and Fellbaum (2016), Collaborative Interlingual Index",
        Version: "2016 Initial release");

    public static IngestSourceProfile Profile => IngestSourceProfile.Cili;
}
