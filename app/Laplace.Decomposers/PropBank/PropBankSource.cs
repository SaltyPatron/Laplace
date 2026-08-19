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

    internal static readonly Hash128 HasDefinitionTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[1]);
    internal static readonly Hash128 HasSemanticRoleTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[2]);
    internal static readonly Hash128 CorrespondsToTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[4]);
    internal static readonly Hash128 RoleCorrespondsToTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[5]);
    internal static readonly Hash128 HasFeatureTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[6]);

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        [
            "PropBank_Roleset", "PropBank_Role", "VerbNet_Class", "VerbNet_Role",
            "FrameNet_Frame", "FrameNet_FE", "Ordinal",
        ];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
