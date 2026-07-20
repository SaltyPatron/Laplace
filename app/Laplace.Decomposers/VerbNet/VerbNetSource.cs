using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.VerbNet;

public readonly struct VerbNetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("VerbNetDecomposer");

    public static string SourceName => "VerbNetDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
    [
        "IS_A", "MEMBER_OF_VERBNET_CLASS", "HAS_THEMATIC_ROLE",
        "HAS_VERB_FRAME", "HAS_EXAMPLE", "CORRESPONDS_TO", "EVOKES_FRAME", "HAS_NAME_ALIAS",
    ];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["VerbNet_Class"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
