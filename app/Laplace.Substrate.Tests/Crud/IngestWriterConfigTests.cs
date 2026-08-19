using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class IngestWriterConfigTests
{
    [Fact]
    public void ResolveApplyDispatchWorkers_IsAlwaysOne()
    {
        Assert.Equal(1, IngestTopology.ResolveApplyDispatchWorkers());
    }

    [Fact]
    public void MaxIntentsPerCommit_WordNetBudget_ScalesAboveOmwCap()
    {
        int n = IngestSizing.ResolveMaxIntentsPerCommit(2048, 250_000, 250_000);
        Assert.InRange(n, 9, 48);
    }

    [Fact]
    public void BaselineGates_WriterRowsPerSecond_MinimumIs500k()
    {
        // Floor requirement — failing the throughput test means the write path
        // is unfinished, not that this constant should be lowered.
        Assert.Equal(500_000, IngestBaselineGates.MinWriterRowsPerSecond);
    }

    [Fact]
    public void BaselineGates_Document_30SecondsPerGigabyte_ContractConstants()
    {
        // Constants only — does not prove a warm ingest met the gate. That is
        // WarmReingest_Meets_30SecondsPerGigabyte_InputScanGate.
        Assert.Equal(30.0, IngestBaselineGates.MaxSecondsPerGigabyte, precision: 3);
        Assert.InRange(IngestBaselineGates.MinMegabytesPerSecond, 34.0, 35.0);
    }
}
