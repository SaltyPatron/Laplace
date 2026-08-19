using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.SemLink;

public readonly struct SemLinkSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("SemLinkDecomposer");

    public static string SourceName => "SemLinkDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        ["CORRESPONDS_TO", "ROLE_CORRESPONDS_TO"];

    internal static readonly Hash128 RoleCorrespondsToTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[1]);

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        [
            "VerbNet_Class", "VerbNet_Role", "PropBank_Roleset", "PropBank_Role",
            "FrameNet_Frame", "FrameNet_FE",
        ];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}

/// <summary>Distinct witness registered beside SemLink during SemLink Initialize.</summary>
public readonly struct PredicateMatrixSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("PredicateMatrixDecomposer");

    public static string SourceName => "PredicateMatrixDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        [
            "CORRESPONDS_TO", "ROLE_CORRESPONDS_TO", "HAS_LANGUAGE", "HAS_POS",
            "HAS_DOMAIN_TOPIC", "HAS_LEX_CATEGORY", "HAS_BASE_CONCEPT_STATUS",
            "HAS_SENSE_FREQUENCY", "HAS_SYNSET_RELATION_COUNT",
        ];

    internal static readonly Hash128 CorrespondsToTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[0]);
    internal static readonly Hash128 RoleCorrespondsToTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[1]);
    internal static readonly Hash128 HasLanguageTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[2]);
    internal static readonly Hash128 HasPosTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[3]);
    internal static readonly Hash128 HasDomainTopicTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[4]);
    internal static readonly Hash128 HasLexCategoryTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[5]);
    internal static readonly Hash128 HasBaseConceptStatusTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[6]);
    internal static readonly Hash128 HasSenseFrequencyTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[7]);
    internal static readonly Hash128 HasSynsetRelationCountTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[8]);

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        [
            "VerbNet_Class", "VerbNet_Role", "PropBank_Roleset", "PropBank_Role",
            "FrameNet_Frame", "FrameNet_FE", "FrameNet_LU",
            "PredicateMatrix_Predicate", "PredicateMatrix_Role", "PredicateMatrix_Annotation_Value",
            "MCR_Domain", "MCR_SUMO", "MCR_Top_Ontology", "MCR_Lexname", "ESO_Class", "ESO_Role",
        ];

    public static SourceLicense License { get; } = new(
        "Creative Commons Attribution 3.0 Unported",
        Spdx: "CC-BY-3.0",
        Url: "http://adimen.si.ehu.es/web/PredicateMatrix",
        Citation: "López de Lacalle, Laparra, Aldabe, and Rigau (2016), A Multilingual Predicate Matrix",
        Version: "1.3");

    public static IngestSourceProfile Profile => IngestSourceProfile.PredicateMatrix;
}

public readonly struct MapNetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("MapNetDecomposer");

    public static string SourceName => "MapNetDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        ["CORRESPONDS_TO"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["FrameNet_Frame", "FrameNet_LU"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}

public readonly struct WordFrameNetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("WordFrameNetDecomposer");

    public static string SourceName => "WordFrameNetDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        ["CORRESPONDS_TO"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["FrameNet_LU"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
