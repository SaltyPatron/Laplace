namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Laplace.Core.Abstractions;

/// <summary>
/// Emits property edges + edge_member rows connecting each codepoint atom
/// to its UCD property concept entities (script, general_category, block,
/// age, bidi_class). One edge per (codepoint × non-empty-property); two
/// edge_member rows per edge (source = codepoint atom, target = property
/// value concept entity).
///
/// Per substrate invariant 4 — knowledge IS edges and intersections — these
/// are the foundational typed-edge attestations of the tier-0 layer. Every
/// edge_type_hash is itself a substrate concept entity (composition of its
/// name's codepoint LINESTRING, computed by SeedConceptEntitiesEmitter).
/// Every role_hash ("source"/"target") is similarly a concept entity. There
/// are NO English-string edge type labels in the schema — types ARE
/// content-addressed entities (CLAUDE.md invariant 1+4).
///
/// Closes verification gate G3 #18 ("every assigned codepoint has every
/// applicable UCD property edge") at the artifact level.
///
/// Schema-aligned with laplace_pg--0.1.0.sql:
///   edge.tsv         — (edge_hash, edge_type_hash, member_count)
///                      created_at uses DEFAULT now() via column-list COPY
///   edge_member.tsv  — (edge_hash, edge_type_hash, role_hash, role_position, participant_hash)
/// </summary>
public static class SeedPropertyEdgesEmitter
{
    /// <summary>
    /// The five tier-0 UCD property axes the foundational seed attests.
    /// Each entry pairs the substrate edge-type concept name with a
    /// projector that reads the corresponding value off a CodepointEntry.
    /// </summary>
    private static readonly (string EdgeTypeName, System.Func<CodepointEntry, string?> Projector)[] PropertyProjectors =
    {
        ("script",           e => e.Script),
        ("general_category", e => e.GeneralCategory),
        ("block",            e => e.Block),
        ("age",              e => e.Age),
        ("bidi_class",       e => e.BidiClass),
    };

    public static void Emit(
        IReadOnlyList<CodepointEntry> entries,
        Dictionary<int, AtomId> codepointHashes,
        Dictionary<string, AtomId> conceptByName,
        IIdentityHashing hashing,
        string outputDir)
    {
        var roleSource = conceptByName["source"];
        var roleTarget = conceptByName["target"];

        // Pre-resolve edge_type_hash for each property — once.
        var edgeTypes = new (string Name, AtomId TypeHash)[PropertyProjectors.Length];
        for (var i = 0; i < PropertyProjectors.Length; ++i)
        {
            edgeTypes[i] = (PropertyProjectors[i].EdgeTypeName, conceptByName[PropertyProjectors[i].EdgeTypeName]);
        }

        var edgePath       = Path.Combine(outputDir, "edge.tsv");
        var edgeMemberPath = Path.Combine(outputDir, "edge_member.tsv");

        using var edgeW   = CHeaderWriter.OpenWriter(edgePath);
        using var memberW = CHeaderWriter.OpenWriter(edgeMemberPath);

        var edgeSb   = new StringBuilder(192);
        var memberSb = new StringBuilder(384);

        var members = new (AtomId Role, int RolePosition, AtomId Participant)[2];
        var emittedEdges   = 0L;
        var emittedMembers = 0L;

        var roleSourceHex = SeedDbRowsEmitter.ToHexLower(roleSource.AsSpan());
        var roleTargetHex = SeedDbRowsEmitter.ToHexLower(roleTarget.AsSpan());

        foreach (var entry in entries)
        {
            if (!codepointHashes.TryGetValue(entry.Codepoint, out var cpHash)) { continue; }
            var cpHashHex = SeedDbRowsEmitter.ToHexLower(cpHash.AsSpan());

            for (var i = 0; i < PropertyProjectors.Length; ++i)
            {
                var (name, projector) = PropertyProjectors[i];
                var value = projector(entry);
                if (string.IsNullOrEmpty(value)) { continue; }
                if (!conceptByName.TryGetValue(value, out var valueHash)) { continue; }

                var (_, edgeTypeHash) = edgeTypes[i];
                members[0] = (roleSource, 0, cpHash);
                members[1] = (roleTarget, 0, valueHash);
                var edgeHash = hashing.EdgeId(edgeTypeHash, members);

                var edgeHashHex     = SeedDbRowsEmitter.ToHexLower(edgeHash.AsSpan());
                var edgeTypeHashHex = SeedDbRowsEmitter.ToHexLower(edgeTypeHash.AsSpan());
                var valueHashHex    = SeedDbRowsEmitter.ToHexLower(valueHash.AsSpan());

                // edge row — 3 cols, created_at via DEFAULT.
                edgeSb.Clear();
                edgeSb.Append(@"\x").Append(edgeHashHex).Append('\t');
                edgeSb.Append(@"\x").Append(edgeTypeHashHex).Append('\t');
                edgeSb.Append('2');
                edgeW.WriteLine(edgeSb.ToString());
                emittedEdges++;

                // edge_member rows — source then target.
                memberSb.Clear();
                memberSb.Append(@"\x").Append(edgeHashHex).Append('\t');
                memberSb.Append(@"\x").Append(edgeTypeHashHex).Append('\t');
                memberSb.Append(@"\x").Append(roleSourceHex).Append('\t');
                memberSb.Append('0').Append('\t');
                memberSb.Append(@"\x").Append(cpHashHex);
                memberW.WriteLine(memberSb.ToString());

                memberSb.Clear();
                memberSb.Append(@"\x").Append(edgeHashHex).Append('\t');
                memberSb.Append(@"\x").Append(edgeTypeHashHex).Append('\t');
                memberSb.Append(@"\x").Append(roleTargetHex).Append('\t');
                memberSb.Append('0').Append('\t');
                memberSb.Append(@"\x").Append(valueHashHex);
                memberW.WriteLine(memberSb.ToString());
                emittedMembers += 2;
            }
        }

        System.Console.WriteLine(
            $"  emitted {emittedEdges.ToString(CultureInfo.InvariantCulture)} property edges, " +
            $"{emittedMembers.ToString(CultureInfo.InvariantCulture)} edge_member rows");
    }
}
