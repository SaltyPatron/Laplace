using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Api.Contracts;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

// The computed geometric anchor for a surface, produced in-process by
// TextDecomposer + HashComposer against the t0 perfcache -- NO database. Every
// codepoint is pinned on S3 and the parent coord/trajectory are composed, so a
// word that was never witnessed (content hash resolves but exists=false) still
// has a fully determined position and shape. This is what the not-found explorer
// hands to structural.explore_anchor_neighbors as a bound anchor.
internal sealed record ExploreAnchor(
    string WordIdHex,
    double Cx, double Cy, double Cz, double Cm,
    string? TrajectoryWkt,
    IReadOnlyList<DecomposeNodeRow> Decomposition);

// A candidate surface that resolves to a witnessed word id.
internal sealed record WitnessedWord(string Surface, string IdHex, long Witnesses);

internal sealed class ExploreDecomposeService
{
    private readonly object _unicodeCloudGate = new();
    private string? _unicodeCloudReceipt;
    private byte[]? _unicodeCloudPositions;

    public DecomposeResponse Decompose(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        EnsurePerfcache();

        using var tree = TextDecomposer.Run(text);
        unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

        var utf8 = Encoding.UTF8.GetBytes(text);
        var root = tree.GetNode(tree.NaturalUnitIndex());
        var nodes = EmittedDecomposition(tree, utf8);

        return new DecomposeResponse(
            Text: text,
            RootIdHex: Convert.ToHexStringLower(root.Id.ToBytes()),
            NaturalUnitOrdinal: tree.NaturalUnitIndex(),
            Nodes: nodes);
    }

    public UnicodeCloudResponse UnicodeCloud()
    {
        EnsurePerfcache();
        int count = CodepointPerfcache.Count;
        return new UnicodeCloudResponse(
            count,
            CodepointPerfcache.ReceiptHex,
            "laplace.t0-unicode-cloud/f32xyz-two-lanes/v1",
            checked(count * 3 * sizeof(float) * 2));
    }

    public byte[] UnicodeCloudPositions()
    {
        EnsurePerfcache();
        string receipt = CodepointPerfcache.ReceiptHex;
        lock (_unicodeCloudGate)
        {
            if (_unicodeCloudPositions is not null
                && string.Equals(_unicodeCloudReceipt, receipt, StringComparison.Ordinal))
                return _unicodeCloudPositions;

            var records = CodepointPerfcache.Records;
            var payload = new byte[checked(records.Length * 3 * sizeof(float) * 2)];
            var values = MemoryMarshal.Cast<byte, float>(payload);
            const ulong Mask53 = (1UL << 53) - 1, Mask42 = (1UL << 42) - 1, Mask22 = (1UL << 22) - 1;
            for (var i = 0; i < records.Length; i++)
            {
                ref readonly var r = ref records[i];
                double len = Math.Sqrt(r.CoordX*r.CoordX + r.CoordY*r.CoordY + r.CoordZ*r.CoordZ);
                if (len <= double.Epsilon) len = 1;
                int p = i * 3;
                values[p] = (float)(r.CoordX / len);
                values[p+1] = (float)(r.CoordY / len);
                values[p+2] = (float)(r.CoordZ / len);

                ulong hi = r.Hash.Hi, lo = r.Hash.Lo;
                double hx = ((lo & Mask53) / (double)Mask53) * 2 - 1;
                ulong ybits = ((hi & Mask42) << 11) | ((lo >> 53) & 0x7ffUL);
                double hy = ((ybits & Mask53) / (double)Mask53) * 2 - 1;
                double hz = (((hi >> 42) & Mask22) / (double)Mask22) * 2 - 1;
                double hlen = Math.Sqrt(hx*hx + hy*hy + hz*hz);
                if (hlen <= double.Epsilon) hlen = 1;
                int h = records.Length * 3 + p;
                values[h] = (float)(hx / hlen);
                values[h+1] = (float)(hy / hlen);
                values[h+2] = (float)(hz / hlen);
            }

            _unicodeCloudReceipt = receipt;
            _unicodeCloudPositions = payload;
            return payload;
        }
    }

    public UnicodePointResponse UnicodePoint(uint codepoint)
    {
        EnsurePerfcache();
        var records = CodepointPerfcache.Records;
        if (codepoint >= (uint)records.Length) throw new ArgumentOutOfRangeException(nameof(codepoint));
        ref readonly var r = ref records[(int)codepoint];
        string display = Rune.IsValid((int)codepoint) ? new Rune((int)codepoint).ToString() : string.Empty;
        double radius = Math.Sqrt(r.CoordX*r.CoordX+r.CoordY*r.CoordY+r.CoordZ*r.CoordZ+r.CoordM*r.CoordM);
        return new UnicodePointResponse(codepoint, display, Convert.ToHexStringLower(r.Hash.ToBytes()),
            r.UcaOrder, r.CoordX, r.CoordY, r.CoordZ, r.CoordM, radius,
            Convert.ToHexStringLower(r.Hilbert.ToByteArray()), r.Flags);
    }

    /// <summary>
    /// Exact, database-independent storage proof for a text surface.  The same
    /// TextDecomposer + HashComposer kernels compute identity, 4-D placement and
    /// Hilbert locality; the same flagged-RLE trajectory builder emits the
    /// 212-bit-per-vertex carrier that Content witnessing writes.
    ///
    /// Packed vertices are identity cargo, never spatial positions.  Realized
    /// vertices are the immediate children's actual 4-D coordinates.
    /// </summary>
    public StorageProofResponse StorageProof(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        EnsurePerfcache();

        using var tree = TextDecomposer.Run(text);
        unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

        var utf8 = Encoding.UTF8.GetBytes(text);
        var naturalUnit = tree.NaturalUnitIndex();
        var root = tree.GetNode(naturalUnit);

        var emitted = new List<uint>(tree.NodeCount);
        var emittedSet = new HashSet<uint>();
        for (uint i = 0; i < tree.NodeCount; i++)
        {
            var node = tree.GetNode(i);
            if (node.Tier != 0 && !tree.ShouldEmitCompositional(i)) continue;
            emitted.Add(i);
            emittedSet.Add(i);
        }

        string Label(TierNodeView node) =>
            Encoding.UTF8.GetString(utf8, (int)node.TextRangeOff, (int)node.TextRangeLen);

        uint? EmittedParent(TierNodeView node)
        {
            var parent = node.ParentIdx;
            while (parent != TierTree.Invalid)
            {
                if (emittedSet.Contains(parent)) return parent;
                parent = tree.GetNode(parent).ParentIdx;
            }
            return null;
        }

        var rows = new List<StorageProofNodeRow>(emitted.Count);
        foreach (var index in emitted)
        {
            var node = tree.GetNode(index);
            double x, y, z, m;
            unsafe
            {
                x = node.Coord[0];
                y = node.Coord[1];
                z = node.Coord[2];
                m = node.Coord[3];
            }

            var packed = new List<StorageProofPackedVertexRow>();
            var realized = new List<StorageProofRealizedVertexRow>();

            if (node.Tier != 0 && node.ChildCount > 0)
            {
                var children = new List<(TierNodeView Node, string Label, ulong Flags)>((int)node.ChildCount);
                for (uint ci = 0; ci < node.ChildCount; ci++)
                {
                    var childIndex = tree.CollapseIndex(node.FirstChildIdx + ci);
                    var child = tree.GetNode(childIndex);
                    var childLabel = Label(child);
                    var childFlags = Trajectory.VertexFlags(
                        child.Tier, hasAtom: child.Tier == 0, atom: child.Atom);
                    children.Add((child, childLabel, childFlags));

                    double cx, cy, cz, cm;
                    unsafe
                    {
                        cx = child.Coord[0];
                        cy = child.Coord[1];
                        cz = child.Coord[2];
                        cm = child.Coord[3];
                    }
                    realized.Add(new StorageProofRealizedVertexRow(
                        Ordinal: checked((int)ci + 1),
                        ChildIdHex: Convert.ToHexStringLower(child.Id.ToBytes()),
                        ChildLabel: childLabel,
                        ChildTier: child.Tier,
                        X: cx, Y: cy, Z: cz, M: cm,
                        Radius: Math.Sqrt(cx * cx + cy * cy + cz * cz + cm * cm)));
                }

                var ids = children.Select(static child => child.Node.Id).ToArray();
                var flags = children.Select(static child => child.Flags).ToArray();
                var trajectory = Trajectory.Build(ids, flags);

                var vertex = 0;
                var childPosition = 0;
                while (childPosition < children.Count)
                {
                    var exemplar = children[childPosition];
                    var run = 1;
                    while (childPosition + run < children.Count
                           && children[childPosition + run].Node.Id == exemplar.Node.Id
                           && children[childPosition + run].Flags == exemplar.Flags)
                        run++;

                    var emittedInRun = 0;
                    while (emittedInRun < run)
                    {
                        var chunk = Math.Min(run - emittedInRun, ushort.MaxValue);
                        var offset = checked(vertex * 4);
                        packed.Add(new StorageProofPackedVertexRow(
                            Vertex: vertex + 1,
                            LogicalOrdinal: childPosition + emittedInRun + 1,
                            X: trajectory[offset],
                            Y: trajectory[offset + 1],
                            Z: trajectory[offset + 2],
                            M: trajectory[offset + 3],
                            ChildIdHex: Convert.ToHexStringLower(exemplar.Node.Id.ToBytes()),
                            ChildLabel: exemplar.Label,
                            ChildTier: exemplar.Node.Tier,
                            RunLength: chunk,
                            Flags: unchecked((long)exemplar.Flags)));
                        emittedInRun += chunk;
                        vertex++;
                    }

                    childPosition += run;
                }

                if (vertex * 4 != trajectory.Length)
                    throw new InvalidOperationException("Storage proof RLE metadata diverged from native trajectory output.");
            }

            uint? ducetRank = null;
            if (node.Tier == 0)
            {
                var records = CodepointPerfcache.Records;
                if (node.Atom >= (uint)records.Length)
                    throw new InvalidOperationException(
                        $"Tier-0 atom U+{node.Atom:X} is outside the published perfcache ROM.");

                ref readonly var atom = ref records[(int)node.Atom];
                if (node.Id != atom.Hash
                    || x != atom.CoordX || y != atom.CoordY
                    || z != atom.CoordZ || m != atom.CoordM
                    || node.Hilbert.CompareToBytewise(atom.Hilbert) != 0)
                    throw new InvalidOperationException(
                        $"Tier-0 node U+{node.Atom:X} diverged from the published perfcache ROM.");

                ducetRank = atom.UcaOrder;
            }

            rows.Add(new StorageProofNodeRow(
                Ordinal: index,
                ParentOrdinal: EmittedParent(node),
                IdHex: Convert.ToHexStringLower(node.Id.ToBytes()),
                Label: Label(node),
                Tier: node.Tier,
                Atom: node.Tier == 0 ? node.Atom : null,
                DucetRank: ducetRank,
                TextOffset: checked((int)node.TextRangeOff),
                TextLength: checked((int)node.TextRangeLen),
                X: x, Y: y, Z: z, M: m,
                Radius: Math.Sqrt(x * x + y * y + z * z + m * m),
                HilbertHex: Convert.ToHexStringLower(node.Hilbert.ToByteArray()),
                PackedVertices: packed,
                RealizedVertices: realized));
        }

        var invariantRows = new List<StorageProofInvariantRow>();
        var maxRadius = rows.Count == 0 ? 0.0 : rows.Max(static row => row.Radius);
        var tier0Rows = rows.Where(static row => row.Tier == 0).ToArray();
        var maxTier0RadiusError = tier0Rows.Length == 0
            ? 0.0
            : tier0Rows.Max(static row => Math.Abs(row.Radius - 1.0));

        bool trajectoryRoundTrip = true;
        int packedVertexCount = 0;
        int realizedVertexCount = 0;
        foreach (var row in rows)
        {
            packedVertexCount += row.PackedVertices.Count;
            realizedVertexCount += row.RealizedVertices.Count;
            if (row.PackedVertices.Count == 0) continue;

            var expanded = new List<string>();
            foreach (var vertex in row.PackedVertices)
                for (var repeat = 0; repeat < vertex.RunLength; repeat++)
                    expanded.Add(vertex.ChildIdHex);

            var realizedIds = row.RealizedVertices.Select(static vertex => vertex.ChildIdHex).ToArray();
            if (!expanded.SequenceEqual(realizedIds, StringComparer.Ordinal))
            {
                trajectoryRoundTrip = false;
                break;
            }
        }

        bool merkleRecomposition = true;
        int recomposedNodes = 0;
        foreach (var row in rows)
        {
            if (row.RealizedVertices.Count == 0) continue;
            var childIds = new Hash128[row.RealizedVertices.Count];
            var childCoords = new double[row.RealizedVertices.Count * 4];
            for (var i = 0; i < row.RealizedVertices.Count; i++)
            {
                var vertex = row.RealizedVertices[i];
                childIds[i] = Hash128.FromBytes(Convert.FromHexString(vertex.ChildIdHex));
                childCoords[i * 4] = vertex.X;
                childCoords[i * 4 + 1] = vertex.Y;
                childCoords[i * 4 + 2] = vertex.Z;
                childCoords[i * 4 + 3] = vertex.M;
            }

            Span<double> recomposedCoord = stackalloc double[4];
            var recomposed = HashComposer.ComposeNode(row.Tier, childIds, childCoords, recomposedCoord);
            recomposedNodes++;
            if (!Convert.ToHexStringLower(recomposed.Id.ToBytes()).Equals(row.IdHex, StringComparison.Ordinal)
                || recomposedCoord[0] != row.X || recomposedCoord[1] != row.Y
                || recomposedCoord[2] != row.Z || recomposedCoord[3] != row.M
                || !Convert.ToHexStringLower(recomposed.Hilbert.ToByteArray()).Equals(row.HilbertHex, StringComparison.Ordinal))
            {
                merkleRecomposition = false;
                break;
            }
        }

        invariantRows.Add(new StorageProofInvariantRow(
            "bounded_closure", "All emitted physicalities remain in the closed unit 4-ball",
            maxRadius <= 1.0 + 1e-12, $"max r4 = {maxRadius:R}"));
        invariantRows.Add(new StorageProofInvariantRow(
            "tier0_shell", "Observed Tier-0 atoms are on the S3 boundary",
            maxTier0RadiusError <= 1e-12,
            $"{tier0Rows.Length} leaves; max |r4-1| = {maxTier0RadiusError:R}"));
        invariantRows.Add(new StorageProofInvariantRow(
            "tier0_rom", "Observed Tier-0 identity, coordinate and Hilbert state equals the published ROM",
            true, $"{tier0Rows.Length} leaves checked byte/value-exact during proof construction"));
        invariantRows.Add(new StorageProofInvariantRow(
            "merkle_recomposition", "Ordered children deterministically recompose the emitted identity, centroid and Hilbert address",
            merkleRecomposition, $"{recomposedNodes} compositional nodes recomposed through HashComposer"));
        invariantRows.Add(new StorageProofInvariantRow(
            "trajectory_roundtrip", "Packed RLE constituent manifest expands to the realized child identity sequence",
            trajectoryRoundTrip,
            $"{packedVertexCount} packed vertices -> {realizedVertexCount} realized ordered vertices"));

        return new StorageProofResponse(
            Text: text,
            RootIdHex: Convert.ToHexStringLower(root.Id.ToBytes()),
            NaturalUnitOrdinal: naturalUnit,
            AtomWindow: UnicodeSeed.CodepointCount,
            PerfcacheReceiptHex: CodepointPerfcache.ReceiptHex,
            DatabasePerfcacheReceiptHex: null,
            DatabasePerfcacheError: null,
            PerfcacheAligned: null,
            Invariants: invariantRows,
            Nodes: rows);
    }

        // Compute the anchor for a surface: the natural-unit centroid coord + a
        // REALIZED grapheme-level curve WKT (LINESTRING ZM of child live coords),
        // plus the decomposition tree for display. This is the Frechet operand
        // (entity_curve shape), NOT the packed physicalities.trajectory manifest
        // (Rule #3). Prefers tier-1 grapheme coords; falls back to tier-0
        // codepoints; null for a degenerate <2-point curve (Frechet skipped,
        // geodesic still runs).
        public ExploreAnchor ComputeAnchor(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        EnsurePerfcache();

        using var tree = TextDecomposer.Run(text);
        unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

        var utf8 = Encoding.UTF8.GetBytes(text);
        var unitIdx = tree.NaturalUnitIndex();
        var unit = tree.GetNode(unitIdx);

        double cx, cy, cz, cm;
        unsafe { cx = unit.Coord[0]; cy = unit.Coord[1]; cz = unit.Coord[2]; cm = unit.Coord[3]; }

        var decomposition = EmittedDecomposition(tree, utf8);

        var wkt = BuildTrajectoryWkt(tree, tier: 1) ?? BuildTrajectoryWkt(tree, tier: 0);

        return new ExploreAnchor(
            WordIdHex: Convert.ToHexStringLower(unit.Id.ToBytes()),
            Cx: cx, Cy: cy, Cz: cz, Cm: cm,
            TrajectoryWkt: wkt,
            Decomposition: decomposition);
    }

    private static IReadOnlyList<DecomposeNodeRow> EmittedDecomposition(
        TierTree tree, byte[] utf8)
    {
        var rows = new List<DecomposeNodeRow>(tree.NodeCount);
        for (uint i = 0; i < tree.NodeCount; i++)
        {
            var n = tree.GetNode(i);
            // Tier is a floor. Single-child, span-identical wrappers collapse to
            // their child and are not stored substrate nodes. Showing those internal
            // parser frames made one word appear to contain itself at tiers 3 and 4
            // (and every one-codepoint grapheme appear twice), even though all rows
            // shared the same identity. Display exactly the nodes the content spine emits.
            if (n.Tier != 0 && !tree.ShouldEmitCompositional(i)) continue;
            rows.Add(new DecomposeNodeRow(
                Ordinal: i,
                IdHex: Convert.ToHexStringLower(n.Id.ToBytes()),
                Label: Encoding.UTF8.GetString(utf8, (int)n.TextRangeOff, (int)n.TextRangeLen),
                Tier: n.Tier,
                TextOffset: (int)n.TextRangeOff,
                TextLength: (int)n.TextRangeLen));
        }
        return rows;
    }

    private static string? BuildTrajectoryWkt(TierTree tree, byte tier)
    {
        var pts = new List<(uint Off, double X, double Y, double Z, double W)>();
        for (uint i = 0; i < tree.NodeCount; i++)
        {
            var n = tree.GetNode(i);
            if (n.Tier != tier) continue;
            double x, y, z, w;
            unsafe { x = n.Coord[0]; y = n.Coord[1]; z = n.Coord[2]; w = n.Coord[3]; }
            pts.Add((n.TextRangeOff, x, y, z, w));
        }
        if (pts.Count < 2) return null;
        pts.Sort((a, b) => a.Off.CompareTo(b.Off));

        var sb = new StringBuilder("LINESTRING ZM (");
        for (var i = 0; i < pts.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var p = pts[i];
            sb.Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(p.Y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(p.Z.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(p.W.ToString("R", CultureInfo.InvariantCulture));
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static void EnsurePerfcache()
    {
        // The shared initializer waits for publication and reuses the mapping.
        // A private once flag both published too early and reloaded/unmapped a
        // cache already being read by turn witnessing and native reverse lookup.
        CodepointPerfcache.LoadDefault();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int PerfcacheResolver(
        uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        var recs = CodepointPerfcache.Records;
        if (atom >= (uint)recs.Length) return -1;
        ref readonly var r = ref recs[(int)atom];
        *outId = r.Hash;
        outCoord[0] = r.CoordX;
        outCoord[1] = r.CoordY;
        outCoord[2] = r.CoordZ;
        outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
    }
}
