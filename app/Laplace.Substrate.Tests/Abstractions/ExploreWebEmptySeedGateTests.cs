using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// An unresolved/thin prompt can legitimately compile to an empty routed seed set.
/// PostgreSQL represents '{}'::bytea[] with zero dimensions, while non-empty bytea[]
/// operands are one-dimensional. The native crawl must treat both representations as
/// the same typed cardinality law: zero seeds means zero work, not an internal error.
/// NULL and true multidimensional arrays remain invalid.
/// </summary>
public class ExploreWebEmptySeedGateTests
{
    private static string Root => TypeIdLawTests.FindRepoRootPublic();

    [Fact]
    public void NativeExploreWeb_AcceptsCanonicalEmptyTypedArrayBeforeZeroSeedReturn()
    {
        var path = Path.Combine(
            Root, "extension", "laplace_substrate", "src", "explore_web.c");
        var source = File.ReadAllText(path);

        var typeCheck = source.IndexOf("ARR_ELEMTYPE(seeds_a) != BYTEAOID", StringComparison.Ordinal);
        var emptyDimension = source.IndexOf("ARR_NDIM(seeds_a) != 0 && ARR_NDIM(seeds_a) != 1", StringComparison.Ordinal);
        var deconstruct = source.IndexOf("deconstruct_array(seeds_a", StringComparison.Ordinal);
        var zeroSeedReturn = source.IndexOf("n_seed_datums == 0", StringComparison.Ordinal);

        Assert.True(typeCheck >= 0, "explore_web no longer proves bytea[] element type");
        Assert.True(emptyDimension > typeCheck, "explore_web no longer distinguishes empty/1-D from multidimensional arrays");
        Assert.True(deconstruct > emptyDimension, "seed array is consumed before its dimensional contract is checked");
        Assert.True(zeroSeedReturn > deconstruct, "empty seed cardinality no longer reaches the zero-work return");
        Assert.Contains("PG_ARGISNULL(0)", source);
        Assert.Contains("seeds must not be NULL", source);
        Assert.DoesNotContain("ARR_NDIM(seeds_a) != 1 || ARR_ELEMTYPE(seeds_a) != BYTEAOID", source);
    }
}
