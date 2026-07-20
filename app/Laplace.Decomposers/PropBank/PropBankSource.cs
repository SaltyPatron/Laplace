using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.PropBank;

public readonly struct PropBankSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("PropBankDecomposer");

    public static string SourceName => "PropBankDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
    [
        "HAS_SENSE", "HAS_DEFINITION", "HAS_SEMANTIC_ROLE", "HAS_EXAMPLE",
        "CORRESPONDS_TO", "ROLE_CORRESPONDS_TO", "HAS_FEATURE",
    ];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["PropBank_Roleset", "VerbNet_Class", "FrameNet_Frame", "Ordinal"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
