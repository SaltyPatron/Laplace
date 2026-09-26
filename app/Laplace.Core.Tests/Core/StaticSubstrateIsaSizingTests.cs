using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

[Collection("CpuTopology")]
public sealed class StaticSubstrateIsaSizingTests
{
    [Fact]
    public void OneWorkingSet_OwnsItsWholeAlreadyAccountedMemoryShare()
    {
        // WorkingSetBudgetBytes is already one owner's share; the flush envelope equals
        // it, and only the apply/fold plan divides it across connections.
        Assert.Equal(
            MemoryTopology.WorkingSetBudgetBytes,
            MemoryTopology.WorkingSetFlushEnvelopeBytes);
        Assert.Equal(
            MemoryTopology.WorkingSetBudgetBytes,
            IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(1));
    }

    [Fact]
    public void ApplyTransit_DividesTheWorkingSetEnvelopeExactlyOnce()
    {
        int connections = Math.Max(1, CpuTopology.ResolveApplyPartitions());
        long envelope = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes();
        var plan = IngestSizing.ResolveApplyIo(
            connections,
            workingSetBudgetBytes: envelope,
            flushEnvelopeBytes: envelope);

        long probeResidency = (long)plan.ProbeChunkIds
            * MemoryTopology.PresenceProbeTransitBytesPerId
            * plan.Connections;
        long mergeResidency = (long)plan.MergeChunkRows
            * MemoryTopology.AttestationMergeTransitBytesPerRow
            * plan.Connections;

        // The fan consumes the whole envelope, less at most one row per connection
        // lost to integer division.
        Assert.InRange(
            envelope - probeResidency,
            0,
            (long)MemoryTopology.PresenceProbeTransitBytesPerId * plan.Connections);
        Assert.InRange(
            envelope - mergeResidency,
            0,
            (long)MemoryTopology.AttestationMergeTransitBytesPerRow * plan.Connections);
    }

    [Fact]
    public void ConcurrentWorkingSets_DivideTheOwnerShareOnce()
    {
        long one = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(1);
        long four = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(4);
        Assert.Equal(one / 4, four);
    }
}
