using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Guards the concurrency contract that monolith segmentation depends on: compose runs
/// across N record-aligned segments against the SAME decomposer instance, so the
/// canonical-name readback accumulator is written from many threads at once. A plain
/// HashSet corrupts here (the ISO639 ingest crash). ConcurrentStringSet must survive it
/// with exact HashSet semantics — no throw, correct dedup, first-wins Add.
/// </summary>
public sealed class ConcurrentStringSetTests
{
    [Fact]
    public void Add_UnderHeavyParallelism_DoesNotCorruptAndDedupsExactly()
    {
        var set = new ConcurrentStringSet(System.StringComparer.Ordinal);
        const int distinct = 5_000;
        const int writers = 32;

        // Every writer races to add the SAME distinct key space — maximum contention,
        // the exact shape of N segments emitting overlapping canonical names.
        Parallel.For(0, writers, new ParallelOptions { MaxDegreeOfParallelism = writers }, _ =>
        {
            for (int i = 0; i < distinct; i++)
                set.Add($"substrate/iso639/code/{i}/v1");
        });

        Assert.Equal(distinct, set.Count);
        for (int i = 0; i < distinct; i++)
            Assert.Contains($"substrate/iso639/code/{i}/v1", set);
    }

    [Fact]
    public void Add_ReturnsFirstWins_MatchingHashSetSemantics()
    {
        var set = new ConcurrentStringSet(System.StringComparer.Ordinal);
        Assert.True(set.Add("x"));   // newly added
        Assert.False(set.Add("x"));  // already present
    }

    [Fact]
    public void Readback_ExposesAllDistinctKeys_AsReadOnlyCollection()
    {
        var set = new ConcurrentStringSet(System.StringComparer.Ordinal);
        foreach (var s in new[] { "a", "b", "a", "c" }) set.Add(s);

        IReadOnlyCollection<string> readback = set;
        Assert.Equal(3, readback.Count);
        Assert.Equal(new[] { "a", "b", "c" }, readback.OrderBy(x => x).ToArray());
    }
}
