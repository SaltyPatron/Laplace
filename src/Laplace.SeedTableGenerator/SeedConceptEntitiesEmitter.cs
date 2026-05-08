namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Laplace.Core.Abstractions;

/// <summary>
/// Emits the foundational tier-1 concept entities — script names, general
/// category names, block names, age strings, bidi class names, decomposition
/// type names, plus edge-type names ("script"/"general_category"/etc.) and
/// role names ("source"/"target"), plus the substrate-atom physicality type
/// name. Each concept is a substrate entity whose identity is the BLAKE3
/// Merkle of its codepoint LINESTRING (per CLAUDE.md invariant 1+4: edges
/// reference concept entities, themselves composed of codepoints — never
/// hardcoded English strings as edge-type labels).
///
/// Per substrate invariant 2: position of a tier-1 composition is the vertex
/// centroid of constituent positions in the 4-ball (pre-S^3-projection). The
/// trajectory column carries the full LINESTRING4D through all constituents.
///
/// Output files (TSV, schema-aligned with laplace_pg--0.1.0.sql):
///   - entity_tier1.tsv  — one row per concept entity
///   - entity_child.tsv  — composition links (parent_concept → child_codepoint)
///
/// The Build call additionally returns the name → AtomId map so the property-
/// edge emitter (next step in Phase B) can reference the right target hash
/// for each (codepoint, property_value) pair without recomputing.
/// </summary>
public static class SeedConceptEntitiesEmitter
{
    /// <summary>The concept-entity name vocabulary that the substrate's
    /// foundational seed bakes in. Each name is a textual identifier that
    /// itself decomposes to codepoints under F1 — the substrate has no
    /// English-string-typed edges; types ARE these tier-1 entities.</summary>
    public static readonly string[] EdgeTypeNames =
    {
        "script",
        "general_category",
        "block",
        "age",
        "bidi_class",
        "decomposition_type",
        "decomposition_mapping",
        "kRSUnicode",
        "uca_primary",
        "iso639_scope",
        "iso639_type",
        "iso639_ref_name",
        "iso639_part1",
        "iso639_part2b",
        "iso639_part2t",
        // Unihan property attestations (CJK Unified Ideographs).
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

    public static readonly string[] RoleNames =
    {
        "source",
        "target",
    };

    public const string AtomPhysicalityTypeName = "codepoint_s3_substrate";

    public sealed class Result
    {
        public IReadOnlyDictionary<string, AtomId> ConceptByName { get; init; } = null!;
        public IReadOnlyDictionary<string, Point4D> PositionByName { get; init; } = null!;
    }

    public static Result Emit(
        IReadOnlyList<CodepointEntry> entries,
        IReadOnlyDictionary<int, AtomId> codepointHashes,
        IIdentityHashing hashing,
        string outputDir,
        IEnumerable<string>? additionalConceptNames = null)
    {
        var positionByCp = new Dictionary<int, Point4D>(entries.Count);
        foreach (var e in entries) { positionByCp[e.Codepoint] = e.Position; }

        // Distinct property values across all entries. Empty strings are not
        // concept entities (the entry simply has no value for that property).
        var distinct = new SortedSet<string>(System.StringComparer.Ordinal);
        foreach (var e in entries)
        {
            AddIfNonEmpty(distinct, e.Script);
            AddIfNonEmpty(distinct, e.GeneralCategory);
            AddIfNonEmpty(distinct, e.Block);
            AddIfNonEmpty(distinct, e.Age);
            AddIfNonEmpty(distinct, e.BidiClass);
        }

        // Add the substrate's structural concept names — edge-type names,
        // role names, and the substrate-atom physicality type name. They
        // dedupe with property-value names automatically (a property value
        // happening to be named "source" would collide intentionally).
        foreach (var n in EdgeTypeNames) { distinct.Add(n); }
        foreach (var n in RoleNames)     { distinct.Add(n); }
        distinct.Add(AtomPhysicalityTypeName);

        // Caller-supplied additional concept names (e.g., ISO 639 language
        // codes + ref_names + scope/type enum values). All become tier-1
        // entities the same way: BLAKE3 Merkle of their codepoint LINESTRING.
        if (additionalConceptNames is not null)
        {
            foreach (var n in additionalConceptNames) { AddIfNonEmpty(distinct, n); }
        }

        // For each distinct name, compute concept entity hash, vertex
        // centroid, and trajectory linestring.
        var conceptByName  = new Dictionary<string, AtomId>(distinct.Count, System.StringComparer.Ordinal);
        var positionByName = new Dictionary<string, Point4D>(distinct.Count, System.StringComparer.Ordinal);
        var concepts = new List<(string Name, AtomId Hash, Point4D Position, IReadOnlyList<(int Cp, AtomId ChildHash, int Rle)> Children)>(distinct.Count);

        foreach (var name in distinct)
        {
            var (children, hash, centroid) = ComposeConcept(name, codepointHashes, positionByCp, hashing);
            conceptByName[name]  = hash;
            positionByName[name] = centroid;
            concepts.Add((name, hash, centroid, children));
        }

        EmitEntityTier1Tsv(concepts, positionByCp, outputDir);
        EmitEntityChildTsv(concepts, outputDir);

        return new Result
        {
            ConceptByName  = conceptByName,
            PositionByName = positionByName,
        };
    }

    private static (
        IReadOnlyList<(int Cp, AtomId ChildHash, int Rle)> Children,
        AtomId Hash,
        Point4D Centroid)
        ComposeConcept(
            string name,
            IReadOnlyDictionary<int, AtomId> codepointHashes,
            Dictionary<int, Point4D> positionByCp,
            IIdentityHashing hashing)
    {
        var runs = new List<(int Cp, AtomId ChildHash, int Rle)>(name.Length);

        // RLE-collapse adjacent identical runes per CLAUDE.md invariant 3.
        int? prevCp = null;
        AtomId prevHash = default;
        var prevRun = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            var cp = rune.Value;
            if (!codepointHashes.TryGetValue(cp, out var hash))
            {
                continue;
            }
            if (prevCp.HasValue && prevCp.Value == cp)
            {
                prevRun++;
            }
            else
            {
                if (prevCp.HasValue)
                {
                    runs.Add((prevCp.Value, prevHash, prevRun));
                }
                prevCp   = cp;
                prevHash = hash;
                prevRun  = 1;
            }
        }
        if (prevCp.HasValue)
        {
            runs.Add((prevCp.Value, prevHash, prevRun));
        }

        // Composition hash = BLAKE3 Merkle of (childHash, rle_count) pairs.
        var children = new AtomId[runs.Count];
        var counts   = new int[runs.Count];
        for (int i = 0; i < runs.Count; ++i)
        {
            children[i] = runs[i].ChildHash;
            counts[i]   = runs[i].Rle;
        }
        var hashOut = hashing.CompositionId(children, counts);

        // Vertex centroid = average of constituent codepoint positions in
        // the 4-ball. CLAUDE.md invariant 2 calls for "vertex centroid for
        // the 4-ball (pre-projection)". We do NOT normalize back to S^3 here
        // — composition-tier entities live in the 4-ball, not on S^3.
        double sx = 0, sy = 0, sz = 0, sw = 0;
        var totalRune = 0;
        foreach (var (cp, _, rle) in runs)
        {
            if (positionByCp.TryGetValue(cp, out var pos))
            {
                sx += pos.X * rle; sy += pos.Y * rle; sz += pos.Z * rle; sw += pos.W * rle;
                totalRune += rle;
            }
        }
        var centroid = totalRune > 0
            ? new Point4D(sx / totalRune, sy / totalRune, sz / totalRune, sw / totalRune)
            : new Point4D(0, 0, 0, 0);

        return (runs, hashOut, centroid);
    }

    /// <summary>
    /// entity_tier1.tsv — one row per concept entity. Schema (matches the
    /// entity table partition entity_tier1):
    ///   1. entity_hash       (bytea, \x + hex)
    ///   2. tier              (smallint, 1)
    ///   3. codepoint         (NULL — only tier-0 atoms carry codepoint)
    ///   4. content           (bytea, UTF-8 of the concept name)
    ///   5. centroid_4d       (point4d, vertex centroid of constituent positions)
    ///   6. trajectory        (linestring4d, full path through constituents)
    ///   7. prime_flags       (bigint — Text modality bit only at this tier)
    ///   8. structural_flags  (smallint, 0)
    /// </summary>
    private static void EmitEntityTier1Tsv(
        IReadOnlyList<(string Name, AtomId Hash, Point4D Position, IReadOnlyList<(int Cp, AtomId ChildHash, int Rle)> Children)> concepts,
        Dictionary<int, Point4D> positionByCp,
        string outputDir)
    {
        var path = Path.Combine(outputDir, "entity_tier1.tsv");
        using var w = CHeaderWriter.OpenWriter(path);
        var sb = new StringBuilder(512);

        foreach (var c in concepts)
        {
            var contentBytes = Encoding.UTF8.GetBytes(c.Name);

            sb.Clear();
            sb.Append(@"\x").Append(SeedDbRowsEmitter.ToHexLower(c.Hash.AsSpan())).Append('\t');
            sb.Append('1').Append('\t');
            sb.Append(@"\N").Append('\t');
            sb.Append(@"\x").Append(SeedDbRowsEmitter.ToHexLower(contentBytes)).Append('\t');
            sb.Append("POINT4D(")
              .Append(SeedDbRowsEmitter.FormatDouble(c.Position.X)).Append(' ')
              .Append(SeedDbRowsEmitter.FormatDouble(c.Position.Y)).Append(' ')
              .Append(SeedDbRowsEmitter.FormatDouble(c.Position.Z)).Append(' ')
              .Append(SeedDbRowsEmitter.FormatDouble(c.Position.W))
              .Append(')').Append('\t');

            // trajectory: full LINESTRING4D through each constituent codepoint
            // position. RLE-collapsed runs contribute one vertex each (the
            // run's codepoint position). LINESTRING4D requires >= 2 vertices,
            // so single-codepoint names duplicate the vertex.
            sb.Append("LINESTRING4D(");
            var emittedAny = false;
            for (int i = 0; i < c.Children.Count; ++i)
            {
                var (cp, _, _) = c.Children[i];
                if (!positionByCp.TryGetValue(cp, out var p)) { continue; }
                if (emittedAny) { sb.Append(", "); }
                sb.Append(SeedDbRowsEmitter.FormatDouble(p.X)).Append(' ')
                  .Append(SeedDbRowsEmitter.FormatDouble(p.Y)).Append(' ')
                  .Append(SeedDbRowsEmitter.FormatDouble(p.Z)).Append(' ')
                  .Append(SeedDbRowsEmitter.FormatDouble(p.W));
                emittedAny = true;
            }
            if (c.Children.Count < 2)
            {
                // duplicate the (only) vertex so the linestring has >= 2 verts
                sb.Append(", ");
                if (c.Children.Count == 1 && positionByCp.TryGetValue(c.Children[0].Cp, out var p1))
                {
                    sb.Append(SeedDbRowsEmitter.FormatDouble(p1.X)).Append(' ')
                      .Append(SeedDbRowsEmitter.FormatDouble(p1.Y)).Append(' ')
                      .Append(SeedDbRowsEmitter.FormatDouble(p1.Z)).Append(' ')
                      .Append(SeedDbRowsEmitter.FormatDouble(p1.W));
                }
                else
                {
                    // 0-codepoint case: synthesize from the centroid (which
                    // is (0,0,0,0) per ComposeConcept's else branch)
                    sb.Append("0 0 0 0, 0 0 0 0");
                }
            }
            sb.Append(')').Append('\t');

            sb.Append(unchecked((long)PrimeFlags.Text).ToString(CultureInfo.InvariantCulture)).Append('\t');
            sb.Append('0');
            w.WriteLine(sb.ToString());
        }
    }

    /// <summary>
    /// entity_child.tsv — composition links. One row per RLE-run within
    /// each concept entity's codepoint LINESTRING.
    /// Schema:
    ///   1. parent_hash   (bytea, the concept entity hash)
    ///   2. parent_tier   (smallint, 1)
    ///   3. position      (integer, ordinal in the LINESTRING)
    ///   4. child_hash    (bytea, the codepoint atom hash)
    ///   5. child_tier    (smallint, 0)
    ///   6. rle_count     (integer)
    /// </summary>
    private static void EmitEntityChildTsv(
        IReadOnlyList<(string Name, AtomId Hash, Point4D Position, IReadOnlyList<(int Cp, AtomId ChildHash, int Rle)> Children)> concepts,
        string outputDir)
    {
        var path = Path.Combine(outputDir, "entity_child.tsv");
        using var w = CHeaderWriter.OpenWriter(path);
        var sb = new StringBuilder(256);

        foreach (var c in concepts)
        {
            var parentHashHex = SeedDbRowsEmitter.ToHexLower(c.Hash.AsSpan());
            for (int i = 0; i < c.Children.Count; ++i)
            {
                var ch = c.Children[i];
                sb.Clear();
                sb.Append(@"\x").Append(parentHashHex).Append('\t');
                sb.Append('1').Append('\t');
                sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(@"\x").Append(SeedDbRowsEmitter.ToHexLower(ch.ChildHash.AsSpan())).Append('\t');
                sb.Append('0').Append('\t');
                sb.Append(ch.Rle.ToString(CultureInfo.InvariantCulture));
                w.WriteLine(sb.ToString());
            }
        }
    }

    private static void AddIfNonEmpty(SortedSet<string> set, string? s)
    {
        if (!string.IsNullOrEmpty(s)) { set.Add(s); }
    }
}
