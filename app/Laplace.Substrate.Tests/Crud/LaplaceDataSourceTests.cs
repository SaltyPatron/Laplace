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
    public void Ingest_PreservesTheInstallStringExceptForResourcePolicy()
    {
        // Pool ownership and explicit command preparation are separate concerns; every
        // unrelated installed setting must still come through untouched.
        var installed = new NpgsqlConnectionStringBuilder(LaplaceInstall.PostgresConnectionString());
        var ingest = new NpgsqlConnectionStringBuilder(
            LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest));

        Assert.Equal(installed.Host, ingest.Host);
        Assert.Equal(installed.Database, ingest.Database);
        Assert.Equal(installed.Username, ingest.Username);
        Assert.Equal(installed.CommandTimeout, ingest.CommandTimeout);
        Assert.Equal(installed.SearchPath, ingest.SearchPath);
    }

    [Fact]
    public void Ingest_LeavesTimeoutUnbounded_AndDisablesHeuristicPreparation()
    {
        // Timeout stays unbounded: hours-long COPY and fold statements are legitimate here.
        //
        var b = new NpgsqlConnectionStringBuilder(
            LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest,
                "Host=h;Database=d;Command Timeout=0;Max Auto Prepare=999;Auto Prepare Min Usages=1"));

        Assert.Equal(0, b.CommandTimeout);
        Assert.Equal(0, b.MaxAutoPrepare);
        Assert.Equal(PostgresResourcePlan.Current.IngestConnectionOwners, b.MaxPoolSize);
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
    public void Serving_DisablesHeuristicPreparation()
    {
        var b = new NpgsqlConnectionStringBuilder(
            LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Serving,
                "Host=h;Database=d;Max Auto Prepare=999;Auto Prepare Min Usages=1"));

        Assert.Equal(0, b.MaxAutoPrepare);
        Assert.Equal(PostgresResourcePlan.Current.ServingConnectionOwners, b.MaxPoolSize);
    }
}
