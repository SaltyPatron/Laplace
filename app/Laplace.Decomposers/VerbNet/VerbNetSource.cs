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
        "IS_A", "MEMBER_OF_VERBNET_CLASS", "HAS_THEMATIC_ROLE", "HAS_SEMANTIC_ROLE",
        "HAS_VERB_FRAME", "HAS_EXAMPLE", "CORRESPONDS_TO", "EVOKES_FRAME", "HAS_NAME_ALIAS",
    ];

    internal static readonly Hash128 HasThematicRoleTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[2]);
    internal static readonly Hash128 HasSemanticRoleTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[3]);
    internal static readonly Hash128 HasNameAliasTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[8]);

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["VerbNet_Class", "VerbNet_Role"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
