using Laplace.Engine.Core;

namespace Laplace.Decomposers.SemLink.Tests;

internal static class SemLinkTestPerfcache
{
    internal static void Load()
    {
        // Process-global native state: skip the mmap+CRC re-load when another class loaded it.
        if (CodepointPerfcache.IsLoaded) return;
        try
        {
            CodepointPerfcache.Load(LaplaceInstall.ResolveT0Perfcache());
            return;
        }
        catch (InvalidOperationException) { }

        throw new InvalidOperationException(
            "perf-cache blob not found; build the engine (laplace_t0_perfcache.bin).");
    }
}
