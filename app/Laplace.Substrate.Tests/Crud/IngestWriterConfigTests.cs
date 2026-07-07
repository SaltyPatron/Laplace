using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class IngestWriterConfigTests
{
    [Fact]
    public void ResolveApplyPartitions_IsAlwaysOne()
    {
        Assert.Equal(1, IngestTopology.ResolveApplyPartitions());
    }

    [Fact]
    public void MaxIntentsPerCommit_WordNetBudget_ScalesAboveOmwCap()
    {
        int n = IngestSizing.ResolveMaxIntentsPerCommit(2048, 250_000, 250_000);
        Assert.InRange(n, 9, 48);
    }

    [Fact]
    public void BaselineGates_WriterRowsPerSecond_Is500k()
    {
        Assert.Equal(500_000, IngestBaselineGates.MinWriterRowsPerSecond);
    }

    [Fact]
    public void BaselineGates_WarmIngest_Is30SecondsPerGigabyte()
    {
        Assert.Equal(30.0, IngestBaselineGates.MaxSecondsPerGigabyte, precision: 3);
        Assert.InRange(IngestBaselineGates.MinMegabytesPerSecond, 34.0, 35.0);
    }
}
