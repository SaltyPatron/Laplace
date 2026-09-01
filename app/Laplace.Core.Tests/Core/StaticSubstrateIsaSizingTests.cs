using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

[Collection("CpuTopology")]
public sealed class StaticSubstrateIsaSizingTests
{
    [Fact]
    public void OneWorkingSet_OwnsItsWholeAlreadyAccountedMemoryShare()
    {
        // WorkingSetBudgetBytes is already one owner's share of the client-memory
        // domain. The apply/fold fan divides that share. MemoryTopology must not
        // divide it by the same fan before the plan sees it (the former 1/p^2 bug).
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

        // Integer division may leave less than one row's worth per lane unused,
        // but the live fan must consume essentially the whole envelope, not 1/p of it.
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
