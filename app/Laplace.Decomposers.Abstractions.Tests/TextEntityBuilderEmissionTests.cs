using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class TextEntityBuilderEmissionTests
{
    private static readonly Hash128 Src =
        Hash128.OfCanonical("substrate/source/test/TextEmission/v1");

    [Fact]
    public void SingleWord_Suppresses_Document_And_Sentence_Wrappers()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("dog");
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out var ents, out _, out var rootId, out var rootTier));

        Assert.Equal(EntityTier.Word, rootTier);
        Assert.DoesNotContain(ents, e => e.Tier >= EntityTier.Sentence);

        var root = Assert.Single(ents, e => e.Id.EqualsBytewise(rootId));
        Assert.Equal(EntityTier.Word, root.Tier);
        Assert.Equal(TextEntityBuilder.WordTypeId, root.TypeId);
    }

    [Fact]
    public void MultiSentence_Keeps_Structural_Tiers()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("Hello world. Second sentence.");
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out var ents, out _, out _, out _));
        Assert.Contains(ents, e => e.Tier == EntityTier.Sentence);
    }
}
