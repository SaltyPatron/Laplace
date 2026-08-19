using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The semantic-role systems whose labels are scoped by a parent predicate/frame.
/// Values are persisted in the identity preimage; append only and never renumber.
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
/// Identity for a role slot under its proposition-defining parent. "Agent" in one
/// VerbNet class and "Agent" in another are different roles even though they share a
/// human-readable label. The label remains content; it is not the role's identity.
/// </summary>
public static class RoleAnchor
{
    private static ReadOnlySpan<byte> Domain => "laplace/role-anchor/v1\0"u8;

    public static Hash128? Id(RoleIdentityKind kind, Hash128 parentId, string? rawRoleKey)
    {
        string? key = Normalize(rawRoleKey);
        if (key is null) return null;
        if (parentId == default)
            throw new ArgumentException("role parent must not be empty", nameof(parentId));
        if (kind is < RoleIdentityKind.PropBank or > RoleIdentityKind.Eso)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown role identity domain");

        byte[] keyUtf8 = Encoding.UTF8.GetBytes(key);
        int length = Domain.Length + sizeof(ushort) + 16 + sizeof(int) + keyUtf8.Length;
        byte[]? rented = null;
        Span<byte> preimage = length <= 512
            ? stackalloc byte[length]
            : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            Domain.CopyTo(preimage);
            int cursor = Domain.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(preimage[cursor..], (ushort)kind);
            cursor += sizeof(ushort);
            BinaryPrimitives.WriteUInt64LittleEndian(preimage[cursor..], parentId.Hi);
            cursor += sizeof(ulong);
            BinaryPrimitives.WriteUInt64LittleEndian(preimage[cursor..], parentId.Lo);
            cursor += sizeof(ulong);
            BinaryPrimitives.WriteInt32LittleEndian(preimage[cursor..], keyUtf8.Length);
            cursor += sizeof(int);
            keyUtf8.CopyTo(preimage[cursor..]);
            return Hash128.Blake3(preimage);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static Hash128? Declare(
        SubstrateChangeBuilder builder,
        RoleIdentityKind kind,
        Hash128 parentId,
        string? roleKey,
        Hash128 entityTypeId,
        Hash128 source)
    {
        Hash128? id = Id(kind, parentId, roleKey);
        if (id is null) return null;
        builder.AddEntity(id.Value, EntityTier.Word, entityTypeId, source);
        return id;
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
        CategoryAnchor.AttestCategory(builder, id.Value, entityTypeId, source, trust);
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

    private static string? Normalize(string? rawRoleKey) =>
        string.IsNullOrWhiteSpace(rawRoleKey)
            ? null
            : rawRoleKey.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();
}
