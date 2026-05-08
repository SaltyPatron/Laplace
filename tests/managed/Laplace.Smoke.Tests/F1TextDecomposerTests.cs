namespace Laplace.Smoke.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core;
using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline;
using Laplace.Pipeline.Abstractions;

using Xunit;

/// <summary>
/// F1 ITextDecomposition property tests. The smallest "substrate exists as
/// invention" verification gate at the managed level: F1 turns text into
/// content-addressed substrate entities deterministically, with maximum dedup
/// and RLE, and produces stable hashes regardless of which CodepointPool
/// instance resolves the codepoints. Substrate invariants 1, 3, 4 (CLAUDE.md).
///
/// All tests exercise the real native BLAKE3 P/Invoke surface — no mocks of
/// IIdentityHashing. CodepointPool's on-the-fly fallback is used so tests
/// don't depend on a generated TSV.
/// </summary>
public class F1TextDecomposerTests
{
    [Fact]
    public async Task Decompose_Cat_EmitsOneCompositionAndThreeChildrenWithRleOne()
    {
        var (decomposer, sink) = MakeDecomposer();

        var hash = await decomposer.DecomposeAsync("cat", CancellationToken.None);

        Assert.Single(sink.Entities);
        var entity = sink.Entities[0];
        Assert.Equal((short)1, entity.Tier);
        Assert.True(HashEquals(entity.Hash, hash));

        Assert.Equal(3, sink.Children.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(i, sink.Children[i].Ordinal);
            Assert.Equal(1, sink.Children[i].RleCount);
            Assert.True(HashEquals(sink.Children[i].ParentHash, hash));
        }
    }

    [Fact]
    public async Task Decompose_SameInputTwice_ProducesIdenticalCompositionHash()
    {
        var (d1, _) = MakeDecomposer();
        var (d2, _) = MakeDecomposer();

        var h1 = await d1.DecomposeAsync("cat", CancellationToken.None);
        var h2 = await d2.DecomposeAsync("cat", CancellationToken.None);

        Assert.True(HashEquals(h1, h2));
    }

    [Fact]
    public async Task Decompose_TripleA_CollapsesToOneRleRunOfCountThree()
    {
        var (decomposer, sink) = MakeDecomposer();

        await decomposer.DecomposeAsync("aaa", CancellationToken.None);

        Assert.Single(sink.Entities);
        Assert.Single(sink.Children);
        Assert.Equal(3, sink.Children[0].RleCount);
    }

    [Fact]
    public async Task Decompose_DistinctInputs_ProduceDistinctHashes()
    {
        var (d1, _) = MakeDecomposer();
        var (d2, _) = MakeDecomposer();

        var hCat = await d1.DecomposeAsync("cat", CancellationToken.None);
        var hDog = await d2.DecomposeAsync("dog", CancellationToken.None);

        Assert.False(HashEquals(hCat, hDog));
    }

    [Fact]
    public async Task Decompose_EmptyString_ProducesCanonicalEmptyEntity()
    {
        var (decomposer, sink) = MakeDecomposer();

        var hash = await decomposer.DecomposeAsync("", CancellationToken.None);

        Assert.Single(sink.Entities);
        Assert.Empty(sink.Children);
        Assert.True(HashEquals(sink.Entities[0].Hash, hash));
    }

    [Fact]
    public async Task Decompose_UnicodeRunes_EmitsOneChildPerRune()
    {
        // 'café' = c, a, f, é. Four runes (é is one Rune, U+00E9 in NFC).
        var (decomposer, sink) = MakeDecomposer();

        await decomposer.DecomposeAsync("café", CancellationToken.None);

        Assert.Equal(4, sink.Children.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(1, sink.Children[i].RleCount);
        }
    }

    [Fact]
    public void CodepointPool_TwoInstancesYieldSameHashForSameCodepoint()
    {
        var hashing = new IdentityHashing();
        var poolA = new CodepointPool(hashing);
        var poolB = new CodepointPool(hashing);

        for (var cp = 0; cp < 256; cp++)
        {
            var a = poolA.AtomIdFor(cp);
            var b = poolB.AtomIdFor(cp);
            Assert.True(HashEquals(a, b));
        }
    }

    [Fact]
    public async Task Decompose_PrefixSuffix_ShareCommonRunHashesNotCompositionHash()
    {
        // "abc" and "abcd" share the same first-three codepoint hashes but the
        // composition hash differs — content addressing of the whole string.
        var (d1, sink1) = MakeDecomposer();
        var (d2, sink2) = MakeDecomposer();

        var h1 = await d1.DecomposeAsync("abc", CancellationToken.None);
        var h2 = await d2.DecomposeAsync("abcd", CancellationToken.None);

        Assert.False(HashEquals(h1, h2));
        Assert.Equal(3, sink1.Children.Count);
        Assert.Equal(4, sink2.Children.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(HashEquals(sink1.Children[i].ChildHash, sink2.Children[i].ChildHash));
        }
    }

    private static (TextDecomposer Decomposer, RecordingSink Sink) MakeDecomposer()
    {
        var hashing      = new IdentityHashing();
        var pool         = new CodepointPool(hashing);
        var sink         = new RecordingSink();
        var decomposer   = new TextDecomposer(pool, hashing, sink, sink);
        return (decomposer, sink);
    }

    private static bool HashEquals(AtomId a, AtomId b)
    {
        var sa = a.AsSpan();
        var sb = b.AsSpan();
        if (sa.Length != sb.Length) { return false; }
        for (var i = 0; i < sa.Length; i++)
        {
            if (sa[i] != sb[i]) { return false; }
        }
        return true;
    }

    private sealed class RecordingSink : IEntityEmission, IEntityChildEmission
    {
        public readonly List<EntityRecord>      Entities = new();
        public readonly List<EntityChildRecord> Children = new();

        public ValueTask EmitAsync(EntityRecord record, CancellationToken cancellationToken)
        {
            Entities.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask EmitAsync(EntityChildRecord record, CancellationToken cancellationToken)
        {
            Children.Add(record);
            return ValueTask.CompletedTask;
        }
    }
}
