namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Iso639;

/// <summary>
/// Emits ISO 639-3 language attestations: each language is a tier-1
/// substrate entity (its 3-letter Id is the canonical content; its hash is
/// the BLAKE3 Merkle of that code's codepoint LINESTRING). Property edges
/// connect each language to its scope, type, ref_name, and alternate-code
/// concept entities.
///
/// Per CLAUDE.md invariants:
///   1. Identity = content (the language entity hash IS the hash of "eng",
///      "jpn", "deu" as codepoint LINESTRINGs).
///   4. Edges reference concept entities, NOT hardcoded labels — the edge
///      types ("iso639_scope", "iso639_type", etc.) are themselves
///      compositions of their name's codepoint LINESTRINGs.
///   12. Cross-language equivalence (cat / neko / gato / chat) is graph-
///      emergent — these language entities are PEERS, none anchored.
///
/// Output:
///   edge_iso639.tsv         — language → property edges
///   edge_member_iso639.tsv  — source / target rows per edge
///
/// (Language ENTITIES themselves go through SeedConceptEntitiesEmitter via
/// the additionalConceptNames parameter, so they land in entity_tier1.tsv +
/// entity_child.tsv alongside the UCD-property concept entities.)
///
/// Closes verification gate G3 #21 ("ISO 639 entity completeness") at the
/// artifact level.
/// </summary>
public static class SeedIso639Emitter
{
    /// <summary>The full set of ISO 639 attribute strings the foundational
    /// seed needs as concept entities. Includes language codes, ref_names,
    /// and the scope/type enum value names.</summary>
    public static IEnumerable<string> EnumerateConceptNames(IReadOnlyList<Iso639LanguageRecord> languages)
    {
        // Scope + type enum value names.
        foreach (var s in System.Enum.GetNames<Iso639Scope>())        { yield return s; }
        foreach (var t in System.Enum.GetNames<Iso639LanguageType>()) { yield return t; }

        // Per-language codes + ref_name.
        foreach (var lang in languages)
        {
            yield return lang.Id;
            if (!string.IsNullOrEmpty(lang.Part1))  { yield return lang.Part1!; }
            if (!string.IsNullOrEmpty(lang.Part2b)) { yield return lang.Part2b!; }
            if (!string.IsNullOrEmpty(lang.Part2t)) { yield return lang.Part2t!; }
            if (!string.IsNullOrEmpty(lang.ReferenceName)) { yield return lang.ReferenceName; }
        }
    }

    public static void Emit(
        IReadOnlyList<Iso639LanguageRecord> languages,
        Dictionary<string, AtomId> conceptByName,
        IIdentityHashing hashing,
        string outputDir)
    {
        var sourceRoleHash = conceptByName["source"];
        var targetRoleHash = conceptByName["target"];

        var scopeTypeHash    = conceptByName["iso639_scope"];
        var typeTypeHash     = conceptByName["iso639_type"];
        var refNameTypeHash  = conceptByName["iso639_ref_name"];
        var part1TypeHash    = conceptByName["iso639_part1"];
        var part2bTypeHash   = conceptByName["iso639_part2b"];
        var part2tTypeHash   = conceptByName["iso639_part2t"];

        var edgePath   = Path.Combine(outputDir, "edge_iso639.tsv");
        var memberPath = Path.Combine(outputDir, "edge_member_iso639.tsv");

        using var edgeW   = CHeaderWriter.OpenWriter(edgePath);
        using var memberW = CHeaderWriter.OpenWriter(memberPath);

        var edgeSb   = new StringBuilder(192);
        var memberSb = new StringBuilder(384);

        var sourceRoleHex = SeedDbRowsEmitter.ToHexLower(sourceRoleHash.AsSpan());
        var targetRoleHex = SeedDbRowsEmitter.ToHexLower(targetRoleHash.AsSpan());

        var members = new (AtomId Role, int RolePosition, AtomId Participant)[2];
        var edgesEmitted   = 0L;
        var membersEmitted = 0L;

        foreach (var lang in languages)
        {
            if (!conceptByName.TryGetValue(lang.Id, out var langHash)) { continue; }
            var langHex = SeedDbRowsEmitter.ToHexLower(langHash.AsSpan());

            // 1) Scope: language → iso639_scope → "Individual"|"Macrolanguage"|"Special"
            EmitEdge(
                edgeTypeHash:   scopeTypeHash,
                sourceHash:     langHash, sourceHex: langHex,
                targetName:     lang.Scope.ToString(),
                conceptByName:  conceptByName,
                hashing:        hashing,
                sourceRoleHash: sourceRoleHash, sourceRoleHex: sourceRoleHex,
                targetRoleHash: targetRoleHash, targetRoleHex: targetRoleHex,
                edgeW:          edgeW, edgeSb: edgeSb,
                memberW:        memberW, memberSb: memberSb,
                edgesEmitted:   ref edgesEmitted,
                membersEmitted: ref membersEmitted);

            // 2) Type: language → iso639_type → "Living"|"Extinct"|...
            EmitEdge(
                edgeTypeHash:   typeTypeHash,
                sourceHash:     langHash, sourceHex: langHex,
                targetName:     lang.Type.ToString(),
                conceptByName:  conceptByName,
                hashing:        hashing,
                sourceRoleHash: sourceRoleHash, sourceRoleHex: sourceRoleHex,
                targetRoleHash: targetRoleHash, targetRoleHex: targetRoleHex,
                edgeW:          edgeW, edgeSb: edgeSb,
                memberW:        memberW, memberSb: memberSb,
                edgesEmitted:   ref edgesEmitted,
                membersEmitted: ref membersEmitted);

            // 3) Ref name (English): language → iso639_ref_name → name composition
            if (!string.IsNullOrEmpty(lang.ReferenceName))
            {
                EmitEdge(
                    edgeTypeHash:   refNameTypeHash,
                    sourceHash:     langHash, sourceHex: langHex,
                    targetName:     lang.ReferenceName,
                    conceptByName:  conceptByName,
                    hashing:        hashing,
                    sourceRoleHash: sourceRoleHash, sourceRoleHex: sourceRoleHex,
                    targetRoleHash: targetRoleHash, targetRoleHex: targetRoleHex,
                    edgeW:          edgeW, edgeSb: edgeSb,
                    memberW:        memberW, memberSb: memberSb,
                    edgesEmitted:   ref edgesEmitted,
                    membersEmitted: ref membersEmitted);
            }

            // 4-6) Alternate codes (Part1 / Part2b / Part2t) — only when the
            // ISO defines them for this language.
            EmitOptionalCodeEdge(part1TypeHash, lang.Part1, langHash, langHex, conceptByName, hashing,
                sourceRoleHash, sourceRoleHex, targetRoleHash, targetRoleHex,
                edgeW, edgeSb, memberW, memberSb, ref edgesEmitted, ref membersEmitted);
            EmitOptionalCodeEdge(part2bTypeHash, lang.Part2b, langHash, langHex, conceptByName, hashing,
                sourceRoleHash, sourceRoleHex, targetRoleHash, targetRoleHex,
                edgeW, edgeSb, memberW, memberSb, ref edgesEmitted, ref membersEmitted);
            EmitOptionalCodeEdge(part2tTypeHash, lang.Part2t, langHash, langHex, conceptByName, hashing,
                sourceRoleHash, sourceRoleHex, targetRoleHash, targetRoleHex,
                edgeW, edgeSb, memberW, memberSb, ref edgesEmitted, ref membersEmitted);
        }

        System.Console.WriteLine(
            $"  emitted {edgesEmitted.ToString(CultureInfo.InvariantCulture)} ISO 639 edges, " +
            $"{membersEmitted.ToString(CultureInfo.InvariantCulture)} edge_member rows");
    }

    private static void EmitOptionalCodeEdge(
        AtomId edgeTypeHash, string? code,
        AtomId sourceHash, string sourceHex,
        Dictionary<string, AtomId> conceptByName,
        IIdentityHashing hashing,
        AtomId sourceRoleHash, string sourceRoleHex,
        AtomId targetRoleHash, string targetRoleHex,
        StreamWriter edgeW, StringBuilder edgeSb,
        StreamWriter memberW, StringBuilder memberSb,
        ref long edgesEmitted, ref long membersEmitted)
    {
        if (string.IsNullOrEmpty(code)) { return; }
        EmitEdge(
            edgeTypeHash:   edgeTypeHash,
            sourceHash:     sourceHash, sourceHex: sourceHex,
            targetName:     code,
            conceptByName:  conceptByName,
            hashing:        hashing,
            sourceRoleHash: sourceRoleHash, sourceRoleHex: sourceRoleHex,
            targetRoleHash: targetRoleHash, targetRoleHex: targetRoleHex,
            edgeW:          edgeW, edgeSb: edgeSb,
            memberW:        memberW, memberSb: memberSb,
            edgesEmitted:   ref edgesEmitted,
            membersEmitted: ref membersEmitted);
    }

    private static void EmitEdge(
        AtomId edgeTypeHash,
        AtomId sourceHash, string sourceHex,
        string targetName,
        Dictionary<string, AtomId> conceptByName,
        IIdentityHashing hashing,
        AtomId sourceRoleHash, string sourceRoleHex,
        AtomId targetRoleHash, string targetRoleHex,
        StreamWriter edgeW, StringBuilder edgeSb,
        StreamWriter memberW, StringBuilder memberSb,
        ref long edgesEmitted, ref long membersEmitted)
    {
        if (!conceptByName.TryGetValue(targetName, out var targetHash)) { return; }

        var members = new (AtomId Role, int RolePosition, AtomId Participant)[]
        {
            (sourceRoleHash, 0, sourceHash),
            (targetRoleHash, 0, targetHash),
        };
        var edgeHash = hashing.EdgeId(edgeTypeHash, members);

        var edgeHashHex      = SeedDbRowsEmitter.ToHexLower(edgeHash.AsSpan());
        var edgeTypeHashHex  = SeedDbRowsEmitter.ToHexLower(edgeTypeHash.AsSpan());
        var targetHashHex    = SeedDbRowsEmitter.ToHexLower(targetHash.AsSpan());

        edgeSb.Clear();
        edgeSb.Append(@"\x").Append(edgeHashHex).Append('\t');
        edgeSb.Append(@"\x").Append(edgeTypeHashHex).Append('\t');
        edgeSb.Append('2');
        edgeW.WriteLine(edgeSb.ToString());
        edgesEmitted++;

        memberSb.Clear();
        memberSb.Append(@"\x").Append(edgeHashHex).Append('\t');
        memberSb.Append(@"\x").Append(edgeTypeHashHex).Append('\t');
        memberSb.Append(@"\x").Append(sourceRoleHex).Append('\t');
        memberSb.Append('0').Append('\t');
        memberSb.Append(@"\x").Append(sourceHex);
        memberW.WriteLine(memberSb.ToString());

        memberSb.Clear();
        memberSb.Append(@"\x").Append(edgeHashHex).Append('\t');
        memberSb.Append(@"\x").Append(edgeTypeHashHex).Append('\t');
        memberSb.Append(@"\x").Append(targetRoleHex).Append('\t');
        memberSb.Append('0').Append('\t');
        memberSb.Append(@"\x").Append(targetHashHex);
        memberW.WriteLine(memberSb.ToString());
        membersEmitted += 2;
    }
}
