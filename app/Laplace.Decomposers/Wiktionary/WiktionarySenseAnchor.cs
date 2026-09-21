using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// A Wiktionary sense is an observed structured composition, not an opaque hash namespace.
/// Source sense ids win when present; otherwise the field-tagged payload is a canonical set.
/// The trajectory retains the parent lexical identity, language/POS scope, field markers and
/// exact content roots. Source refreshes therefore preserve a source-id sense while fallback
/// senses remain stable under member reordering.
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

    private static readonly Hash128 Schema =
        Hash128.OfCanonical("wiktionary/sense/structure/v2");
    private static readonly Hash128 SourceIdMode =
        Hash128.OfCanonical("wiktionary/sense/mode/source-id/v1");
    private static readonly Hash128 PayloadMode =
        Hash128.OfCanonical("wiktionary/sense/mode/field-payload/v1");
    private static readonly Hash128 None =
        Hash128.OfCanonical("wiktionary/sense/none/v1");

    public static Hash128? Id(
        Hash128 wordId, Hash128? languageId, Hash128? posId, WiktionaryEntry.Sense sense)
    {
        ArgumentNullException.ThrowIfNull(sense);
        if (wordId == default)
            throw new ArgumentException("sense parent word must not be empty", nameof(wordId));

        if (!TryMemberRoots(sense, out Hash128 mode, out var members))
            return null;
        Hash128[] flat = Flatten(wordId, languageId, posId, mode, members);
        return Hash128.Merkle(EntityTier.Word, flat);
    }

    public static Hash128? Declare(
        SubstrateChangeBuilder builder,
        string wordSurface,
        Hash128 wordId,
        Hash128? languageId,
        Hash128? posId,
        WiktionaryEntry.Sense sense,
        Hash128 source,
        IReadOnlyDictionary<string, Hash128>? roots = null,
        IReadOnlyDictionary<string, WiktionarySurfaceTrees.RootCoord>? coords = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sense);
        if (wordId == default)
            throw new ArgumentException("sense parent word must not be empty", nameof(wordId));

        List<(MemberKind Kind, string Value)> values = CollectMembers(sense, out Hash128 mode);
        if (values.Count == 0) return null;

        if (!TryResolveSurface(
                builder, wordSurface, source, roots, coords,
                out Hash128 resolvedWord, out WiktionarySurfaceTrees.RootCoord wordCoord)
            || resolvedWord != wordId)
            throw new InvalidOperationException("Wiktionary sense parent surface changed identity");

        var pairs = new Dictionary<Hash128, (Hash128 Marker, Hash128 Value, WiktionarySurfaceTrees.RootCoord Coord)>();
        foreach (var (kind, value) in values)
        {
            if (!TryResolveSurface(
                    builder, value, source, roots, coords,
                    out Hash128 valueRoot, out WiktionarySurfaceTrees.RootCoord valueCoord))
                return null;
            Hash128 marker = Marker(kind);
            Span<Hash128> pair = stackalloc Hash128[2] { marker, valueRoot };
            Hash128 pairKey = Hash128.Merkle(EntityTier.Word, pair);
            pairs.TryAdd(pairKey, (marker, valueRoot, valueCoord));
        }
        if (pairs.Count == 0) return null;

        var ordered = pairs.OrderBy(static pair => pair.Key, Hash128BytewiseComparer.Instance)
            .Select(static pair => (pair.Value.Marker, pair.Value.Value))
            .ToArray();
        Hash128[] flat = Flatten(wordId, languageId, posId, mode, ordered);
        Hash128 id = Hash128.Merkle(EntityTier.Word, flat);

        builder.AddEntity(id, EntityTier.Word, EntityTypeRegistry.WiktionarySense, source);

        var coordinateRows = new double[(pairs.Count + 1) * 4];
        wordCoord.CopyTo(coordinateRows.AsSpan(0, 4));
        int at = 4;
        foreach (var pair in pairs.OrderBy(static pair => pair.Key, Hash128BytewiseComparer.Instance))
        {
            pair.Value.Coord.CopyTo(coordinateRows.AsSpan(at, 4));
            at += 4;
        }
        double[] coord = Math4d.KarcherMean(coordinateRows);
        builder.AddPhysicality(new PhysicalityRow(
            PhysicalityId.Compute(id, PhysicalityType.ParseStructure),
            id, source, PhysicalityType.ParseStructure,
            coord[0], coord[1], coord[2], coord[3], Hilbert128.Encode(coord),
            Trajectory.Build(flat), flat.Length, null, null, 0));
        return id;
    }

    private static bool TryResolveSurface(
        SubstrateChangeBuilder builder,
        string surface,
        Hash128 source,
        IReadOnlyDictionary<string, Hash128>? roots,
        IReadOnlyDictionary<string, WiktionarySurfaceTrees.RootCoord>? coords,
        out Hash128 id,
        out WiktionarySurfaceTrees.RootCoord coord)
    {
        id = default;
        coord = default;
        if (roots is not null)
            return roots.TryGetValue(surface, out id)
                && coords is not null
                && coords.TryGetValue(surface, out coord);
        return WiktionarySurfaceTrees.TryStageWithCoord(
            builder, surface, source, retainForReuse: false, out id, out coord);
    }

    private static bool TryMemberRoots(
        WiktionaryEntry.Sense sense,
        out Hash128 mode,
        out (Hash128 Marker, Hash128 Value)[] members)
    {
        List<(MemberKind Kind, string Value)> values = CollectMembers(sense, out mode);
        if (values.Count == 0)
        {
            members = [];
            return false;
        }

        var unique = new Dictionary<Hash128, (Hash128 Marker, Hash128 Value)>();
        foreach (var (kind, value) in values)
        {
            Hash128? root = ContentEmitter.RootId(value);
            if (root is null) continue;
            Hash128 marker = Marker(kind);
            Span<Hash128> pair = stackalloc Hash128[2] { marker, root.Value };
            unique.TryAdd(
                Hash128.Merkle(EntityTier.Word, pair),
                (marker, root.Value));
        }
        if (unique.Count == 0)
        {
            members = [];
            return false;
        }

        members = unique
            .OrderBy(static pair => pair.Key, Hash128BytewiseComparer.Instance)
            .Select(static pair => pair.Value)
            .ToArray();
        return true;
    }

    private static Hash128[] Flatten(
        Hash128 wordId,
        Hash128? languageId,
        Hash128? posId,
        Hash128 mode,
        IReadOnlyList<(Hash128 Marker, Hash128 Value)> members)
    {
        var flat = new Hash128[5 + members.Count * 2];
        flat[0] = Schema;
        flat[1] = wordId;
        flat[2] = languageId ?? None;
        flat[3] = posId ?? None;
        flat[4] = mode;
        int cursor = 5;
        foreach (var member in members)
        {
            flat[cursor++] = member.Marker;
            flat[cursor++] = member.Value;
        }
        return flat;
    }

    private static List<(MemberKind Kind, string Value)> CollectMembers(
        WiktionaryEntry.Sense sense, out Hash128 mode)
    {
        var members = new List<(MemberKind, string)>(8);
        Add(members, MemberKind.SenseId, sense.SenseIds);
        if (members.Count > 0)
        {
            mode = SourceIdMode;
            return members;
        }

        mode = PayloadMode;
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
        return members;
    }

    private static void Add(
        List<(MemberKind Kind, string Value)> members,
        MemberKind kind,
        List<WiktionaryMember>? values)
    {
        if (values is null) return;
        foreach (var member in values)
            if (Normalize(member.Word) is { } normalized)
                members.Add((kind, normalized));
    }

    private static void Add(
        List<(MemberKind Kind, string Value)> members,
        MemberKind kind,
        List<string>? values)
    {
        if (values is null) return;
        foreach (string value in values)
            if (Normalize(value) is { } normalized)
                members.Add((kind, normalized));
    }

    private static Hash128 Marker(MemberKind kind) =>
        Hash128.OfCanonical($"wiktionary/sense/member-kind/{(ushort)kind}/v1");

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string value = raw.Trim();
        return value.IsNormalized(NormalizationForm.FormC)
            ? value
            : value.Normalize(NormalizationForm.FormC);
    }

    private sealed class Hash128BytewiseComparer : IComparer<Hash128>
    {
        public static readonly Hash128BytewiseComparer Instance = new();
        public int Compare(Hash128 x, Hash128 y) => x.CompareToBytewise(y);
    }
}
