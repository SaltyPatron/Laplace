using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.SemLink;

public readonly struct SemLinkSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/SemLinkDecomposer/v1");

    public static string SourceName => "SemLinkDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public static IReadOnlyList<string> Relations { get; } =
        ["CORRESPONDS_TO", "ROLE_CORRESPONDS_TO"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["VerbNet_Class", "PropBank_Roleset", "FrameNet_Frame"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}

/// <summary>Distinct witness registered beside SemLink during SemLink Initialize.</summary>
public readonly struct PredicateMatrixSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/PredicateMatrixDecomposer/v1");

    public static string SourceName => "PredicateMatrixDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public static IReadOnlyList<string> Relations { get; } =
        ["CORRESPONDS_TO", "ROLE_CORRESPONDS_TO"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["VerbNet_Class", "PropBank_Roleset", "FrameNet_Frame", "FrameNet_FE"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}

public readonly struct MapNetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/MapNetDecomposer/v1");

    public static string SourceName => "MapNetDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

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
        Hash128.OfCanonical("substrate/source/WordFrameNetDecomposer/v1");

    public static string SourceName => "WordFrameNetDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public static IReadOnlyList<string> Relations { get; } =
        ["CORRESPONDS_TO"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["FrameNet_LU"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
