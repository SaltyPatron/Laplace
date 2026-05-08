namespace Laplace.Substrate.Tests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using Laplace.Core.Abstractions;

/// <summary>
/// xUnit collection fixture loading the generated tier-0 substrate seed
/// (entity_tier0.tsv + physicality_atoms.tsv) into memory ONCE so each
/// invariant test class operates on the same data without re-parsing.
///
/// Path resolution: walks upward from the test bin output looking for
/// `ext/laplace_pg/generated/`. Tests that depend on the generator output
/// skip if the directory isn't present (the generator is env-gated to
/// D:\Models\UCD\... which not every machine has).
/// </summary>
public sealed class GeneratedSubstrateFixture
{
    public bool IsAvailable { get; }
    public string GeneratedDir { get; } = string.Empty;

    public IReadOnlyList<TierZeroRow> Atoms { get; } = Array.Empty<TierZeroRow>();
    public IReadOnlyList<PhysicalityRow> Physicality { get; } = Array.Empty<PhysicalityRow>();
    public IReadOnlyList<TierOneRow> Concepts { get; } = Array.Empty<TierOneRow>();
    public IReadOnlyList<EntityChildRow> EntityChildren { get; } = Array.Empty<EntityChildRow>();
    public string EdgeTsvPath { get; } = string.Empty;
    public string EdgeMemberTsvPath { get; } = string.Empty;
    public long EdgeRowCount { get; }
    public long EdgeMemberRowCount { get; }

    /// <summary>
    /// Per-row tier-0 record: the fields invariant tests need to assert
    /// substrate properties. Fields not needed by tests (content bytes,
    /// trajectory, structural_flags) are dropped to keep the load under
    /// ~200 MB working set for 1.114M rows.
    /// </summary>
    public readonly record struct TierZeroRow(
        byte[] EntityHash,
        int Codepoint,
        double X, double Y, double Z, double W,
        long PrimeFlags);

    public readonly record struct PhysicalityRow(
        byte[] PhysicalityTypeHash,
        byte[] EntityHash,
        double X, double Y, double Z, double W,
        long HilbertIndex);

    public readonly record struct TierOneRow(
        byte[] EntityHash,
        byte[] Content,
        double CentroidX, double CentroidY, double CentroidZ, double CentroidW,
        IReadOnlyList<(double X, double Y, double Z, double W)> TrajectoryVertices,
        long PrimeFlags);

    public readonly record struct EntityChildRow(
        byte[] ParentHash,
        short ParentTier,
        int Position,
        byte[] ChildHash,
        short ChildTier,
        int RleCount);

    public readonly record struct EdgeRow(
        byte[] EdgeHash,
        byte[] EdgeTypeHash,
        short MemberCount);

    public readonly record struct EdgeMemberRow(
        byte[] EdgeHash,
        byte[] EdgeTypeHash,
        byte[] RoleHash,
        short RolePosition,
        byte[] ParticipantHash);

    public GeneratedSubstrateFixture()
    {
        var dir = ResolveGeneratedDir();
        var atomTsv         = dir is null ? null : Path.Combine(dir, "entity_tier0.tsv");
        var physicalityTsv  = dir is null ? null : Path.Combine(dir, "physicality_atoms.tsv");

        if (dir is null || !File.Exists(atomTsv!) || !File.Exists(physicalityTsv!))
        {
            IsAvailable = false;
            return;
        }

        GeneratedDir = dir;
        IsAvailable  = true;

        var atoms        = new List<TierZeroRow>(capacity: 1_200_000);
        var physicality  = new List<PhysicalityRow>(capacity: 1_200_000);

        foreach (var row in ParseTier0(atomTsv!))      { atoms.Add(row); }
        foreach (var row in ParsePhysicality(physicalityTsv!)) { physicality.Add(row); }

        Atoms       = atoms;
        Physicality = physicality;

        var tier1Tsv  = Path.Combine(dir, "entity_tier1.tsv");
        var childTsv  = Path.Combine(dir, "entity_child.tsv");

        var concepts = new List<TierOneRow>();
        if (File.Exists(tier1Tsv))
        {
            foreach (var row in ParseTier1(tier1Tsv)) { concepts.Add(row); }
        }
        Concepts = concepts;

        var children = new List<EntityChildRow>();
        if (File.Exists(childTsv))
        {
            foreach (var row in ParseEntityChild(childTsv)) { children.Add(row); }
        }
        EntityChildren = children;

        // Edges + edge_members are too large (~1.5 GB combined) to retain in
        // memory. Tests stream them on demand. Count rows once via newline
        // count for cardinality assertions.
        var edgeTsv  = Path.Combine(dir, "edge.tsv");
        var memberTsv = Path.Combine(dir, "edge_member.tsv");
        if (File.Exists(edgeTsv))
        {
            EdgeTsvPath  = edgeTsv;
            EdgeRowCount = CountLines(edgeTsv);
        }
        if (File.Exists(memberTsv))
        {
            EdgeMemberTsvPath  = memberTsv;
            EdgeMemberRowCount = CountLines(memberTsv);
        }
    }

    public IEnumerable<EdgeRow> StreamEdges()
    {
        if (string.IsNullOrEmpty(EdgeTsvPath)) { yield break; }
        using var r = new StreamReader(EdgeTsvPath);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            var fields = SplitTabs(line, 3);
            if (fields.Length != 3) { continue; }
            yield return new EdgeRow(
                HexDecode(fields[0].AsSpan(2)),
                HexDecode(fields[1].AsSpan(2)),
                short.Parse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture));
        }
    }

    public IEnumerable<EdgeMemberRow> StreamEdgeMembers()
    {
        if (string.IsNullOrEmpty(EdgeMemberTsvPath)) { yield break; }
        using var r = new StreamReader(EdgeMemberTsvPath);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            var fields = SplitTabs(line, 5);
            if (fields.Length != 5) { continue; }
            yield return new EdgeMemberRow(
                HexDecode(fields[0].AsSpan(2)),
                HexDecode(fields[1].AsSpan(2)),
                HexDecode(fields[2].AsSpan(2)),
                short.Parse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture),
                HexDecode(fields[4].AsSpan(2)));
        }
    }

    private static long CountLines(string path)
    {
        long n = 0;
        using var r = new StreamReader(path);
        while (r.ReadLine() != null) { n++; }
        return n;
    }

    private static IEnumerable<TierOneRow> ParseTier1(string path)
    {
        using var r = new StreamReader(path);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            // 8 cols: hash, tier=1, codepoint=\N, content, centroid POINT4D, trajectory LINESTRING4D, prime, structural
            var fields = SplitTabs(line, 8);
            if (fields.Length != 8) { continue; }

            var hash    = HexDecode(fields[0].AsSpan(2));
            var content = HexDecode(fields[3].AsSpan(2));
            var (cx, cy, cz, cw) = ParsePoint4D(fields[4].AsSpan());
            var traj    = ParseLineString4D(fields[5].AsSpan());
            var prime   = long.Parse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture);

            yield return new TierOneRow(hash, content, cx, cy, cz, cw, traj, prime);
        }
    }

    private static IEnumerable<EntityChildRow> ParseEntityChild(string path)
    {
        using var r = new StreamReader(path);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            // 6 cols: parent_hash, parent_tier, position, child_hash, child_tier, rle_count
            var fields = SplitTabs(line, 6);
            if (fields.Length != 6) { continue; }

            var parentHash = HexDecode(fields[0].AsSpan(2));
            var parentTier = short.Parse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
            var position   = int.Parse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture);
            var childHash  = HexDecode(fields[3].AsSpan(2));
            var childTier  = short.Parse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture);
            var rle        = int.Parse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture);

            yield return new EntityChildRow(parentHash, parentTier, position, childHash, childTier, rle);
        }
    }

    private static string[] SplitTabs(string line, int expected)
    {
        var result = new string[expected];
        var idx = 0;
        var start = 0;
        for (var i = 0; i < line.Length && idx < expected - 1; ++i)
        {
            if (line[i] == '\t')
            {
                result[idx++] = line.Substring(start, i - start);
                start = i + 1;
            }
        }
        result[idx] = line[start..];
        return idx == expected - 1 ? result : Array.Empty<string>();
    }

    private static List<(double X, double Y, double Z, double W)> ParseLineString4D(ReadOnlySpan<char> s)
    {
        var open  = s.IndexOf('(');
        var close = s.LastIndexOf(')');
        var inner = s[(open + 1)..close];

        var verts = new List<(double, double, double, double)>();
        var cursor = 0;
        while (cursor < inner.Length)
        {
            var commaRel = inner[cursor..].IndexOf(',');
            ReadOnlySpan<char> chunk = commaRel < 0 ? inner[cursor..] : inner.Slice(cursor, commaRel);
            chunk = chunk.Trim();
            if (chunk.Length > 0)
            {
                var p1 = chunk.IndexOf(' ');
                var rest1 = chunk[(p1 + 1)..];
                var p2 = rest1.IndexOf(' ');
                var rest2 = rest1[(p2 + 1)..];
                var p3 = rest2.IndexOf(' ');
                var x = double.Parse(chunk[..p1], NumberStyles.Float, CultureInfo.InvariantCulture);
                var y = double.Parse(rest1[..p2], NumberStyles.Float, CultureInfo.InvariantCulture);
                var z = double.Parse(rest2[..p3], NumberStyles.Float, CultureInfo.InvariantCulture);
                var w = double.Parse(rest2[(p3 + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture);
                verts.Add((x, y, z, w));
            }
            if (commaRel < 0) { break; }
            cursor += commaRel + 1;
        }
        return verts;
    }

    private static IEnumerable<TierZeroRow> ParseTier0(string path)
    {
        using var r = new StreamReader(path);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            // 8 columns: hash \t tier \t cp \t content \t POINT4D \t \N \t prime \t structural
            var f1 = line.IndexOf('\t');
            var f2 = line.IndexOf('\t', f1 + 1);
            var f3 = line.IndexOf('\t', f2 + 1);
            var f4 = line.IndexOf('\t', f3 + 1);
            var f5 = line.IndexOf('\t', f4 + 1);
            var f6 = line.IndexOf('\t', f5 + 1);
            var f7 = line.IndexOf('\t', f6 + 1);
            if (f1 < 0 || f2 < 0 || f3 < 0 || f4 < 0 || f5 < 0 || f6 < 0 || f7 < 0)
            {
                continue;
            }

            var hashSpan      = line.AsSpan(2, f1 - 2); // skip leading "\x"
            var cpSpan        = line.AsSpan(f2 + 1, f3 - f2 - 1);
            var positionSpan  = line.AsSpan(f4 + 1, f5 - f4 - 1);
            var primeSpan     = line.AsSpan(f6 + 1, f7 - f6 - 1);

            var hash  = HexDecode(hashSpan);
            var cp    = int.Parse(cpSpan, NumberStyles.Integer, CultureInfo.InvariantCulture);
            var (x, y, z, w) = ParsePoint4D(positionSpan);
            var prime = long.Parse(primeSpan, NumberStyles.Integer, CultureInfo.InvariantCulture);

            yield return new TierZeroRow(hash, cp, x, y, z, w, prime);
        }
    }

    private static IEnumerable<PhysicalityRow> ParsePhysicality(string path)
    {
        using var r = new StreamReader(path);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            // 6 columns: ptype \t entity \t tier \t POINT4D \t \N \t hilbert
            var f1 = line.IndexOf('\t');
            var f2 = line.IndexOf('\t', f1 + 1);
            var f3 = line.IndexOf('\t', f2 + 1);
            var f4 = line.IndexOf('\t', f3 + 1);
            var f5 = line.IndexOf('\t', f4 + 1);
            if (f1 < 0 || f2 < 0 || f3 < 0 || f4 < 0 || f5 < 0)
            {
                continue;
            }

            var pHashSpan      = line.AsSpan(2, f1 - 2);
            var entityHashSpan = line.AsSpan(f1 + 3, f2 - f1 - 3); // skip "\x"
            var positionSpan   = line.AsSpan(f3 + 1, f4 - f3 - 1);
            var hilbertSpan    = line.AsSpan(f5 + 1);

            var pHash      = HexDecode(pHashSpan);
            var entityHash = HexDecode(entityHashSpan);
            var (x, y, z, w) = ParsePoint4D(positionSpan);
            var hilbert    = long.Parse(hilbertSpan, NumberStyles.Integer, CultureInfo.InvariantCulture);

            yield return new PhysicalityRow(pHash, entityHash, x, y, z, w, hilbert);
        }
    }

    private static (double X, double Y, double Z, double W) ParsePoint4D(ReadOnlySpan<char> s)
    {
        // Format: POINT4D(x y z w)
        var open  = s.IndexOf('(');
        var close = s.IndexOf(')');
        var inner = s[(open + 1)..close];

        var p1Rel  = inner.IndexOf(' ');
        var rest1  = inner[(p1Rel + 1)..];
        var p2Rel  = rest1.IndexOf(' ');
        var rest2  = rest1[(p2Rel + 1)..];
        var p3Rel  = rest2.IndexOf(' ');

        var xSpan = inner[..p1Rel];
        var ySpan = rest1[..p2Rel];
        var zSpan = rest2[..p3Rel];
        var wSpan = rest2[(p3Rel + 1)..];

        var x = double.Parse(xSpan, NumberStyles.Float, CultureInfo.InvariantCulture);
        var y = double.Parse(ySpan, NumberStyles.Float, CultureInfo.InvariantCulture);
        var z = double.Parse(zSpan, NumberStyles.Float, CultureInfo.InvariantCulture);
        var w = double.Parse(wSpan, NumberStyles.Float, CultureInfo.InvariantCulture);
        return (x, y, z, w);
    }

    private static byte[] HexDecode(ReadOnlySpan<char> hex)
    {
        var n = hex.Length / 2;
        var result = new byte[n];
        for (var i = 0; i < n; ++i)
        {
            result[i] = byte.Parse(hex.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static string? ResolveGeneratedDir()
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 12; ++i)
        {
            var candidate = Path.Combine(probe, "ext", "laplace_pg", "generated");
            if (Directory.Exists(candidate)) { return candidate; }
            var parent = Path.GetDirectoryName(probe);
            if (parent is null || parent == probe) { break; }
            probe = parent;
        }
        return null;
    }
}

[Xunit.CollectionDefinition("GeneratedSubstrate")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit collection-fixture naming convention requires the 'Collection' suffix.")]
public sealed class GeneratedSubstrateCollection : Xunit.ICollectionFixture<GeneratedSubstrateFixture>
{
}
