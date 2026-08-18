using Xunit;

namespace Laplace.Ingestion.Tests;

/// <summary>
/// Guards the separation between compose memory fragments and database apply batches.
/// A multi-file worker may close tiny compose sets to release deferred trees; the runner
/// must not turn their count into a transaction/probe/fold cadence.
/// </summary>
public sealed class IngestWorkingSetBatchingGateTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(499_999, 399_999, false)]
    [InlineData(500_000, 1, true)]
    [InlineData(1, 400_000, true)]
    public void WorkingSetFlushPolicy_UsesFinalizedPayloadBounds(
        long bytes, int rows, bool expected)
    {
        Assert.Equal(expected, IngestRunner.ShouldFlushWorkingSet(
            bytes, rows, byteCap: 500_000, rowCap: 400_000));
    }
}
