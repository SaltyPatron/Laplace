namespace Laplace.Engine.Core;

public static unsafe class Trajectory
{
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

    public const ulong VFlagHasAtom = 1UL;
    public const int VFlagTierShift = 1, VFlagAtomShift = 31;

    public static ulong VertexFlags(byte tier, bool hasAtom, uint atom)
    {
        ulong f = ((ulong)(tier & 0x1F)) << VFlagTierShift;
        if (hasAtom) f |= VFlagHasAtom | ((ulong)(atom & 0x1FFFFF)) << VFlagAtomShift;
        return f;
    }

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
        fixed (double* o = xyzm)
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
