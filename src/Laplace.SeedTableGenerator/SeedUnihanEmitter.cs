namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Laplace.Core.Abstractions;

/// <summary>
/// Emits Unihan property attestations for CJK Unified Ideograph codepoints.
/// Each non-empty Unihan property value (kRSUnicode, kMandarin, kCantonese,
/// kJapaneseOn, kJapaneseKun, kKorean, kVietnamese, kSimplifiedVariant,
/// kTraditionalVariant, kTotalStrokes, kFrequency, kGradeLevel) attaches as
/// an edge from the codepoint atom to the value-string concept entity via
/// the corresponding edge-type concept entity.
///
/// Closes verification gate G3 #19 ("every CJK Unified Ideograph has Unihan
/// property edges") at the artifact level.
///
/// Output:
///   edge_unihan.tsv         — codepoint → unihan property edges
///   edge_member_unihan.tsv  — source / target rows per edge
///
/// The edge-type concept entities ("kRSUnicode" etc.) AND every property
/// value string (e.g., "9.5", "zhōng", "中") must be present in
/// entity_tier1.tsv — emitted by SeedConceptEntitiesEmitter via the
/// additionalConceptNames feed driven by EnumerateConceptNames().
/// </summary>
public static class SeedUnihanEmitter
{
    /// <summary>The Unihan property names attested by the foundational seed.
    /// These match the UCD short-name convention and are present as
    /// attributes on &lt;char&gt; elements in ucd.all.flat.xml for CJK
    /// Unified Ideograph codepoints.</summary>
    public static readonly string[] PropertyNames =
    {
        "kRSUnicode",
        "kMandarin",
        "kCantonese",
        "kJapaneseOn",
        "kJapaneseKun",
        "kKorean",
        "kVietnamese",
        "kSimplifiedVariant",
        "kTraditionalVariant",
        "kTotalStrokes",
        "kFrequency",
        "kGradeLevel",
    };

    /// <summary>
    /// Distinct Unihan property values across all UCD records — pass to
    /// SeedConceptEntitiesEmitter as additional concept names.
    /// </summary>
    public static IEnumerable<string> EnumerateConceptValues(IReadOnlyList<CanonicalOrdering.OrderingKey> ordering)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var key in ordering)
        {
            for (var i = 0; i < PropertyNames.Length; ++i)
            {
                var v = key.Record.Get(PropertyNames[i]);
                if (string.IsNullOrEmpty(v)) { continue; }
                if (seen.Add(v)) { yield return v; }
            }
        }
    }

    public static void Emit(
        IReadOnlyList<CanonicalOrdering.OrderingKey> ordering,
        Dictionary<int, AtomId> codepointHashes,
        Dictionary<string, AtomId> conceptByName,
        IIdentityHashing hashing,
        string outputDir)
    {
        var sourceRoleHash = conceptByName["source"];
        var targetRoleHash = conceptByName["target"];
        var sourceRoleHex  = SeedDbRowsEmitter.ToHexLower(sourceRoleHash.AsSpan());
        var targetRoleHex  = SeedDbRowsEmitter.ToHexLower(targetRoleHash.AsSpan());

        // Pre-resolve edge-type concept hashes once.
        var edgeTypeHashes = new AtomId[PropertyNames.Length];
        for (var i = 0; i < PropertyNames.Length; ++i)
        {
            edgeTypeHashes[i] = conceptByName[PropertyNames[i]];
        }

        var edgePath   = Path.Combine(outputDir, "edge_unihan.tsv");
        var memberPath = Path.Combine(outputDir, "edge_member_unihan.tsv");

        using var edgeW   = CHeaderWriter.OpenWriter(edgePath);
        using var memberW = CHeaderWriter.OpenWriter(memberPath);

        var edgeSb   = new StringBuilder(192);
        var memberSb = new StringBuilder(384);
        var members = new (AtomId Role, int RolePosition, AtomId Participant)[2];

        var edgesEmitted   = 0L;
        var membersEmitted = 0L;

        // Iterate ordering; each codepoint's Record carries the Unihan attrs
        // (CJK Unified Ideographs only — others have no kRSUnicode etc.).
        foreach (var key in ordering)
        {
            if (!codepointHashes.TryGetValue(key.Codepoint, out var cpHash)) { continue; }
            var cpHashHex = SeedDbRowsEmitter.ToHexLower(cpHash.AsSpan());

            for (var i = 0; i < PropertyNames.Length; ++i)
            {
                var value = key.Record.Get(PropertyNames[i]);
                if (string.IsNullOrEmpty(value)) { continue; }
                if (!conceptByName.TryGetValue(value, out var valueHash)) { continue; }

                var edgeTypeHash = edgeTypeHashes[i];
                members[0] = (sourceRoleHash, 0, cpHash);
                members[1] = (targetRoleHash, 0, valueHash);
                var edgeHash = hashing.EdgeId(edgeTypeHash, members);

                var edgeHashHex     = SeedDbRowsEmitter.ToHexLower(edgeHash.AsSpan());
                var edgeTypeHashHex = SeedDbRowsEmitter.ToHexLower(edgeTypeHash.AsSpan());
                var valueHashHex    = SeedDbRowsEmitter.ToHexLower(valueHash.AsSpan());

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
                memberSb.Append(@"\x").Append(cpHashHex);
                memberW.WriteLine(memberSb.ToString());

                memberSb.Clear();
                memberSb.Append(@"\x").Append(edgeHashHex).Append('\t');
                memberSb.Append(@"\x").Append(edgeTypeHashHex).Append('\t');
                memberSb.Append(@"\x").Append(targetRoleHex).Append('\t');
                memberSb.Append('0').Append('\t');
                memberSb.Append(@"\x").Append(valueHashHex);
                memberW.WriteLine(memberSb.ToString());
                membersEmitted += 2;
            }
        }

        System.Console.WriteLine(
            $"  emitted {edgesEmitted.ToString(CultureInfo.InvariantCulture)} Unihan edges, " +
            $"{membersEmitted.ToString(CultureInfo.InvariantCulture)} edge_member rows");
    }
}
