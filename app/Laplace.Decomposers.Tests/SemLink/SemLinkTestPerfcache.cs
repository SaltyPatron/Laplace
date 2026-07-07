using Laplace.Engine.Core;

namespace Laplace.Decomposers.SemLink.Tests;

internal static class SemLinkTestPerfcache
{
    internal static void Load()
    {
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
