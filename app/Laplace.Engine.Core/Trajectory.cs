namespace Laplace.Engine.Core;

/// <summary>
/// The substrate's content-storage codec (ADR 0012): an entity's CONTENT
/// physicality records its ordered constituents as a mantissa-packed
/// LINESTRING — one vertex per constituent, each packing the constituent
/// entity's full 128-bit id + ordinal. <see cref="Build"/> packs ids → XYZM
/// doubles (the physicality trajectory) on the way in; <see cref="Constituents"/>
/// unpacks them on the way out. Round-trip lossless; every component is a
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
