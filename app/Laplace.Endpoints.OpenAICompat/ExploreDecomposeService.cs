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
