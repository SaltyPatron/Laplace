using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests;

/// <summary>
/// GC.GetTotalMemory is process-wide. This measurement must not overlap another
/// xUnit collection allocating in the same testhost, or unrelated live allocations
/// are charged to the fold dictionary and the measured bytes/entry becomes random.
/// </summary>
[CollectionDefinition("Fold memory topology measurement", DisableParallelization = true)]
public sealed class FoldMemoryTopologyMeasurementCollection
{
}

/// <summary>
/// Measures retained bytes per consensus-fold accumulator entry, which MemoryTopology's
/// fold constants use to size the ingest memory envelope. The local Delta must match the
/// production accumulator's shape for the measurement to mean anything.
/// </summary>
[Collection("Fold memory topology measurement")]
public sealed class FoldMemoryTopologyMeasurementTests
{
    private readonly record struct PeriodKey(long OpponentRatingFp1e9, long PhiFp1e9);

    private struct PeriodAggregate
    {
        public long Games;
        public long SumScoreFp1e9;
    }

    // Mirrors ConsensusAccumulatingWriter.Delta: two inline structs, an optional
    // dictionary of additional rating periods, and three aggregate longs.
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
