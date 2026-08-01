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
    public void Ingest_PreservesTheInstallStringExceptForPlanReuse()
    {
        // Was Ingest_IsByteIdenticalToTheInstallString. The ingest policy now adds plan
        // reuse and nothing else, so every other key must still come through untouched.
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
    public void Ingest_LeavesTimeoutUnbounded_AndReusesPlans()
    {
        // Timeout stays unbounded: hours-long COPY and fold statements are legitimate here.
        //
        // AutoPrepare is now ON, reversing this test's original assertion. The old rationale
        // was that the ingest path issues staging DDL which would invalidate cached plans --
        // but PostgreSQL invalidates a dependent plan automatically and re-plans on next use.
        // That is the mechanism working, not a hazard, and it never justified re-planning
        // EVERY statement on EVERY call.
        //
        // MEASURED 2026-08-01, tier-descent probe over the partitioned physicalities table:
        //   Planning 28.622 ms (10,660 buffers) against Execution 7.108 ms.
        // Four times more expensive to plan than to run, because ~130 RANGE leaves are opened
        // to build an Append that runtime pruning then discards. The same run logged
        // 131,686,449 buffer hits against 59,163 disk reads across 67 calls.
        var b = new NpgsqlConnectionStringBuilder(
            LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest, "Host=h;Database=d;Command Timeout=0"));

        Assert.Equal(0, b.CommandTimeout);
        Assert.True(b.MaxAutoPrepare > 0,
            "ingest must reuse plans: re-planning a ~130-leaf Append per call cost 4x its execution");
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
