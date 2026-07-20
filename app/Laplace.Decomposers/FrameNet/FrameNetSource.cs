using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.FrameNet;

public readonly struct FrameNetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("FrameNetDecomposer");

    public static string SourceName => "FrameNetDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
    [
        "EVOKES_FRAME", "HAS_FRAME_ELEMENT", "REQUIRES", "EXCLUDES",
        "HAS_VALENCE_PATTERN", "HAS_DEFINITION", "HAS_POS", "HAS_EXAMPLE",
        "FRAME_USES", "PERSPECTIVE_ON", "INHERITS_FROM", "CAUSATIVE_OF",
        "INCHOATIVE_OF", "PRECEDES", "ALSO_SEE", "IS_A", "HAS_SUBEVENT", "RELATED_TO",
    ];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["FrameNet_Frame", "FrameNet_FE", "FrameNet_LU", "FrameNet_Coreness"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
