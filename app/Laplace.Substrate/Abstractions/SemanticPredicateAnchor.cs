using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public enum SemanticPredicateIdentityKind : ushort
{
    VerbNet = 1,
}

public readonly record struct SemanticPredicateArgument(string Type, string Value);

/// <summary>
/// Identity for one predicate occurrence inside an owning semantic frame. The globally shared
/// predicate label remains content; its argument binding and source position identify the
/// occurrence whose roles the source actually described.
/// </summary>
public static class SemanticPredicateAnchor
{
    private static ReadOnlySpan<byte> Domain => "laplace/semantic-predicate-anchor/v1\0"u8;

    public static Hash128 Id(
        SemanticPredicateIdentityKind kind,
        Hash128 ownerId,
        int frameOrdinal,
        int predicateOrdinal,
        Hash128 labelId,
        IReadOnlyList<SemanticPredicateArgument> arguments)
    {
        if (kind != SemanticPredicateIdentityKind.VerbNet)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown predicate identity domain");
        if (ownerId == default) throw new ArgumentException("predicate owner must not be empty", nameof(ownerId));
        if (labelId == default) throw new ArgumentException("predicate label must not be empty", nameof(labelId));
        if (frameOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(frameOrdinal));
        if (predicateOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(predicateOrdinal));
        ArgumentNullException.ThrowIfNull(arguments);

        var encoded = new (byte[] Type, byte[] Value)[arguments.Count];
        int length = Domain.Length + sizeof(ushort) + 16 + sizeof(int) + sizeof(int) + 16 + sizeof(int);
        for (int i = 0; i < arguments.Count; i++)
        {
            string type = Normalize(arguments[i].Type);
            string value = Normalize(arguments[i].Value);
            encoded[i] = (Encoding.UTF8.GetBytes(type), Encoding.UTF8.GetBytes(value));
            length = checked(length + sizeof(int) + encoded[i].Type.Length
                + sizeof(int) + encoded[i].Value.Length);
        }

        byte[]? rented = null;
        Span<byte> preimage = length <= 1024
            ? stackalloc byte[length]
            : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            int cursor = 0;
            Domain.CopyTo(preimage);
            cursor += Domain.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(preimage[cursor..], (ushort)kind);
            cursor += sizeof(ushort);
            WriteHash(preimage, ref cursor, ownerId);
            BinaryPrimitives.WriteInt32LittleEndian(preimage[cursor..], frameOrdinal);
            cursor += sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(preimage[cursor..], predicateOrdinal);
            cursor += sizeof(int);
            WriteHash(preimage, ref cursor, labelId);
            BinaryPrimitives.WriteInt32LittleEndian(preimage[cursor..], encoded.Length);
            cursor += sizeof(int);
            foreach (var (type, value) in encoded)
            {
                WriteBytes(preimage, ref cursor, type);
                WriteBytes(preimage, ref cursor, value);
            }
            return Hash128.Blake3(preimage);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static Hash128 Declare(
        SubstrateChangeBuilder builder,
        SemanticPredicateIdentityKind kind,
        Hash128 ownerId,
        int frameOrdinal,
        int predicateOrdinal,
        Hash128 labelId,
        IReadOnlyList<SemanticPredicateArgument> arguments,
        Hash128 entityTypeId,
        Hash128 source)
    {
        Hash128 id = Id(kind, ownerId, frameOrdinal, predicateOrdinal, labelId, arguments);
        builder.AddEntity(id, EntityTier.Word, entityTypeId, source);
        return id;
    }

    private static string Normalize(string value) =>
        (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);

    private static void WriteHash(Span<byte> target, ref int cursor, Hash128 value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(target[cursor..], value.Hi);
        cursor += sizeof(ulong);
        BinaryPrimitives.WriteUInt64LittleEndian(target[cursor..], value.Lo);
        cursor += sizeof(ulong);
    }

    private static void WriteBytes(Span<byte> target, ref int cursor, byte[] value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target[cursor..], value.Length);
        cursor += sizeof(int);
        value.CopyTo(target[cursor..]);
        cursor += value.Length;
    }
}
