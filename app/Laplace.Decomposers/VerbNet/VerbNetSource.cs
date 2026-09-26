using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.VerbNet;

public readonly struct VerbNetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("VerbNetDecomposer");

    public static string SourceName => "VerbNetDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
    [
        "IS_A", "IS_MEMBER_OF", "HAS_THEMATIC_ROLE", "HAS_SEMANTIC_ROLE",
        "HAS_VERB_FRAME", "HAS_EXAMPLE", "CORRESPONDS_TO", "EVOKES_FRAME", "HAS_NAME",
        "ENTAILS",
    ];

    internal static readonly Hash128 HasThematicRoleTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[2]);
    internal static readonly Hash128 HasSemanticRoleTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[3]);
    internal static readonly Hash128 HasNameTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[8]);
    // Which name a HAS_NAME claim states is its qualifier: a role's or predicate's label
    // as VerbNet defines it is its primary name; a member's lemma is one of its names.
    internal static readonly Mask256 PrimaryName =
        Laplace.SubstrateCRUD.ClaimQualifiers.Of("name", "primary");
    internal static readonly Mask256 AliasName =
        Laplace.SubstrateCRUD.ClaimQualifiers.Of("name", "alias");
    internal static readonly Hash128 CorrespondsToTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[6]);
    internal static readonly Hash128 EntailsTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[9]);

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["VerbNet_Class", "VerbNet_Member", "VerbNet_Role", "VerbNet_Predicate", "PropBank_Roleset"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
