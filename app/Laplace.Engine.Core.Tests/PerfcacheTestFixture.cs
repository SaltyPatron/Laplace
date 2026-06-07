using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class PerfcacheTestFixture : IDisposable
{
    public string BlobPath { get; }

    public PerfcacheTestFixture()
    {
        BlobPath = LocateBlob()
            ?? throw new InvalidOperationException(
                "T0 perf-cache blob not found. Build it (`just build`, or the engine " +
                "target `laplace_t0_perfcache`) or set LAPLACE_PERFCACHE_BIN. Looked at: " +
                "$LAPLACE_PERFCACHE_BIN, /opt/laplace/share/laplace/, and build*/**/perfcache/.");
        CodepointPerfcache.Load(BlobPath);
    }

    public void Dispose() => CodepointPerfcache.Unload();

    private static string? LocateBlob()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        const string share = "/opt/laplace/share/laplace";
        if (Directory.Exists(share))
        {
            var hit = Directory.EnumerateFiles(share, "laplace_t0_perfcache*.bin").FirstOrDefault();
            if (hit is not null) return hit;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory
                    .EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
        }
        return null;
    }
}

[CollectionDefinition("Perfcache")]
public sealed class PerfcacheCollection : ICollectionFixture<PerfcacheTestFixture> { }
