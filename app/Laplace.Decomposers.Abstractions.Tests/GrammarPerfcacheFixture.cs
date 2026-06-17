using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;


public sealed class GrammarPerfcacheFixture : IDisposable
{
    public GrammarPerfcacheFixture()
    {
        var blob = LocateBlob() ?? throw new InvalidOperationException(
            "T0 perfcache blob not found. Set LAPLACE_PERFCACHE_BIN or build the engine " +
            "(target laplace_t0_perfcache).");
        CodepointPerfcache.Load(blob);
    }

    public void Dispose() => CodepointPerfcache.Unload();

    private static string? LocateBlob()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory
                    .EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
        return null;
    }
}

[CollectionDefinition("GrammarPerfcache")]
public sealed class GrammarPerfcacheCollection : ICollectionFixture<GrammarPerfcacheFixture> { }
