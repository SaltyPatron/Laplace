using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests;

/// <summary>
/// MemoryTopology's fold constants size the ingest memory envelope. This test must
/// mirror the CURRENT retained accumulator shape; a stale surrogate is worse than no
/// measurement because it gives a precise-looking justification to the wrong batch size.
/// </summary>
public sealed class FoldMemoryTopologyMeasurementTests
{
    private readonly record struct PeriodKey(long OpponentRatingFp1e9, long PhiFp1e9);

    private struct PeriodAggregate
    {
        public long Games;
        public long SumScoreFp1e9;
    }

    // Mirrors ConsensusAccumulatingWriter.Delta as of the grouped-rating-period path:
    // two inline structs, an optional dictionary reference, and three aggregate longs.
    // The previous test modeled four longs and therefore stopped measuring production
    // when exact grouped periods were added.
    private struct Delta
    {
        public PeriodKey FirstPeriod;
        public PeriodAggregate FirstAggregate;
        public Dictionary<PeriodKey, PeriodAggregate>? AdditionalPeriods;
        public long Games;
        public long SumScoreFp1e9;
        public long MaxTsUnixUs;
    }

    private static long MeasureBytesPerEntry(int entries)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(forceFullCollection: true);

        var map = new Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta>(entries);
        for (int i = 0; i < entries; i++)
        {
            var h = new Hash128((ulong)i, (ulong)~i);
            map[(h, h, h)] = new Delta
            {
                FirstPeriod = new PeriodKey(Glicko2.DefaultRatingFp1e9, 30_000_000_000L),
                FirstAggregate = new PeriodAggregate { Games = 1, SumScoreFp1e9 = i },
                Games = 1,
                SumScoreFp1e9 = i,
                MaxTsUnixUs = i,
            };
        }

        long after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(map);
        return (after - before) / entries;
    }

    [Fact]
    public void ConsensusFoldBytesPerRelation_IsConservativeAndNotWildlyOver()
    {
        long measured = MeasureBytesPerEntry(200_000);

        Assert.True(measured > 0, $"measurement produced {measured} bytes/entry");
        Assert.True(
            MemoryTopology.ConsensusFoldBytesPerRelation >= measured,
            $"ConsensusFoldBytesPerRelation is {MemoryTopology.ConsensusFoldBytesPerRelation} but "
            + $"the current accumulated relation shape measures {measured} bytes: the fold "
            + "envelope is under-reserved and can outrun back-pressure");
        Assert.True(
            MemoryTopology.ConsensusFoldBytesPerRelation <= measured * 2,
            $"ConsensusFoldBytesPerRelation is {MemoryTopology.ConsensusFoldBytesPerRelation} "
            + $"against a measured {measured} bytes/entry — over 2x silently halves "
            + "accumulator capacity and multiplies calls to the dominant fold statement");
    }

    [Fact]
    public void TransitBytesPerCell_ExceedsResidentBytes_BecauseItAlsoCoversTheWire()
    {
        Assert.True(
            MemoryTopology.ConsensusFoldTransitBytesPerCell
                > MemoryTopology.ConsensusFoldBytesPerRelation,
            "transit cost must exceed resident cost: it carries the same cell plus the wire");
    }
}
