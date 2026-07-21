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
// hands to laplace.explore_anchor_neighbors as a bound anchor.
internal sealed record ExploreAnchor(
    string WordIdHex,
    double Cx, double Cy, double Cz, double Cm,
    string? TrajectoryWkt,
    IReadOnlyList<DecomposeNodeRow> Decomposition);

// A candidate surface that resolves to a witnessed word id.
internal sealed record WitnessedWord(string Surface, string IdHex, long Witnesses);

internal sealed class ExploreDecomposeService
{
    private static int _perfcacheLoaded;

    public DecomposeResponse Decompose(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        EnsurePerfcache();

        using var tree = TextDecomposer.Run(text);
        unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

        var utf8 = Encoding.UTF8.GetBytes(text);
        var root = tree.GetNode(tree.NaturalUnitIndex());
        var nodes = new List<DecomposeNodeRow>((int)tree.NodeCount);

        for (uint i = 0; i < tree.NodeCount; i++)
        {
            var n = tree.GetNode(i);
            nodes.Add(new DecomposeNodeRow(
                Ordinal: i,
                IdHex: Convert.ToHexStringLower(n.Id.ToBytes()),
                Label: Encoding.UTF8.GetString(utf8, (int)n.TextRangeOff, (int)n.TextRangeLen),
                Tier: n.Tier,
                TextOffset: (int)n.TextRangeOff,
                TextLength: (int)n.TextRangeLen));
        }

        return new DecomposeResponse(
            Text: text,
            RootIdHex: Convert.ToHexStringLower(root.Id.ToBytes()),
            NaturalUnitOrdinal: tree.NaturalUnitIndex(),
            Nodes: nodes);
    }

    // Compute the anchor for a surface: the natural-unit centroid coord + the
    // grapheme-level trajectory WKT (LINESTRING ZM), plus the decomposition tree
    // for display. Trajectory prefers tier-1 (grapheme) coords to match how
    // witnessed words store physicalities.trajectory (type=1); falls back to
    // tier-0 codepoints, and is null for a degenerate <2-point curve (Frechet
    // then skipped, geodesic still runs).
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

        var decomposition = new List<DecomposeNodeRow>((int)tree.NodeCount);
        for (uint i = 0; i < tree.NodeCount; i++)
        {
            var n = tree.GetNode(i);
            decomposition.Add(new DecomposeNodeRow(
                Ordinal: i,
                IdHex: Convert.ToHexStringLower(n.Id.ToBytes()),
                Label: Encoding.UTF8.GetString(utf8, (int)n.TextRangeOff, (int)n.TextRangeLen),
                Tier: n.Tier,
                TextOffset: (int)n.TextRangeOff,
                TextLength: (int)n.TextRangeLen));
        }

        var wkt = BuildTrajectoryWkt(tree, tier: 1) ?? BuildTrajectoryWkt(tree, tier: 0);

        return new ExploreAnchor(
            WordIdHex: Convert.ToHexStringLower(unit.Id.ToBytes()),
            Cx: cx, Cy: cy, Cz: cz, Cm: cm,
            TrajectoryWkt: wkt,
            Decomposition: decomposition);
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
        if (Interlocked.CompareExchange(ref _perfcacheLoaded, 1, 0) != 0)
            return;
        CodepointPerfcache.Load(LaplaceInstall.ResolveT0Perfcache());
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
