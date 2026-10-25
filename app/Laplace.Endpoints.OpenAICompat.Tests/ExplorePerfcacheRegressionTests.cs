using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Laplace.Api.Contracts;
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

        // Compare addresses only: dereferencing a replaced span would touch an unmapped
        // native allocation.
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(before),
            ref MemoryMarshal.GetReference(after)),
            "Explorer initialization replaced a mapping already published to other readers.");
        Assert.NotEmpty(result.Nodes);
    }

    [Fact]
    public void StorageProof_UsesThePublishedTier0Rom()
    {
        CodepointPerfcache.LoadDefault();
        ref readonly CodepointRecord cached = ref CodepointPerfcache.LookupScalar((uint)'A');

        StorageProofResponse proof = new ExploreDecomposeService().StorageProof("A");
        StorageProofNodeRow leaf = Assert.Single(proof.Nodes);

        Assert.Equal((uint)'A', leaf.Atom);
        Assert.Equal(cached.UcaOrder, leaf.DucetRank);
        Assert.Equal(Convert.ToHexStringLower(cached.Hash.ToBytes()), leaf.IdHex);
        Assert.Equal(cached.CoordX, leaf.X);
        Assert.Equal(cached.CoordY, leaf.Y);
        Assert.Equal(cached.CoordZ, leaf.Z);
        Assert.Equal(cached.CoordM, leaf.M);
        Assert.Equal(Convert.ToHexStringLower(cached.Hilbert.ToByteArray()), leaf.HilbertHex);
    }

    [Fact]
    public void Decompose_ReportsStoredNodesWithoutCollapsedSelfWrappers()
    {
        CodepointPerfcache.LoadDefault();
        var result = new ExploreDecomposeService().Decompose("MagnusCarlsen");

        using var native = TextDecomposer.Run("MagnusCarlsen");
        var root = Assert.Single(result.Nodes, n => n.IdHex == result.RootIdHex);
        Assert.Equal(native.GetNode(native.NaturalUnitIndex()).Tier, root.Tier);
        Assert.All(result.Nodes, n => Assert.True(n.Tier == 0 || native.ShouldEmitCompositional(n.Ordinal)));
        Assert.Equal(14, result.Nodes.Count); // 13 codepoints + one emitted word root
    }

    [Fact]
    public async Task ParallelExplorerAndReverseLookup_UseOneInitializedMapping()
    {
        CodepointPerfcache.LoadDefault();
        var expected = CodepointPerfcache.LookupScalar((uint)'d').Hash;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            Assert.NotEmpty(new ExploreDecomposeService().Decompose("dog").Nodes);
            Assert.True(CodepointPerfcache.TryLookupCodepoint(expected, out var codepoint));
            Assert.Equal((uint)'d', codepoint);
        })));
    }
}
