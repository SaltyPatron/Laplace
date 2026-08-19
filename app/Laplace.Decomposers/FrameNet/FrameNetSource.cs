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
        "HAS_VALENCE_PATTERN", "HAS_DEFINITION", "HAS_NAME_ALIAS", "HAS_FEATURE",
        "HAS_POS", "HAS_EXAMPLE",
        "FRAME_USES", "PERSPECTIVE_ON", "INHERITS_FROM", "CAUSATIVE_OF",
        "INCHOATIVE_OF", "PRECEDES", "ALSO_SEE", "IS_A", "HAS_SUBEVENT", "RELATED_TO",
    ];

    internal static readonly Hash128 HasFrameElementTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[1]);
    internal static readonly Hash128 RequiresTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[2]);
    internal static readonly Hash128 ExcludesTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[3]);
    internal static readonly Hash128 HasDefinitionTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[5]);
    internal static readonly Hash128 HasNameAliasTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[6]);
    internal static readonly Hash128 HasFeatureTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[7]);

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["FrameNet_Frame", "FrameNet_FE", "FrameNet_LU", "FrameNet_Coreness"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.FrameNet;
}
