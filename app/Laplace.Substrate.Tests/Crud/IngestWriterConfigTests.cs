using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("cpu-topology-global")]
public sealed class IngestWriterConfigTests
{
    [Fact]
    public void MaxIntentsPerCommit_WordNetBudget_ScalesAboveOmwCap()
    {
        int n = IngestSizing.ResolveMaxIntentsPerCommit(2048, 250_000, 250_000);
        Assert.Equal((250_000 + 2047) / 2048, n);
    }

    [Fact]
    public void BaselineGates_WriterRowsPerSecond_MinimumIs500k()
    {
        Assert.Equal(500_000, IngestBaselineGates.MinWriterRowsPerSecond);
    }

    [Fact]
    public void BaselineGates_Document_30SecondsPerGigabyte_ContractConstants()
    {
        // Pin the thresholds used by WarmReingest_Meets_30SecondsPerGigabyte_InputScanGate.
        Assert.Equal(30.0, IngestBaselineGates.MaxSecondsPerGigabyte, precision: 3);
        Assert.InRange(IngestBaselineGates.MinMegabytesPerSecond, 34.0, 35.0);
    }
}
