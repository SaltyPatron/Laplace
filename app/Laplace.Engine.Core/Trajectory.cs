namespace Laplace.Engine.Core;

/// <summary>
/// The substrate's content-trajectory packing: an entity's CONTENT
/// physicality records its ordered constituents as a mantissa-packed
/// LINESTRING — one vertex per constituent, each packing the constituent
/// entity's full 128-bit id + ordinal. <see cref="Build"/> packs ids → XYZM
/// doubles (the physicality trajectory) on the way in; <see cref="Constituents"/>
/// unpacks them on the way out. Lossless both ways; every component is a
/// finite normal double (PG-geometry-valid).
///
/// One trajectory holds ONE tier's direct children (a word's graphemes, a
/// sentence's words), not a flattened leaf list — fan-out per node stays
/// within the uint16 ordinal.
/// </summary>
public static unsafe class Trajectory
{
    /// <summary>Pack ordered constituent ids into a trajectory XYZM buffer
    /// (4 doubles per vertex). Empty input → empty buffer.</summary>
    public static double[] Build(ReadOnlySpan<Hash128> constituents)
    {
        var xyzm = new double[constituents.Length * 4];
        if (constituents.Length == 0) return xyzm;
        fixed (Hash128* h = constituents)
        fixed (double* o = xyzm)
        {
            int rc = NativeInterop.TrajectoryBuild(h, (nuint)constituents.Length, o);
            if (rc != 0) throw new InvalidOperationException($"trajectory_build returned {rc}");
        }
        return xyzm;
    }

    /// <summary>RLE-compressed variant: packs constituent ids into a trajectory
    /// XYZM buffer, collapsing consecutive identical ids into a single vertex
    /// with <c>run_length &gt; 1</c> in the M channel. Returns a
    /// buffer of length <c>vertexCount * 4</c> (≤ <c>constituents.Length * 4</c>).
    /// The original constituent count (for <c>NConstituents</c> in the
    /// physicality row) is <c>constituents.Length</c>.</summary>
    /// <summary>Vertex flag layout (mantissa.h, 2026-06-05): bit 0 HAS_ATOM,
    /// bits 1..5 constituent tier, bits 31..51 atom scalar (codepoint). In-band
    /// type/atom — renderers stop joining entities/codepoint_render per leaf.</summary>
    public const ulong VFlagHasAtom = 1UL;
    public const int VFlagTierShift = 1, VFlagAtomShift = 31;

    public static ulong VertexFlags(byte tier, bool hasAtom, uint atom)
    {
        ulong f = ((ulong)(tier & 0x1F)) << VFlagTierShift;
        if (hasAtom) f |= VFlagHasAtom | ((ulong)(atom & 0x1FFFFF)) << VFlagAtomShift;
        return f;
    }

    /// <summary>Build with per-constituent in-band flags (same order as ids).</summary>
    public static unsafe double[] Build(ReadOnlySpan<Hash128> constituents, ReadOnlySpan<ulong> flags)
    {
        if (flags.Length != constituents.Length)
            throw new ArgumentException("flags length must match constituents length");
        var xyzm = new double[constituents.Length * 4];
        fixed (Hash128* h = constituents)
        fixed (ulong* fl = flags)
        fixed (double* o = xyzm)
        {
            int rc = NativeInterop.TrajectoryBuildFlagged(h, fl, (nuint)constituents.Length, o);
            if (rc != 0) throw new InvalidOperationException($"trajectory_build_flagged returned {rc}");
        }
        return xyzm;
    }

    public static double[] BuildRle(ReadOnlySpan<Hash128> constituents)
    {
        if (constituents.Length == 0) return [];
        var xyzm = new double[constituents.Length * 4];
        nuint vertexCount;
        fixed (Hash128* h = constituents)
        fixed (double*  o = xyzm)
        {
            int rc = NativeInterop.TrajectoryBuildRle(h, (nuint)constituents.Length, o, &vertexCount);
            if (rc != 0) throw new InvalidOperationException($"trajectory_build_rle returned {rc}");
        }
        int vc = checked((int)vertexCount);
        if (vc < constituents.Length)
        {
            var trimmed = new double[vc * 4];
            Array.Copy(xyzm, trimmed, vc * 4);
            return trimmed;
        }
        return xyzm;
    }

    /// <summary>Unpack a trajectory XYZM buffer back to its ordered constituent
    /// ids (the vertex order is the sequence).</summary>
    public static Hash128[] Constituents(ReadOnlySpan<double> xyzm)
    {
        int n = xyzm.Length / 4;
        var outH = new Hash128[n];
        if (n == 0) return outH;
        fixed (double* x = xyzm)
        fixed (Hash128* o = outH)
        {
            int rc = NativeInterop.TrajectoryConstituents(x, (nuint)n, o, (nuint)n);
            if (rc < 0) throw new InvalidOperationException($"trajectory_constituents returned {rc}");
        }
        return outH;
    }
}
