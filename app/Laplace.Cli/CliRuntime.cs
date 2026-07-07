using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;

namespace Laplace.Cli;

internal static class CliRuntime
{
    public static string ConnString => LaplaceInstall.PostgresConnectionString();

    public static int Fail(string m) { Console.Error.WriteLine(m); return 2; }

    public static string Hex(Hash128 h) => Convert.ToHexString(h.ToBytes()).ToLowerInvariant();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe int PerfcacheResolver(
        uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        var recs = CodepointPerfcache.Records;
        if (atom >= (uint)recs.Length) return -1;
        ref readonly var r = ref recs[(int)atom];
        *outId = r.Hash;
        outCoord[0] = r.CoordX; outCoord[1] = r.CoordY; outCoord[2] = r.CoordZ; outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
    }

    public static string ResolveBlob() => LaplaceInstall.ResolveT0Perfcache();
}
