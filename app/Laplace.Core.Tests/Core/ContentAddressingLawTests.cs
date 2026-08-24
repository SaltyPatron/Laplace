using System;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// THE content-addressing law, spec 05 #1b: same content = same hash AT EVERY TIER. The id
/// is a function of the child-id sequence and nothing else -- no tier, no ordinal, no
/// container. It is what makes cross-source merging a hash collision rather than an
/// entity-resolution pass, and what lets "cat" the word and "cat" standing alone as an
/// answer be one entity.
///
/// hash128.c discards the tier parameter with an explicit `(void)tier` and its comment
/// records that a tier byte was briefly mixed in on 2026-07-01, broke the law, and was
/// reverted.
///
/// NOTHING CAUGHT THAT. Measured 2026-08-24: mixing the tier byte back into
/// hash128_compose and rebuilding leaves all 735 Laplace.Substrate.Tests and all
/// Laplace.Core.Tests GREEN. TierFloorIdentityTests does not catch it either -- it composes
/// the single-word "dog", and hash_composer collapses a one-child node to the child id, so
/// the compose path under test is never reached.
///
/// This composes TWO children, which cannot collapse, at two different tiers.
/// </summary>
public sealed class ContentAddressingLawTests
{
    private static unsafe Hash128 Compose(byte tier, Hash128 a, Hash128 b)
    {
        Hash128* kids = stackalloc Hash128[2] { a, b };
        double* coords = stackalloc double[8];
        for (int i = 0; i < 8; i++) coords[i] = 0.0;
        Hash128 outId;
        // out_coord is double[4], not a single double (hash_composer.c:16). Passing a
        // one-slot buffer let the native centroid write four doubles over the stack and
        // clobber outId to zero -- and the "same id at every tier" assertion PASSED on
        // 0 == 0 == 0. Only the converse assertion below exposed it.
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
        // The converse, so the test above cannot be satisfied by a degenerate composer that
        // returns a constant.
        Hash128 a = Hash128.OfCanonical("law/content-addressing/a");
        Hash128 b = Hash128.OfCanonical("law/content-addressing/b");
        Hash128 c = Hash128.OfCanonical("law/content-addressing/c");

        Assert.NotEqual(Compose(3, a, b), Compose(3, a, c));

        // Order is content: the child SEQUENCE is what is hashed.
        Assert.NotEqual(Compose(3, a, b), Compose(3, b, a));
    }
}
