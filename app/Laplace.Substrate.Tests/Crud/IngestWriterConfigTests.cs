using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class IngestWriterConfigTests
{
    [Fact]
    public void ResolveApplyPartitions_Default_IsOneBulkApply()
    {
        var prev = Environment.GetEnvironmentVariable("LAPLACE_APPLY_PARTITIONS");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_APPLY_PARTITIONS", null);
            Assert.Equal(1, IngestTopology.ResolveApplyPartitions());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_APPLY_PARTITIONS", prev);
        }
    }

    [Fact]
    public void ResolveApplyPartitions_EnvOverride_IsHonored()
    {
        var prev = Environment.GetEnvironmentVariable("LAPLACE_APPLY_PARTITIONS");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_APPLY_PARTITIONS", "4");
            Assert.Equal(4, IngestTopology.ResolveApplyPartitions());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_APPLY_PARTITIONS", prev);
        }
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
