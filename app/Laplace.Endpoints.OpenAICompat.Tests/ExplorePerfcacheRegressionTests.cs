using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class ExplorePerfcacheRegressionTests
{
    [Fact]
    public void FirstExplorerUse_PreservesThePublishedCodepointMapping()
    {
        CodepointPerfcache.LoadDefault();
        var before = CodepointPerfcache.Records;
        var result = new ExploreDecomposeService().Decompose("dog");
        var after = CodepointPerfcache.Records;

        // Compare addresses only: dereferencing the old span after the broken
        // explorer reload would itself access an unmapped native allocation.
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(before),
            ref MemoryMarshal.GetReference(after)),
            "Explorer initialization replaced a mapping already published to other readers.");
        Assert.NotEmpty(result.Nodes);
    }

    [Fact]
    public void Decompose_ReportsStoredNodesWithoutCollapsedSelfWrappers()
    {
        CodepointPerfcache.LoadDefault();
        var result = new ExploreDecomposeService().Decompose("MagnusCarlsen");

        Assert.Single(result.Nodes, n => n.IdHex == result.RootIdHex);
        Assert.DoesNotContain(result.Nodes, n => n.Tier is 1 or 3 or 4);
        Assert.Equal(14, result.Nodes.Count); // 13 codepoints + one emitted word root
    }

    [Fact]
    public async Task ParallelExplorerAndReverseLookup_UseOneInitializedMapping()
    {
        CodepointPerfcache.LoadDefault();
        var expected = CodepointPerfcache.Records['d'].Hash;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            Assert.NotEmpty(new ExploreDecomposeService().Decompose("dog").Nodes);
            Assert.True(CodepointPerfcache.TryLookupCodepoint(expected, out var codepoint));
            Assert.Equal((uint)'d', codepoint);
        })));
    }
}
