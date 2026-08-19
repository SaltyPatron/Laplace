using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Source lexical-member systems whose entries are scoped by an owning class.
/// Values are persisted in the identity preimage; append only and never renumber.
/// </summary>
public enum LexicalMemberIdentityKind : ushort
{
    VerbNet = 1,
}

/// <summary>
/// Identity for a source member under its proposition-defining owner. The same
/// spelling or source key can occur in more than one class; the human-readable
/// lemma remains content connected through HAS_NAME_ALIAS.
/// </summary>
public static class LexicalMemberAnchor
{
    private static ReadOnlySpan<byte> Domain => "laplace/lexical-member-anchor/v1\0"u8;

    public static Hash128? Id(
        LexicalMemberIdentityKind kind, Hash128 ownerId, string? rawMemberKey)
    {
        string? key = Normalize(rawMemberKey);
        if (key is null) return null;
        if (ownerId == default)
            throw new ArgumentException("member owner must not be empty", nameof(ownerId));
        if (kind != LexicalMemberIdentityKind.VerbNet)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown member identity domain");

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
            BinaryPrimitives.WriteUInt64LittleEndian(preimage[cursor..], ownerId.Hi);
            cursor += sizeof(ulong);
            BinaryPrimitives.WriteUInt64LittleEndian(preimage[cursor..], ownerId.Lo);
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

    public static Hash128? Emit(
        SubstrateChangeBuilder builder,
        LexicalMemberIdentityKind kind,
        Hash128 ownerId,
        string? memberKey,
        Hash128 entityTypeId,
        Hash128 source,
        double trust)
    {
        Hash128? id = Id(kind, ownerId, memberKey);
        if (id is null) return null;
        builder.AddEntity(id.Value, EntityTier.Word, entityTypeId, source);
        CategoryAnchor.AttestCategory(builder, id.Value, entityTypeId, source, trust);
        return id;
    }

    private static string? Normalize(string? rawMemberKey) =>
        string.IsNullOrWhiteSpace(rawMemberKey)
            ? null
            : rawMemberKey.Trim().Normalize(NormalizationForm.FormC);
}
