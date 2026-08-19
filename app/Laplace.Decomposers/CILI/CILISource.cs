using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.CILI;

public readonly struct CILISource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("CILIDecomposer");

    public static string SourceName => "CILIDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        ["IS_TYPED_AS", "HAS_DEFINITION", "HAS_SYNSET_KEY"];

    internal static readonly Hash128 IsTypedAsTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[0]);
    internal static readonly Hash128 HasDefinitionTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[1]);
    internal static readonly Hash128 HasSynsetKeyTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[2]);

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
