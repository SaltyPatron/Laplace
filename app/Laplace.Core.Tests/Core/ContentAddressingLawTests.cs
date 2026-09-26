using System;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// Content addressing (spec 05 #1b): a composition id is a function of the ordered child-id
/// sequence and nothing else (no tier, ordinal, or container), so equal content from any
/// source converges on one entity without a resolution pass.
///
/// Composes two children through the native composer, since a one-child node collapses to
/// the child and would never reach the Merkle compose path, at several tier arguments.
/// </summary>
public sealed class ContentAddressingLawTests
{
    private static unsafe Hash128 Compose(byte tier, Hash128 a, Hash128 b)
    {
        Hash128* kids = stackalloc Hash128[2] { a, b };
        double* coords = stackalloc double[8];
        for (int i = 0; i < 8; i++) coords[i] = 0.0;
        Hash128 outId;
        // out_coord is double[4] (the 4-ball centroid); a smaller buffer is overwritten.
        double* outCoord = stackalloc double[4];
        Hilbert128 outHb;
        NativeInterop.HashComposerComposeNode(tier, kids, coords, 2, &outId, outCoord, &outHb);
        Assert.False(outId.Equals(default(Hash128)), "composer returned a zero id");
        return outId;
    }

    [Fact]
    public unsafe void SameChildren_ComposeToTheSameId_AtEveryTier()
    {
        Hash128 a = Hash128.OfCanonical("law/content-addressing/a");
        Hash128 b = Hash128.OfCanonical("law/content-addressing/b");

        Hash128 atWord = Compose(2, a, b);
        Hash128 atSentence = Compose(3, a, b);
        Hash128 atDocument = Compose(4, a, b);

        Assert.Equal(atWord, atSentence);
        Assert.Equal(atWord, atDocument);
    }

    [Fact]
    public unsafe void DifferentChildren_ComposeToDifferentIds()
    {
        // Different children give different ids, so a constant composer cannot pass.
        Hash128 a = Hash128.OfCanonical("law/content-addressing/a");
        Hash128 b = Hash128.OfCanonical("law/content-addressing/b");
        Hash128 c = Hash128.OfCanonical("law/content-addressing/c");

        Assert.NotEqual(Compose(3, a, b), Compose(3, a, c));

        // Order is content: the child sequence is what is hashed.
        Assert.NotEqual(Compose(3, a, b), Compose(3, b, a));
    }
}
