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
                "target `laplace_t0_perfcache`).");
        CodepointPerfcache.Load(BlobPath);
    }

    public void Dispose() => CodepointPerfcache.Unload();

    private static string? LocateBlob()
    {
        try { return LaplaceInstall.ResolveT0Perfcache(); }
        catch (InvalidOperationException) { return null; }
    }
}

[CollectionDefinition("Perfcache")]
public sealed class PerfcacheCollection : ICollectionFixture<PerfcacheTestFixture> { }
