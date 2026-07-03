using Laplace.Engine.Core;

namespace Laplace.Decomposers.SemLink.Tests;

internal static class SemLinkTestPerfcache
{
    internal static void Load()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            CodepointPerfcache.Load(env);
            return;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                string? hit = Directory.EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin",
                                                        SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null)
                {
                    CodepointPerfcache.Load(hit);
                    return;
                }
            }
        }

        throw new InvalidOperationException(
            "perf-cache blob not found; build the engine or set LAPLACE_PERFCACHE_BIN.");
    }
}
