using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// Content-addressed identity for one source sense block. The source's sense IDs win
/// when present; otherwise the unordered, field-tagged sense payload is the fallback.
/// Neither path turns a source identifier or a semantic bundle into text content.
/// </summary>
internal static class WiktionarySenseAnchor
{
    private enum MemberKind : ushort
    {
        SenseId = 1,
        Gloss = 2,
        Example = 3,
        Synonym = 4,
        Antonym = 5,
        Hyponym = 6,
        Meronym = 7,
        Holonym = 8,
        Related = 9,
        Hypernym = 10,
        Derived = 11,
        Coordinate = 12,
        Tag = 13,
        LinkTarget = 14,
        Wikidata = 15,
    }

    private static ReadOnlySpan<byte> Domain => "laplace/wiktionary-sense/v1\0"u8;
    private static ReadOnlySpan<byte> MemberDomain => "laplace/wiktionary-sense-member/v1\0"u8;

    public static Hash128? Id(
        Hash128 wordId, Hash128? languageId, Hash128? posId, WiktionaryEntry.Sense sense)
    {
        ArgumentNullException.ThrowIfNull(sense);
        if (wordId == default)
            throw new ArgumentException("sense parent word must not be empty", nameof(wordId));

        var members = new List<Hash128>(8);
        Add(members, MemberKind.SenseId, sense.SenseIds);
        byte mode = 1;
        if (members.Count == 0)
        {
            mode = 2;
            Add(members, MemberKind.Gloss, sense.Glosses);
            Add(members, MemberKind.Example, sense.Examples);
            Add(members, MemberKind.Synonym, sense.Relations.Synonyms);
            Add(members, MemberKind.Antonym, sense.Relations.Antonyms);
            Add(members, MemberKind.Hyponym, sense.Relations.Hyponyms);
            Add(members, MemberKind.Meronym, sense.Relations.Meronyms);
            Add(members, MemberKind.Holonym, sense.Relations.Holonyms);
            Add(members, MemberKind.Related, sense.Relations.Related);
            Add(members, MemberKind.Hypernym, sense.Relations.Hypernyms);
            Add(members, MemberKind.Derived, sense.Relations.Derived);
            Add(members, MemberKind.Coordinate, sense.Relations.Coordinate);
            Add(members, MemberKind.Tag, sense.Tags);
            Add(members, MemberKind.LinkTarget, sense.LinkTargets);
            Add(members, MemberKind.Wikidata, sense.WikidataIds);
        }

        if (members.Count == 0) return null;
        members.Sort(static (x, y) => x.CompareToBytewise(y));
        int unique = 1;
        for (int i = 1; i < members.Count; i++)
            if (members[i] != members[unique - 1]) members[unique++] = members[i];

        int length = Domain.Length + 1 + 16 + 16 + 16 + sizeof(int) + unique * 16;
        byte[]? rented = null;
        Span<byte> preimage = length <= 512
            ? stackalloc byte[length]
            : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            Domain.CopyTo(preimage);
            int cursor = Domain.Length;
            preimage[cursor++] = mode;
            wordId.WriteBytes(preimage[cursor..]);
            cursor += 16;
            (languageId ?? default).WriteBytes(preimage[cursor..]);
            cursor += 16;
            (posId ?? default).WriteBytes(preimage[cursor..]);
            cursor += 16;
            BinaryPrimitives.WriteInt32LittleEndian(preimage[cursor..], unique);
            cursor += sizeof(int);
            for (int i = 0; i < unique; i++)
            {
                members[i].WriteBytes(preimage[cursor..]);
                cursor += 16;
            }
            return Hash128.Blake3(preimage);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static Hash128? Declare(
        SubstrateChangeBuilder builder,
        Hash128 wordId,
        Hash128? languageId,
        Hash128? posId,
        WiktionaryEntry.Sense sense,
        Hash128 source)
    {
        Hash128? id = Id(wordId, languageId, posId, sense);
        if (id is null) return null;
        builder.AddEntity(id.Value, EntityTier.Word, EntityTypeRegistry.WiktionarySense, source);
        return id;
    }

    private static void Add(List<Hash128> members, MemberKind kind, List<string>? values)
    {
        if (values is null) return;
        foreach (string value in values)
            if (Normalize(value) is { } normalized)
                members.Add(Member(kind, normalized));
    }

    private static Hash128 Member(MemberKind kind, string normalized)
    {
        int byteCount = Encoding.UTF8.GetByteCount(normalized);
        int length = MemberDomain.Length + sizeof(ushort) + sizeof(int) + byteCount;
        byte[]? rented = null;
        Span<byte> preimage = length <= 512
            ? stackalloc byte[length]
            : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            MemberDomain.CopyTo(preimage);
            int cursor = MemberDomain.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(preimage[cursor..], (ushort)kind);
            cursor += sizeof(ushort);
            BinaryPrimitives.WriteInt32LittleEndian(preimage[cursor..], byteCount);
            cursor += sizeof(int);
            Encoding.UTF8.GetBytes(normalized, preimage[cursor..]);
            return Hash128.Blake3(preimage);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string value = raw.Trim();
        return value.IsNormalized(NormalizationForm.FormC)
            ? value
            : value.Normalize(NormalizationForm.FormC);
    }
}
