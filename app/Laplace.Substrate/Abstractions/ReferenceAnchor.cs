using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Identity domains for opaque catalog keys. These values are persisted as part of the
/// identity preimage; append only and never renumber them.
/// </summary>
public enum ReferenceIdentityKind : ushort
{
    CiliIli = 1,
    WordNetSenseKey = 2,
    WordNetSynsetKey = 3,
    CiliMapVersion = 4,
    PropBankRoleset = 5,
    VerbNetClass = 6,
    FrameNetLexicalUnit = 7,
    WikidataItem = 8,
}

/// <summary>
/// Admission path for opaque references. A reference is a governed identity, not literal
/// content: emitting one stages exactly one typed entity and never runs its serialization
/// through the Unicode/content DAG.
/// </summary>
public static class ReferenceAnchor
{
    private static ReadOnlySpan<byte> Domain => "laplace/reference-anchor/v1\0"u8;

    public static Hash128? Id(ReferenceIdentityKind kind, string? rawKey)
    {
        string? key = Normalize(rawKey);
        return key is null ? null : IdUtf8(kind, Encoding.UTF8.GetBytes(key));
    }

    public static Hash128 IdUtf8(ReferenceIdentityKind kind, ReadOnlySpan<byte> normalizedKey)
    {
        if (kind is < ReferenceIdentityKind.CiliIli or > ReferenceIdentityKind.WikidataItem)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown reference identity domain");
        if (normalizedKey.IsEmpty)
            throw new ArgumentException("reference key must not be empty", nameof(normalizedKey));

        int length = Domain.Length + sizeof(ushort) + sizeof(int) + normalizedKey.Length;
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
            BinaryPrimitives.WriteInt32LittleEndian(preimage[cursor..], normalizedKey.Length);
            cursor += sizeof(int);
            normalizedKey.CopyTo(preimage[cursor..]);
            return Hash128.Blake3(preimage);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static Hash128? Emit(
        SubstrateChangeBuilder builder,
        ReferenceIdentityKind kind,
        string? rawKey,
        Hash128 entityTypeId,
        Hash128 source,
        double trust)
    {
        Hash128? id = Declare(builder, kind, rawKey, entityTypeId, source);
        if (id is null) return null;
        CategoryAnchor.AttestCategory(builder, id.Value, entityTypeId, source, trust);
        return id;
    }

    public static Hash128? Declare(
        SubstrateChangeBuilder builder,
        ReferenceIdentityKind kind,
        string? rawKey,
        Hash128 entityTypeId,
        Hash128 source)
    {
        Hash128? id = Id(kind, rawKey);
        if (id is null) return null;
        builder.AddEntity(id.Value, EntityTier.Word, entityTypeId, source);
        return id;
    }

    public static Hash128? EmitUtf8(
        SubstrateChangeBuilder builder,
        ReferenceIdentityKind kind,
        ReadOnlySpan<byte> normalizedKey,
        Hash128 entityTypeId,
        Hash128 source,
        double trust)
    {
        if (normalizedKey.IsEmpty) return null;
        Hash128 id = DeclareUtf8(builder, kind, normalizedKey, entityTypeId, source)!.Value;
        CategoryAnchor.AttestCategory(builder, id, entityTypeId, source, trust);
        return id;
    }

    public static Hash128? DeclareUtf8(
        SubstrateChangeBuilder builder,
        ReferenceIdentityKind kind,
        ReadOnlySpan<byte> normalizedKey,
        Hash128 entityTypeId,
        Hash128 source)
    {
        if (normalizedKey.IsEmpty) return null;
        Hash128 id = IdUtf8(kind, normalizedKey);
        builder.AddEntity(id, EntityTier.Word, entityTypeId, source);
        return id;
    }

    public static Hash128? WordNetSynsetKeyId(string version, string key) =>
        Id(ReferenceIdentityKind.WordNetSynsetKey, VersionedKey(version, key));

    public static Hash128? DeclareWordNetSynsetKey(
        SubstrateChangeBuilder builder,
        string version,
        string key,
        Hash128 source) =>
        Declare(builder, ReferenceIdentityKind.WordNetSynsetKey, VersionedKey(version, key),
            EntityTypeRegistry.SourceReference, source);

    private static string? Normalize(string? rawKey) =>
        string.IsNullOrWhiteSpace(rawKey) ? null : rawKey.Trim();

    private static string VersionedKey(string version, string key)
    {
        string? normalizedVersion = Normalize(version);
        string? normalizedKey = Normalize(key);
        if (normalizedVersion is null)
            throw new ArgumentException("reference version must not be empty", nameof(version));
        if (normalizedKey is null)
            throw new ArgumentException("reference key must not be empty", nameof(key));
        return $"{normalizedVersion}\0{normalizedKey}";
    }
}
