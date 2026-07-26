using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// Pins the two access policies. The Ingest assertion is the load-bearing one: the
/// CLI and Chess were migrated onto it on the claim that it is byte-identical to the
/// bare LaplaceInstall.PostgresConnectionString() passthrough they used before. If that
/// ever stops being true, every ingest path silently acquires a timeout it never had.
/// </summary>
public sealed class LaplaceDataSourceTests
{
    [Fact]
    public void Ingest_IsByteIdenticalToTheInstallString()
    {
        Assert.Equal(
            LaplaceInstall.PostgresConnectionString(),
            LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest));
    }

    [Fact]
    public void Ingest_LeavesTimeoutUnboundedAndAutoPrepareOff()
    {
        // Hours-long COPY/fold are legitimate here, and the ingest path issues staging
        // DDL, which would invalidate cached plans.
        var b = new NpgsqlConnectionStringBuilder(
            LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest, "Host=h;Database=d;Command Timeout=0"));

        Assert.Equal(0, b.CommandTimeout);
        Assert.Equal(0, b.MaxAutoPrepare);
    }

    [Fact]
    public void Serving_BoundsAnUnboundedIngestTimeout()
    {
        // The exact failure this policy exists to prevent: a serving path inheriting
        // the ingest CLI's `Command Timeout=0` and hanging its caller forever.
        var b = new NpgsqlConnectionStringBuilder(
            LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Serving, "Host=h;Database=d;Command Timeout=0"));

        Assert.Equal(LaplaceDataSource.ServingCommandTimeoutSeconds, b.CommandTimeout);
    }

    [Fact]
    public void Serving_DoesNotLoosenATighterCallerBudget()
    {
        var b = new NpgsqlConnectionStringBuilder(
            LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Serving, "Host=h;Database=d;Command Timeout=5"));

        Assert.Equal(5, b.CommandTimeout);
    }

    [Fact]
    public void Serving_EnablesPlanReuse()
    {
        var b = new NpgsqlConnectionStringBuilder(
            LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Serving, "Host=h;Database=d"));

        Assert.True(b.MaxAutoPrepare > 0);
        Assert.True(b.AutoPrepareMinUsages > 0);
    }
}
