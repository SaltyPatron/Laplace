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
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(BlobPath);
    }

    // The T0 perfcache is process-global native state shared by every test
    // collection in this assembly; unloading here would unmap it under
    // concurrently running collections (this assembly does not disable xunit
    // parallelization). Fixtures must never CodepointPerfcache.Unload().
    public void Dispose() { }

    private static string? LocateBlob()
    {
        try { return LaplaceInstall.ResolveT0Perfcache(); }
        catch (InvalidOperationException) { return null; }
    }
}

[CollectionDefinition("Perfcache")]
public sealed class PerfcacheCollection : ICollectionFixture<PerfcacheTestFixture> { }
