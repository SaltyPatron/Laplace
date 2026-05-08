namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Laplace.Core.Abstractions;

/// <summary>
/// Emits Unicode decomposition attestations: for each codepoint with a
/// non-trivial canonical or compatibility decomposition (UCD properties
/// dt/dm), the substrate gets:
///
///   1. A new tier-1 composition entity for the decomposition target — the
///      content-addressed BLAKE3 Merkle of the decomposed-codepoint
///      LINESTRING with RLE. Multiple codepoints may decompose to the same
///      target sequence; identical compositions share one tier-1 row.
///   2. An edge: source_codepoint → decomposition_mapping → target_composition.
///   3. An edge: source_codepoint → decomposition_type → dt_concept
///      (where dt is "can", "compat", "font", "sub", "super", etc.).
///
/// Per CLAUDE.md invariant 1+4: decomposition target identity = content of
/// the decomposed sequence (NOT a label). Decomposition_type values are
/// also tier-1 concept entities composed of their codepoint LINESTRINGs.
///
/// Output:
///   entity_tier1_decomp.tsv    — decomposition target compositions
///   entity_child_decomp.tsv    — composition children (with RLE)
///   edge_decomp.tsv            — dt + dm edges
///   edge_member_decomp.tsv     — source/target rows per edge
/// </summary>
public static class SeedDecompositionEmitter
{
    /// <summary>
    /// Distinct UCD decomposition_type ("dt") values across all records.
    /// Pass to SeedConceptEntitiesEmitter so each becomes a tier-1 concept
    /// entity (composition of its name's codepoint LINESTRING).
    /// </summary>
    public static IEnumerable<string> EnumerateDecompositionTypeValues(
        IReadOnlyList<CanonicalOrdering.OrderingKey> ordering)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var key in ordering)
        {
            var dt = key.Record.Get("dt");
            if (string.IsNullOrEmpty(dt) || dt == "none") { continue; }
            if (seen.Add(dt)) { yield return dt; }
        }
    }

    public static void Emit(
        IReadOnlyList<CanonicalOrdering.OrderingKey> ordering,
        Dictionary<int, AtomId> codepointHashes,
        Dictionary<int, Point4D> positionByCp,
        Dictionary<string, AtomId> conceptByName,
        IIdentityHashing hashing,
        string outputDir)
    {
        var dmTypeHash = conceptByName["decomposition_mapping"];
        var dtTypeHash = conceptByName["decomposition_type"];
        var sourceRole = conceptByName["source"];
        var targetRole = conceptByName["target"];

        var sourceRoleHex = SeedDbRowsEmitter.ToHexLower(sourceRole.AsSpan());
        var targetRoleHex = SeedDbRowsEmitter.ToHexLower(targetRole.AsSpan());

        var decompTier1Path  = Path.Combine(outputDir, "entity_tier1_decomp.tsv");
        var decompChildPath  = Path.Combine(outputDir, "entity_child_decomp.tsv");
        var edgePath         = Path.Combine(outputDir, "edge_decomp.tsv");
        var memberPath       = Path.Combine(outputDir, "edge_member_decomp.tsv");

        using var tier1W  = CHeaderWriter.OpenWriter(decompTier1Path);
        using var childW  = CHeaderWriter.OpenWriter(decompChildPath);
        using var edgeW   = CHeaderWriter.OpenWriter(edgePath);
        using var memberW = CHeaderWriter.OpenWriter(memberPath);

        var sb = new StringBuilder(512);

        var emittedTargets = new HashSet<string>(System.StringComparer.Ordinal); // hex hash dedup
        var members = new (AtomId Role, int RolePosition, AtomId Participant)[2];

        var dmEdgeCount   = 0L;
        var dtEdgeCount   = 0L;
        var memberCount   = 0L;
        var targetCount   = 0L;

        foreach (var key in ordering)
        {
            if (!codepointHashes.TryGetValue(key.Codepoint, out var cpHash)) { continue; }
            var cpHashHex = SeedDbRowsEmitter.ToHexLower(cpHash.AsSpan());

            var dt = key.Record.Get("dt");
            var dm = key.Record.Get("dm");

            // dt edge (decomposition_type) — when present and not "none".
            if (!string.IsNullOrEmpty(dt) && dt != "none" &&
                conceptByName.TryGetValue(dt, out var dtValueHash))
            {
                members[0] = (sourceRole, 0, cpHash);
                members[1] = (targetRole, 0, dtValueHash);
                var edgeHash = hashing.EdgeId(dtTypeHash, members);
                EmitEdgeRow(edgeW, edgeHash, dtTypeHash, sb);
                EmitMemberRows(memberW, edgeHash, dtTypeHash,
                    sourceRoleHex, cpHashHex,
                    targetRoleHex, SeedDbRowsEmitter.ToHexLower(dtValueHash.AsSpan()),
                    sb);
                dtEdgeCount++;
                memberCount += 2;
            }

            // dm edge (decomposition_mapping) — when the dm field is a
            // non-trivial codepoint sequence ("#" sentinel = self, skip).
            if (string.IsNullOrEmpty(dm) || dm == "#") { continue; }

            var targetCps = ParseCodepointList(dm);
            if (targetCps.Count == 0) { continue; }

            // Compose the target entity: BLAKE3 Merkle of the target codepoint
            // LINESTRING with RLE collapse (per CLAUDE.md invariant 3).
            var (children, counts, contentBytes, centroid, trajectory) =
                ComposeTargetSequence(targetCps, codepointHashes, positionByCp);
            if (children.Count == 0) { continue; }

            var targetHash = hashing.CompositionId(children, counts);
            var targetHex  = SeedDbRowsEmitter.ToHexLower(targetHash.AsSpan());

            // Emit the target composition tier-1 row + entity_child rows
            // ONCE per unique target hash (multiple codepoints may decompose
            // to the same sequence — content addressing dedupes naturally).
            if (emittedTargets.Add(targetHex))
            {
                EmitTier1Row(tier1W, targetHash, contentBytes, centroid, trajectory, sb);
                EmitTier1ChildRows(childW, targetHash, children, counts, sb);
                targetCount++;
            }

            // Edge: source_codepoint → decomposition_mapping → target_composition
            members[0] = (sourceRole, 0, cpHash);
            members[1] = (targetRole, 0, targetHash);
            var dmEdgeHash = hashing.EdgeId(dmTypeHash, members);
            EmitEdgeRow(edgeW, dmEdgeHash, dmTypeHash, sb);
            EmitMemberRows(memberW, dmEdgeHash, dmTypeHash,
                sourceRoleHex, cpHashHex,
                targetRoleHex, targetHex,
                sb);
            dmEdgeCount++;
            memberCount += 2;
        }

        System.Console.WriteLine(
            $"  emitted {targetCount.ToString(CultureInfo.InvariantCulture)} decomposition target compositions, " +
            $"{dmEdgeCount.ToString(CultureInfo.InvariantCulture)} dm edges, " +
            $"{dtEdgeCount.ToString(CultureInfo.InvariantCulture)} dt edges, " +
            $"{memberCount.ToString(CultureInfo.InvariantCulture)} edge_member rows");
    }

    private static List<int> ParseCodepointList(string dm)
    {
        // dm is space-separated hex codepoints, e.g. "0041 0301".
        var result = new List<int>(8);
        var spans = dm.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in spans)
        {
            if (int.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
            {
                result.Add(cp);
            }
        }
        return result;
    }

    private static (
        List<AtomId> Children,
        List<int> Counts,
        byte[] ContentBytes,
        Point4D Centroid,
        List<Point4D> Trajectory)
        ComposeTargetSequence(
            List<int> targetCps,
            Dictionary<int, AtomId> codepointHashes,
            Dictionary<int, Point4D> positionByCp)
    {
        var children = new List<AtomId>(targetCps.Count);
        var counts   = new List<int>(targetCps.Count);
        var trajectory = new List<Point4D>(targetCps.Count);
        var contentBuf = new List<byte>(targetCps.Count * 4);

        int? prevCp = null;
        AtomId prevHash = default;
        var prevRun = 0;
        Point4D prevPos = default;

        foreach (var cp in targetCps)
        {
            if (!codepointHashes.TryGetValue(cp, out var hash)) { continue; }
            if (!positionByCp.TryGetValue(cp, out var pos)) { continue; }

            // Append UTF-8 bytes for content.
            var utf8 = SeedDbRowsEmitter.EncodeUtf8(cp);
            contentBuf.AddRange(utf8);

            if (prevCp.HasValue && prevCp.Value == cp)
            {
                prevRun++;
            }
            else
            {
                if (prevCp.HasValue)
                {
                    children.Add(prevHash);
                    counts.Add(prevRun);
                    trajectory.Add(prevPos);
                }
                prevCp = cp;
                prevHash = hash;
                prevPos = pos;
                prevRun = 1;
            }
        }
        if (prevCp.HasValue)
        {
            children.Add(prevHash);
            counts.Add(prevRun);
            trajectory.Add(prevPos);
        }

        // Vertex centroid in the 4-ball — average over RLE-weighted children.
        double sx = 0, sy = 0, sz = 0, sw = 0;
        var totalRune = 0;
        for (var i = 0; i < trajectory.Count; ++i)
        {
            sx += trajectory[i].X * counts[i];
            sy += trajectory[i].Y * counts[i];
            sz += trajectory[i].Z * counts[i];
            sw += trajectory[i].W * counts[i];
            totalRune += counts[i];
        }
        var centroid = totalRune > 0
            ? new Point4D(sx / totalRune, sy / totalRune, sz / totalRune, sw / totalRune)
            : new Point4D(0, 0, 0, 0);

        return (children, counts, contentBuf.ToArray(), centroid, trajectory);
    }

    private static void EmitTier1Row(
        StreamWriter w, AtomId hash, byte[] content, Point4D centroid, List<Point4D> trajectory,
        StringBuilder sb)
    {
        sb.Clear();
        sb.Append(@"\x").Append(SeedDbRowsEmitter.ToHexLower(hash.AsSpan())).Append('\t');
        sb.Append('1').Append('\t');
        sb.Append(@"\N").Append('\t');
        sb.Append(@"\x").Append(SeedDbRowsEmitter.ToHexLower(content)).Append('\t');
        sb.Append("POINT4D(")
          .Append(SeedDbRowsEmitter.FormatDouble(centroid.X)).Append(' ')
          .Append(SeedDbRowsEmitter.FormatDouble(centroid.Y)).Append(' ')
          .Append(SeedDbRowsEmitter.FormatDouble(centroid.Z)).Append(' ')
          .Append(SeedDbRowsEmitter.FormatDouble(centroid.W))
          .Append(')').Append('\t');

        sb.Append("LINESTRING4D(");
        for (var i = 0; i < trajectory.Count; ++i)
        {
            if (i > 0) { sb.Append(", "); }
            var p = trajectory[i];
            sb.Append(SeedDbRowsEmitter.FormatDouble(p.X)).Append(' ')
              .Append(SeedDbRowsEmitter.FormatDouble(p.Y)).Append(' ')
              .Append(SeedDbRowsEmitter.FormatDouble(p.Z)).Append(' ')
              .Append(SeedDbRowsEmitter.FormatDouble(p.W));
        }
        if (trajectory.Count < 2)
        {
            sb.Append(", ");
            var p = trajectory.Count == 1 ? trajectory[0] : new Point4D(0, 0, 0, 0);
            sb.Append(SeedDbRowsEmitter.FormatDouble(p.X)).Append(' ')
              .Append(SeedDbRowsEmitter.FormatDouble(p.Y)).Append(' ')
              .Append(SeedDbRowsEmitter.FormatDouble(p.Z)).Append(' ')
              .Append(SeedDbRowsEmitter.FormatDouble(p.W));
        }
        sb.Append(')').Append('\t');

        sb.Append(unchecked((long)PrimeFlags.Text).ToString(CultureInfo.InvariantCulture)).Append('\t');
        sb.Append('0');
        w.WriteLine(sb.ToString());
    }

    private static void EmitTier1ChildRows(
        StreamWriter w, AtomId parentHash, List<AtomId> children, List<int> counts,
        StringBuilder sb)
    {
        var parentHex = SeedDbRowsEmitter.ToHexLower(parentHash.AsSpan());
        for (var i = 0; i < children.Count; ++i)
        {
            sb.Clear();
            sb.Append(@"\x").Append(parentHex).Append('\t');
            sb.Append('1').Append('\t');
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(@"\x").Append(SeedDbRowsEmitter.ToHexLower(children[i].AsSpan())).Append('\t');
            sb.Append('0').Append('\t');
            sb.Append(counts[i].ToString(CultureInfo.InvariantCulture));
            w.WriteLine(sb.ToString());
        }
    }

    private static void EmitEdgeRow(StreamWriter w, AtomId edgeHash, AtomId edgeTypeHash, StringBuilder sb)
    {
        sb.Clear();
        sb.Append(@"\x").Append(SeedDbRowsEmitter.ToHexLower(edgeHash.AsSpan())).Append('\t');
        sb.Append(@"\x").Append(SeedDbRowsEmitter.ToHexLower(edgeTypeHash.AsSpan())).Append('\t');
        sb.Append('2');
        w.WriteLine(sb.ToString());
    }

    private static void EmitMemberRows(
        StreamWriter w, AtomId edgeHash, AtomId edgeTypeHash,
        string sourceRoleHex, string sourceParticipantHex,
        string targetRoleHex, string targetParticipantHex,
        StringBuilder sb)
    {
        var edgeHashHex     = SeedDbRowsEmitter.ToHexLower(edgeHash.AsSpan());
        var edgeTypeHashHex = SeedDbRowsEmitter.ToHexLower(edgeTypeHash.AsSpan());

        sb.Clear();
        sb.Append(@"\x").Append(edgeHashHex).Append('\t');
        sb.Append(@"\x").Append(edgeTypeHashHex).Append('\t');
        sb.Append(@"\x").Append(sourceRoleHex).Append('\t');
        sb.Append('0').Append('\t');
        sb.Append(@"\x").Append(sourceParticipantHex);
        w.WriteLine(sb.ToString());

        sb.Clear();
        sb.Append(@"\x").Append(edgeHashHex).Append('\t');
        sb.Append(@"\x").Append(edgeTypeHashHex).Append('\t');
        sb.Append(@"\x").Append(targetRoleHex).Append('\t');
        sb.Append('0').Append('\t');
        sb.Append(@"\x").Append(targetParticipantHex);
        w.WriteLine(sb.ToString());
    }
}
