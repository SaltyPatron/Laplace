using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public class SecondaryIndexPolicyTests
{
    private const string Table = "idxpolicy_probe";
    private const string SecondaryIndex = "idxpolicy_probe_v";

    private readonly LocalPgFixture _pg;
    public SecondaryIndexPolicyTests(LocalPgFixture pg) => _pg = pg;

    private async Task ResetTableAsync()
    {
        await using var conn = await _pg.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            DROP TABLE IF EXISTS laplace.{Table};
            CREATE TABLE laplace.{Table} (id bigint PRIMARY KEY, v int);
            CREATE INDEX {SecondaryIndex} ON laplace.{Table} (v);";
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SecondaryIndexesPresentAsync_TrueWhenSecondaryIndexExists()
    {
        await ResetTableAsync();
        var policy = new SecondaryIndexPolicy(_pg.DataSource);
        Assert.True(await policy.SecondaryIndexesPresentAsync(Table));
    }

    [Fact]
    public async Task EnsureIndexesAsync_CreatesMissingIndexesWithoutDropping()
    {
        await using var conn = await _pg.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            DROP TABLE IF EXISTS laplace.{Table};
            CREATE TABLE laplace.{Table} (id bigint PRIMARY KEY, v int);";
        await cmd.ExecuteNonQueryAsync();

        var policy = new SecondaryIndexPolicy(_pg.DataSource);
        Assert.False(await policy.SecondaryIndexesPresentAsync(Table));

        await SecondaryIndexPolicy.EnsureIndexesAsync(
            _pg.DataSource,
            [$"CREATE INDEX IF NOT EXISTS {SecondaryIndex} ON laplace.{Table} (v)"],
            CancellationToken.None);

        Assert.True(await policy.SecondaryIndexesPresentAsync(Table));
    }

    [Theory]
    [InlineData("attestations; DROP TABLE x")]
    [InlineData("foo bar")]
    [InlineData("")]
    public async Task UnsafeTableIdentifier_Throws(string table)
    {
        var policy = new SecondaryIndexPolicy(_pg.DataSource);
        await Assert.ThrowsAsync<ArgumentException>(() => policy.SecondaryIndexesPresentAsync(table));
    }
}
