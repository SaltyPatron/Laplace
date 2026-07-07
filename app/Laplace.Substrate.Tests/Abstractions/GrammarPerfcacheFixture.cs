using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;


public sealed class GrammarPerfcacheFixture : IDisposable
{
    public GrammarPerfcacheFixture()
    {
        if (CodepointPerfcache.IsLoaded) return;
        var blob = LocateBlob() ?? throw new InvalidOperationException(
            "T0 perfcache blob not found. Build the engine (target laplace_t0_perfcache).");
        CodepointPerfcache.Load(blob);
    }

    // The T0 perfcache is process-global native state shared by every test
    // collection in this assembly (the suites used to be separate processes);
    // unloading here would pull it out from under still-running collections.
    public void Dispose() { }

    private static string? LocateBlob()
    {
        try { return LaplaceInstall.ResolveT0Perfcache(); }
        catch (InvalidOperationException) { return null; }
    }
}

[CollectionDefinition("GrammarPerfcache")]
public sealed class GrammarPerfcacheCollection : ICollectionFixture<GrammarPerfcacheFixture> { }
