namespace Laplace.Engine.Core;

public static unsafe class Trajectory
{
    public static double[] Build(ReadOnlySpan<Hash128> constituents)
    {
        return BuildRle(constituents);
    }

    public const ulong VFlagHasAtom = 1UL;
    public const int VFlagTierShift = 1, VFlagAtomShift = 31;
    public const int VFlagTierExtensionShift = 43;
    public const ulong VFlagTierExtension = 1UL << 46;

    public static ulong VertexFlags(byte tier, bool hasAtom, uint atom)
    {
        ulong f = ((ulong)(tier & 0x1F)) << VFlagTierShift;
        if (hasAtom) f |= VFlagHasAtom | ((ulong)(atom & 0x1FFFFF)) << VFlagAtomShift;
        else if (tier > 0x1F)
            f |= VFlagTierExtension | ((ulong)(tier >> 5) & 0x7UL) << VFlagTierExtensionShift;
        return f;
    }

    public static unsafe double[] Build(ReadOnlySpan<Hash128> constituents, ReadOnlySpan<ulong> flags)
    {
        if (flags.Length != constituents.Length)
            throw new ArgumentException("flags length must match constituents length");
        if (constituents.Length == 0) return [];
        var xyzm = new double[constituents.Length * 4];
        nuint vertexCount;
        fixed (Hash128* h = constituents)
        fixed (ulong* fl = flags)
        fixed (double* o = xyzm)
        {
            int rc = NativeInterop.TrajectoryBuildFlaggedRle(
                h, fl, (nuint)constituents.Length, o, &vertexCount);
            if (rc != 0) throw new InvalidOperationException($"trajectory_build_flagged_rle returned {rc}");
        }
        return Trim(xyzm, checked((int)vertexCount));
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
        return Trim(xyzm, checked((int)vertexCount));
    }

    public static Hash128[] Constituents(ReadOnlySpan<double> xyzm)
    {
        if (xyzm.Length % 4 != 0)
            throw new ArgumentException("trajectory must contain XY ZM vertex groups", nameof(xyzm));
        int n = xyzm.Length / 4;
        if (n == 0) return [];
        nuint count;
        fixed (double* x = xyzm)
        {
            int rc = NativeInterop.TrajectoryConstituentCount(x, (nuint)n, &count);
            if (rc != 0) throw new InvalidOperationException($"trajectory_constituent_count returned {rc}");
        }
        var outH = new Hash128[checked((int)count)];
        fixed (double* x = xyzm)
        fixed (Hash128* o = outH)
        {
            int rc = NativeInterop.TrajectoryConstituents(x, (nuint)n, o, count);
            if (rc != outH.Length) throw new InvalidOperationException($"trajectory_constituents returned {rc}");
        }
        return outH;
    }

    private static double[] Trim(double[] xyzm, int vertices)
    {
        if (vertices == xyzm.Length / 4) return xyzm;
        var trimmed = new double[checked(vertices * 4)];
        Array.Copy(xyzm, trimmed, trimmed.Length);
        return trimmed;
    }
}
