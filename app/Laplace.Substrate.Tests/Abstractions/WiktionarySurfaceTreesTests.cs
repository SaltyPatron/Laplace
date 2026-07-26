using Laplace.Decomposers.Wiktionary;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// WiktionarySurfaceTrees splits ContentTierSpine.TryStageIntoBuilder into its expensive
/// half (BuildTree — decompose, grapheme ladder, Merkle hash) and its cheap half
/// (EmitTree — register with a builder), then memoizes the expensive half across records.
///
/// Content-hash identity is exact and tier is a floor, so the ONLY acceptable outcome is
/// that the cached path is bit-identical to the direct path — same root id, first
/// occurrence and every occurrence after, in any builder. A cache that changed an id
/// would silently fork the substrate's identity law, which is far worse than a slow
/// ingest. These tests exist to make that impossible to regress.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class WiktionarySurfaceTreesTests
{
    private static SubstrateChangeBuilder NewBuilder(string context) =>
        new(WiktionaryDecomposer.Source, context, null,
            entityCapacity: 64, physicalityCapacity: 64, attestationCapacity: 64);

    private static Hash128 StageCached(string surface, string context)
    {
        var b = NewBuilder(context);
        Assert.True(WiktionarySurfaceTrees.TryStage(b, surface, WiktionaryDecomposer.Source, out var id));
        return id;
    }

    private static Hash128 StageDirectSpine(string surface, string context)
    {
        var b = NewBuilder(context);
        Assert.True(ContentTierSpine.TryStageIntoBuilder(
            b, System.Text.Encoding.UTF8.GetBytes(surface), WiktionaryDecomposer.Source, out var id));
        return id;
    }

    public static TheoryData<string> Surfaces() => new()
    {
        "filter",                       // ordinary word
        "en",                           // language code — extreme repeat class
        "noun",                         // POS tag
        "archaic",                      // register tag
        "a",                            // single codepoint
        "café",                         // multi-byte UTF-8
        "日本語",                        // non-Latin
        "🜁",                            // astral plane
        "New York",                     // space
        // Comfortably past MaxCachedSurfaceBytes (64) — must take the uncached
        // StageDirect path and still agree with the spine byte for byte.
        "a device for separating solid particles from a liquid or gas passing through it",
    };

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void CachedPath_IsBitIdenticalToTheSpine(string surface)
    {
        CodepointPerfcache.LoadDefault();

        Hash128 direct = StageDirectSpine(surface, "wiktionary/test/direct");
        Hash128 cached = StageCached(surface, "wiktionary/test/cached");

        Assert.NotEqual(default, direct);
        Assert.Equal(direct, cached);
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void RepeatOccurrences_AgreeAcrossSeparateBuilders(string surface)
    {
        CodepointPerfcache.LoadDefault();

        // First call may build and publish; later calls must hit the cache. Distinct
        // builders, because the native root-id dedup is per intent stage — a cached tree
        // must still stage correctly into a builder that has never seen the surface.
        Hash128 first = StageCached(surface, "wiktionary/test/a");
        Hash128 second = StageCached(surface, "wiktionary/test/b");
        Hash128 third = StageCached(surface, "wiktionary/test/c");

        Assert.Equal(first, second);
        Assert.Equal(second, third);
        Assert.Equal(StageDirectSpine(surface, "wiktionary/test/d"), third);
    }

    /// <summary>
    /// Content witnesses are staged into the builder's native ContentStage, NOT into the
    /// managed rows that Build() returns — both paths leave change.Entities empty, which
    /// is why this asserts parity between the paths rather than an absolute count. (An
    /// earlier draft asserted Entities.Length > 0 and failed; a probe showed the direct
    /// spine path returns 0 as well, so the expectation was wrong, not the cache.)
    /// The invariant that matters: after a cache hit, a builder that has never seen the
    /// surface must be left in exactly the state the direct spine call would leave it.
    /// </summary>
    [Fact]
    public void CacheHit_LeavesAFreshBuilder_InTheSameStateAsTheSpine()
    {
        CodepointPerfcache.LoadDefault();
        const string surface = "hyponym";

        // Warm the cache, so the builders below are served from a cache HIT.
        _ = StageCached(surface, "wiktionary/test/warm");

        var viaCache = NewBuilder("wiktionary/test/fresh-cached");
        Assert.True(WiktionarySurfaceTrees.TryStage(viaCache, surface, WiktionaryDecomposer.Source, out var cachedId));
        var cachedChange = viaCache.Build();

        var viaSpine = NewBuilder("wiktionary/test/fresh-direct");
        Assert.True(ContentTierSpine.TryStageIntoBuilder(
            viaSpine, System.Text.Encoding.UTF8.GetBytes(surface), WiktionaryDecomposer.Source, out var spineId));
        var spineChange = viaSpine.Build();

        Assert.NotEqual(default, cachedId);
        Assert.Equal(spineId, cachedId);
        Assert.Equal(spineChange.Entities.Length, cachedChange.Entities.Length);
        Assert.Equal(spineChange.Physicalities.Length, cachedChange.Physicalities.Length);
        Assert.Equal(spineChange.Attestations.Length, cachedChange.Attestations.Length);
    }

    /// <summary>
    /// The cache must actually be reached — a memo that never hits is just the old cost
    /// plus a dictionary lookup. Staging a short surface twice must leave exactly one
    /// entry behind it.
    /// </summary>
    [Fact]
    public void ShortSurfaces_AreMemoized_LongOnesAreNot()
    {
        CodepointPerfcache.LoadDefault();

        int before = WiktionarySurfaceTrees.CachedSurfaceCount;
        _ = StageCached("memoizeme", "wiktionary/test/m1");
        int afterShort = WiktionarySurfaceTrees.CachedSurfaceCount;
        _ = StageCached("memoizeme", "wiktionary/test/m2");
        int afterRepeat = WiktionarySurfaceTrees.CachedSurfaceCount;

        Assert.Equal(before + 1, afterShort);
        Assert.Equal(afterShort, afterRepeat);

        // Past MaxCachedSurfaceBytes the surface is near-unique; caching it would spend
        // native tree memory for no reuse.
        _ = StageCached(
            "a device for separating solid particles from a liquid or gas passing through it",
            "wiktionary/test/m3");
        Assert.Equal(afterRepeat, WiktionarySurfaceTrees.CachedSurfaceCount);
    }

    [Fact]
    public void ConcurrentStaging_OfOneSurface_AgreesOnTheRoot()
    {
        CodepointPerfcache.LoadDefault();
        const string surface = "coordinate";

        var ids = new Hash128[32];
        Parallel.For(0, ids.Length, i => ids[i] = StageCached(surface, $"wiktionary/test/par/{i}"));

        Assert.All(ids, id => Assert.Equal(ids[0], id));
        Assert.Equal(StageDirectSpine(surface, "wiktionary/test/par/ref"), ids[0]);
    }

    [Fact]
    public void EmptySurface_IsRejected_LikeTheSpine()
    {
        CodepointPerfcache.LoadDefault();
        var b = NewBuilder("wiktionary/test/empty");
        Assert.False(WiktionarySurfaceTrees.TryStage(b, "", WiktionaryDecomposer.Source, out var id));
        Assert.Equal(default, id);
    }
}
