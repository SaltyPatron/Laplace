using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Tier is altitude, not identity: equal content is one hash at every tier. A one-child
/// composition is the child, so "dog" admitted alone has the word's id as its root, with
/// no sentence or document entity wrapping it.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class TierFloorIdentityTests
{
    [Fact]
    public void OneWordContent_TiersAboveWord_CarryTheWordId_NoWrapperEntities()
    {
        using var tree = IntentStage.BuildContentTree(Encoding.UTF8.GetBytes("dog"));
        Assert.NotNull(tree);

        var wordId = Hash128.Text("dog");
        var root = tree!.GetNode((uint)tree.NodeCount - 1);
        Assert.Equal(wordId, root.Id);
        Assert.Equal(1, root.Tier); // Three scalar leaves, one composition above them.
        for (uint i = 0; i < tree!.NodeCount; i++)
        {
            var node = tree.GetNode(i);
            if (node.ChildCount == 1)
                Assert.Equal(tree.GetNode(node.FirstChildIdx).Id, node.Id);
        }
        using var stage = IntentStage.New(32);
        Assert.True(stage.EmitContentTree(tree, wordId, default, out var emitted));
        Assert.Equal(wordId, emitted);
        Assert.Equal(1, stage.EntityCount);
    }
}
