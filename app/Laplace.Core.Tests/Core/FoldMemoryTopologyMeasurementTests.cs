using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests;

/// <summary>
/// MemoryTopology's fold constants size the ingest memory envelope, and
/// ConsensusFoldTransitBytesPerCell DIVIDES that envelope to produce chunkCells — the
/// number of cells one consensus_upsert call carries. Measured on the live foundation
/// seed, that call is the single most expensive statement in the whole ingest
/// (consensus.upsert_type: 3,189s over 1,058 calls, mean 3,014ms, against 1,121s for all
/// COPY combined), so a constant that is wrong by 2x makes the chunk size wrong by 2x on
/// the exact statement that dominates the run.
///
/// Both constants carried an accounting rationale in their comments and no measurement.
/// This is the same gap IngestRecordSizeMeasurementTests exists to close for
/// bytes-per-record: "a constant declared per source that nothing ever checked".
///
/// These do not assert an exact figure — allocator behaviour is not a fixed number. They
/// assert the constants are CONSERVATIVE (at least what the shape actually costs, so the
/// envelope cannot be under-reserved into an OOM) and not absurdly conservative (which
/// silently shrinks every chunk and slows the dominant statement).
/// </summary>
public sealed class FoldMemoryTopologyMeasurementTests
{
    // The accumulator's real shape: Dictionary<(Hash128, Hash128, Hash128?), Delta>
    // where Delta is four int64s (ConsensusAccumulatingWriter.Delta).
    private struct Delta
    {
        public long PhiFp1e9;
        public long Games;
        public long SumScoreFp1e9;
        public long MaxTsUnixUs;
    }

    private static long MeasureBytesPerEntry(int entries)
    {
        // Settle, then measure only what the dictionary itself retains.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(forceFullCollection: true);

        var map = new Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta>(entries);
        for (int i = 0; i < entries; i++)
        {
            var h = new Hash128((ulong)i, (ulong)~i);
            map[(h, h, h)] = new Delta { Games = 1, SumScoreFp1e9 = i };
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
            + $"one accumulated relation measures {measured} bytes: the fold envelope is "
            + "under-reserved and the run can exhaust memory before back-pressure engages");
        Assert.True(
            MemoryTopology.ConsensusFoldBytesPerRelation <= measured * 4,
            $"ConsensusFoldBytesPerRelation is {MemoryTopology.ConsensusFoldBytesPerRelation} "
            + $"against a measured {measured} bytes/entry — over 4x reserves memory nothing "
            + "uses and shrinks every fold chunk for no reason");
    }

    [Fact]
    public void TransitBytesPerCell_ExceedsResidentBytes_BecauseItAlsoCoversTheWire()
    {
        // Transit covers the resident accumulator PLUS the managed arrays, the Npgsql write
        // buffer, the server-side arrays and the per-type slices. It must therefore be
        // strictly larger than the resident cost, or the envelope is counting the wire for
        // free.
        Assert.True(
            MemoryTopology.ConsensusFoldTransitBytesPerCell
                > MemoryTopology.ConsensusFoldBytesPerRelation,
            "transit cost must exceed resident cost: it carries the same cell plus the wire");
    }
}
