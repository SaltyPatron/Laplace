using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Laplace.Decomposers.Composition;
using Laplace.Engine.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Laplace.Cli;

internal static class CliRuntime
{
    private static IServiceProvider? _services;

    /// <summary>CLI composition root — built once at process start.</summary>
    public static IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("CliRuntime.Services not initialized");

    public static ISeedDecomposerResolver Decomposers =>
        Services.GetRequiredService<ISeedDecomposerResolver>();

    public static void InitializeServices()
    {
        if (_services is not null) return;
        var sc = new ServiceCollection();
        sc.AddLaplaceSeedIngest();
        _services = sc.BuildServiceProvider();
    }

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
