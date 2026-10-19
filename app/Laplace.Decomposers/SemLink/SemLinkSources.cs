using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.SemLink;

public readonly struct SemLinkSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("SemLinkDecomposer");

    public static string SourceName => "SemLinkDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        [
            "pb-vn2", "vn-fn2", "pb-wn", "vn-wn", "fn-wn", "external_vn2pb",
            "vn-class", "frame", "roleset", "ontonotes-sense", "semlink-2",
            "vn-fn-role-mapping",
        ];

    internal static readonly Hash128 VnFnRoleTypeId = RelationTypeRegistry.RelationTypeId("vn-fn-role-mapping");
    internal static readonly Hash128 VnClassTypeId = RelationTypeRegistry.RelationTypeId("vn-class");
    internal static readonly Hash128 FrameTypeId = RelationTypeRegistry.RelationTypeId("frame");
    internal static readonly Hash128 RolesetTypeId = RelationTypeRegistry.RelationTypeId("roleset");
    internal static readonly Hash128 OntoNotesSenseTypeId = RelationTypeRegistry.RelationTypeId("ontonotes-sense");
    internal static readonly Hash128 InstancePairTypeId = RelationTypeRegistry.RelationTypeId("semlink-2");

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        [
            "VerbNet_Class", "VerbNet_Role", "PropBank_Roleset", "PropBank_Role",
            "FrameNet_Frame", "FrameNet_FE",
        ];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}

/// <summary>PredicateMatrix source identity, a distinct witness registered when SemLink initializes.</summary>
public readonly struct PredicateMatrixSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("PredicateMatrixDecomposer");

    public static string SourceName => "PredicateMatrixDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        [
            "4_VN_CLASS", "9_VN_ROLE", "10_WN_SENSE", "11_MCR_iliOffset",
            "12_FN_FRAME", "13_FN_LE", "14_FN_FRAME_ELEMENT", "15_PB_ROLESET", "16_PB_ARG",
            "18_MCR_BC", "19_MCR_DOMAIN", "20_MCR_SUMO", "21_MCR_TO", "22_MCR_LEXNAME",
            "23_MCR_BLC", "24_WN_SENSEFREC", "25_WN_SYNSET_REL_NUM",
            "26_ESO_CLASS", "27_ESO_ROLE",
        ];

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
        TrustClassRegistry.Id("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        ["mapping_frame_synsets", "mapping_lus_synsets"];

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
        TrustClassRegistry.Id("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        ["lu_synset", "WordFrameNet", "wordframenet", "WFN", "XWFN", "eXtendedWFN"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["FrameNet_LU"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
