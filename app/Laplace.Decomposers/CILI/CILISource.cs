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

    /// <summary>
    /// An ILI's status in one release, from changes-in-wn31.csv.
    ///
    /// THIS IS NOT A REFUTE, AND IT CANNOT BE. laplace.consensus_id is
    /// blake3(subject, type, object) -- CONTEXT IS NOT IN THE CELL KEY. A deprecation is
    /// version-scoped ("ili:i115 is gone in wn31, it was 00023074-r in wn30"), so refuting
    /// `ili IS_TYPED_AS concept` would deny it flatly and contradict the wn30 testimony
    /// that shares that same cell. Version-scoped denial is not expressible as an outcome
    /// in this schema; forcing one would corrupt an unscoped claim.
    ///
    /// So it is recorded the way the substrate records provenance elsewhere: a meta-type,
    /// minted inline, never in relation_types.toml, never given a highway bit, never
    /// folded (FileEntity.MetadataRelationTypeId, LayerCompletion's HasLayerCompleted,
    /// ChessVocabulary.AnalysisVersionMetaTypeId). It still converts "absent from
    /// ili-map-wn31" -- which spec 05 says is UNKNOWN, not refutation -- into a stated
    /// fact that a reader can fetch.
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
