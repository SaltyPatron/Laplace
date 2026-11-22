using System.Runtime.InteropServices;
using Laplace.Native;

namespace Laplace.Engine.Core;

public static unsafe class Trajectory
{
    public const ulong VFlagHasAtom = 1UL;
    public const int VFlagTierShift = 1;
    public const int VFlagAtomShift = 31;
    public const int VFlagTierExtensionShift = 43;
    public const ulong VFlagTierExtension = 1UL << 46;

    private const ulong ExponentMinusTwoBits = 1021UL << 52;
    private const ulong RunMask = (1UL << 20) - 1;
    private const int MaxRunChunk = ushort.MaxValue;

    public static ulong VertexFlags(byte tier, bool hasAtom, uint atom)
    {
        ulong f = ((ulong)(tier & 0x1F)) << VFlagTierShift;
        if (hasAtom) f |= VFlagHasAtom | ((ulong)(atom & 0x1FFFFF)) << VFlagAtomShift;
        else if (tier > 0x1F)
            f |= VFlagTierExtension | ((ulong)(tier >> 5) & 0x7UL) << VFlagTierExtensionShift;
        return f;
    }

    public static double[] Build(ReadOnlySpan<Hash128> constituents) => BuildRle(constituents);

    public static double[] BuildRle(ReadOnlySpan<Hash128> constituents) =>
        BuildCore(constituents, ReadOnlySpan<ulong>.Empty);

    public static double[] Build(ReadOnlySpan<Hash128> constituents, ReadOnlySpan<ulong> flags)
    {
        if (flags.Length != constituents.Length)
            throw new ArgumentException("flags length must match constituents length");
        return BuildCore(constituents, flags);
    }

    private static double[] BuildCore(ReadOnlySpan<Hash128> constituents, ReadOnlySpan<ulong> flags)
    {
        if (constituents.Length == 0) return [];
        bool hasFlags = !flags.IsEmpty;

        var xyzm = new List<double>(constituents.Length * 4);
        double* xyz3 = stackalloc double[3];
        int i = 0;
        while (i < constituents.Length)
        {
            Hash128 id = constituents[i];
            ulong f = hasFlags ? flags[i] : 0UL;
            int run = 1;
            while (i + run < constituents.Length
                   && constituents[i + run] == id
                   && (!hasFlags || flags[i + run] == f))
                run++;

            lp_id nid = id.ToNative();
            LaplaceNative.lp_id_to_xyz(&nid, xyz3);

            ulong flagTag = (f ^ (f >> 24)) & 0xFFFFFFFFUL;
            int rem = run;
            while (rem > 0)
            {
                int chunk = Math.Min(rem, MaxRunChunk);
                ulong mbits = ExponentMinusTwoBits | ((uint)chunk & RunMask) | ((flagTag & 0xFFFFFFFFUL) << 20);
                xyzm.Add(xyz3[0]);
                xyzm.Add(xyz3[1]);
                xyzm.Add(xyz3[2]);
                xyzm.Add(BitConverter.UInt64BitsToDouble(mbits));
                rem -= chunk;
            }
            i += run;
        }
        return xyzm.ToArray();
    }

    public static Hash128 ContentIdentity(ReadOnlySpan<double> xyzm, out int constituentCount)
    {
        if (xyzm.Length == 0 || xyzm.Length % 4 != 0)
            throw new ArgumentException("content trajectory must contain one or more XYZM vertex groups", nameof(xyzm));
        var expanded = Constituents(xyzm);
        constituentCount = expanded.Length;
        return Hash128.Merkle(0, expanded);
    }

    public static Hash128[] Constituents(ReadOnlySpan<double> xyzm)
    {
        if (xyzm.Length % 4 != 0)
            throw new ArgumentException("trajectory must contain XYZM vertex groups", nameof(xyzm));
        int nv = xyzm.Length / 4;
        if (nv == 0) return [];

        var result = new List<Hash128>(nv);
        double* xyz3 = stackalloc double[3];
        lp_id nid;
        for (int v = 0; v < nv; v++)
        {
            int o = v * 4;
            xyz3[0] = xyzm[o];
            xyz3[1] = xyzm[o + 1];
            xyz3[2] = xyzm[o + 2];
            LaplaceNative.lp_xyz_to_id(xyz3, &nid);
            var id = Hash128.FromNative(nid);

            ulong mbits = BitConverter.DoubleToUInt64Bits(xyzm[o + 3]);
            int run = checked((int)(mbits & RunMask));
            if (run <= 0) run = 1;
            for (int r = 0; r < run; r++)
                result.Add(id);
        }
        return result.ToArray();
    }
}
