using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The semantic-role systems whose labels are scoped by a parent predicate/frame.
/// Values are persisted in the structural trajectory; append only and never renumber.
/// </summary>
public enum RoleIdentityKind : ushort
{
    PropBank = 1,
    VerbNet = 2,
    FrameNet = 3,
    PredicateMatrix = 4,
    Eso = 5,
}

/// <summary>
/// Identity for a role slot under its proposition-defining parent. The human-readable
/// label is canonical content; the role itself is the witnessed structure
/// [role-schema, role-system, parent, label]. It therefore owns an exact trajectory and
/// typed physicality instead of living in a separate domain-hashed identity universe.
/// </summary>
public static class RoleAnchor
{
    private const string SchemaText = "semantic-role/structure/v2";

    public static Hash128? Id(RoleIdentityKind kind, Hash128 parentId, string? rawRoleKey)
    {
        Validate(kind, parentId);
        string? key = Normalize(rawRoleKey);
        if (key is null) return null;
        Hash128? label = ContentEmitter.RootId(key);
        if (label is null) return null;
        Span<Hash128> constituents = stackalloc Hash128[4]
        {
            RequiredRoot(SchemaText), RequiredRoot(KindMarkerText(kind)), parentId, label.Value
        };
        return Hash128.Merkle(EntityTier.Word, constituents);
    }

    public static Hash128? Declare(
        SubstrateChangeBuilder builder,
        RoleIdentityKind kind,
        Hash128 parentId,
        string? roleKey,
        Hash128 entityTypeId,
        Hash128 source) =>
        DeclareComponent(builder, kind, parentId, roleKey, entityTypeId, source)?.Id;

    /// <summary>The role as a composition part: identity plus realized coordinate.</summary>
    public static OrderedCompositionComponent? DeclareComponent(
        SubstrateChangeBuilder builder,
        RoleIdentityKind kind,
        Hash128 parentId,
        string? roleKey,
        Hash128 entityTypeId,
        Hash128 source)
    {
        Validate(kind, parentId);
        string? key = Normalize(roleKey);
        if (key is null) return null;
        OrderedCompositionComponent schema =
            RequiredComponent(builder, SchemaText, source);
        OrderedCompositionComponent system =
            RequiredComponent(builder, KindMarkerText(kind), source);
        OrderedCompositionComponent? label =
            ContentEmitter.StageComponent(builder, key, source);
        if (label is not { } component) return null;

        Span<Hash128> constituents = stackalloc Hash128[4]
        {
            schema.Id, system.Id, parentId, component.Id
        };
        Hash128 id = Hash128.Merkle(EntityTier.Word, constituents);
        builder.AddEntity(id, EntityTier.Word, entityTypeId);

        double[] coord = Math4d.KarcherMean(
        [
            schema.CoordX, schema.CoordY, schema.CoordZ, schema.CoordM,
            system.CoordX, system.CoordY, system.CoordZ, system.CoordM,
            component.CoordX, component.CoordY, component.CoordZ, component.CoordM,
        ]);
        builder.AddPhysicality(new PhysicalityRow(
            PhysicalityId.Compute(id, PhysicalityType.ParseStructure),
            id, source, PhysicalityType.ParseStructure,
            coord[0], coord[1], coord[2], coord[3], Hilbert128.Encode(coord),
            Trajectory.Build(constituents), constituents.Length,
            null, null, 0));
        return new OrderedCompositionComponent(id, EntityTier.Word, coord[0], coord[1], coord[2], coord[3]);
    }

    public static Hash128? Emit(
        SubstrateChangeBuilder builder,
        RoleIdentityKind kind,
        Hash128 parentId,
        string? roleKey,
        Hash128 entityTypeId,
        Hash128 source,
        double trust)
    {
        Hash128? id = Declare(builder, kind, parentId, roleKey, entityTypeId, source);
        if (id is null) return null;
        return id;
    }

    public static RoleIdentityKind KindForParentType(Hash128 parentTypeId)
    {
        if (parentTypeId == EntityTypeRegistry.PropBankRoleset) return RoleIdentityKind.PropBank;
        if (parentTypeId == EntityTypeRegistry.VerbNetClass) return RoleIdentityKind.VerbNet;
        if (parentTypeId == EntityTypeRegistry.FrameNetFrame) return RoleIdentityKind.FrameNet;
        if (parentTypeId == EntityTypeRegistry.PredicateMatrixPredicate) return RoleIdentityKind.PredicateMatrix;
        if (parentTypeId == EntityTypeRegistry.EsoClass) return RoleIdentityKind.Eso;
        throw new ArgumentOutOfRangeException(
            nameof(parentTypeId), parentTypeId, "entity type does not own semantic roles");
    }

    public static Hash128 EntityTypeFor(RoleIdentityKind kind) => kind switch
    {
        RoleIdentityKind.PropBank => EntityTypeRegistry.PropBankRole,
        RoleIdentityKind.VerbNet => EntityTypeRegistry.VerbNetRole,
        RoleIdentityKind.FrameNet => EntityTypeRegistry.FrameNetFe,
        RoleIdentityKind.PredicateMatrix => EntityTypeRegistry.PredicateMatrixRole,
        RoleIdentityKind.Eso => EntityTypeRegistry.EsoRole,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown role identity domain"),
    };

    private static void Validate(RoleIdentityKind kind, Hash128 parentId)
    {
        if (parentId == default)
            throw new ArgumentException("role parent must not be empty", nameof(parentId));
        if (kind is < RoleIdentityKind.PropBank or > RoleIdentityKind.Eso)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown role identity domain");
    }

    private static string KindMarkerText(RoleIdentityKind kind) =>
        $"semantic-role/system/{(ushort)kind}/v1";

    private static Hash128 RequiredRoot(string value) =>
        ContentEmitter.RootId(value)
        ?? throw new InvalidOperationException($"role constituent could not be composed: {value}");

    private static OrderedCompositionComponent RequiredComponent(
        SubstrateChangeBuilder builder, string value, Hash128 source) =>
        ContentEmitter.StageComponent(builder, value, source)
        ?? throw new InvalidOperationException($"role constituent could not be admitted: {value}");

    private static string? Normalize(string? rawRoleKey) =>
        string.IsNullOrWhiteSpace(rawRoleKey)
            ? null
            : rawRoleKey.Trim().Normalize(NormalizationForm.FormC);
}
