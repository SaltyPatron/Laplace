using Laplace.Engine.Core;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class LocalPgFixturePoolTests(LocalPgFixture primary)
{
    [Fact]
    public async Task ConcurrentFixturesShareOnePoolAndKeepItAliveUntilLastRelease()
    {
        var first = new LocalPgFixture();
        var second = new LocalPgFixture();
        try
        {
            await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
            Assert.Same(primary.DataSource, first.DataSource);
            Assert.Same(primary.DataSource, second.DataSource);
            await first.DisposeAsync();
            await first.DisposeAsync(); // releasing one owner twice must not drop the database
            await using var command = second.DataSource.CreateCommand("SELECT 1");
            Assert.Equal(1, await command.ExecuteScalarAsync());
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }

        await using var remaining = primary.DataSource.CreateCommand("SELECT 1");
        Assert.Equal(1, await remaining.ExecuteScalarAsync());
    }

    [Fact]
    public void SharedPoolUsesTheProductionIngestBudget()
    {
        var connection = new NpgsqlConnectionStringBuilder(primary.DataSource.ConnectionString);
        Assert.Equal(PostgresResourcePlan.Current.IngestConnectionOwners, connection.MaxPoolSize);
    }

    [Fact]
    public async Task RepeatedInitializationDoesNotAcquireAnotherReference()
    {
        var other = new LocalPgFixture();
        try
        {
            await other.InitializeAsync();
            var pool = other.DataSource;
            await other.InitializeAsync();
            Assert.Same(pool, other.DataSource);
        }
        finally
        {
            await other.DisposeAsync();
        }
        Assert.Throws<InvalidOperationException>(() => other.DataSource);
        await using var remaining = primary.DataSource.CreateCommand("SELECT 1");
        Assert.Equal(1, await remaining.ExecuteScalarAsync());
    }
}
