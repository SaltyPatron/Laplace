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
        ["CORRESPONDS_TO", "ROLE_CORRESPONDS_TO"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        [
            "VerbNet_Class", "VerbNet_Role", "PropBank_Roleset", "PropBank_Role",
            "FrameNet_Frame", "FrameNet_FE",
        ];

    public static SourceLicense License { get; } = new(
        "Creative Commons Attribution 3.0 Unported",
        Spdx: "CC-BY-3.0",
        Url: "http://adimen.si.ehu.es/web/PredicateMatrix",
        Citation: "López de Lacalle, Laparra, Aldabe, and Rigau (2016), A Multilingual Predicate Matrix",
        Version: "1.3");

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
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
