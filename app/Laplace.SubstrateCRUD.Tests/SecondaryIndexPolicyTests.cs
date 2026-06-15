using global::Npgsql;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
public class SecondaryIndexPolicyTests
{
    private const string Table = "idxpolicy_probe";
    private const string SecondaryIndex = "idxpolicy_probe_v";

    private readonly LocalPgFixture _pg;
    public SecondaryIndexPolicyTests(LocalPgFixture pg) => _pg = pg;

    private async Task ResetTableAsync(bool seedRow)
    {
        await using var conn = await _pg.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            DROP TABLE IF EXISTS laplace.{Table};
            CREATE TABLE laplace.{Table} (id bigint PRIMARY KEY, v int);
            CREATE INDEX {SecondaryIndex} ON laplace.{Table} (v);
            {(seedRow ? $"INSERT INTO laplace.{Table} (id, v) VALUES (1, 7);" : "")}";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> SecondaryIndexCountAsync()
    {
        await using var conn = await _pg.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT count(*) FROM pg_index i
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = 'laplace' AND t.relname = $1
              AND NOT i.indisprimary AND NOT i.indisunique";
        cmd.Parameters.AddWithValue(Table);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task EmptyTable_DropsSecondaryIndexes_AndExplicitRebuildRestoresThem()
    {
        await ResetTableAsync(seedRow: false);
        var policy = new SecondaryIndexPolicy(_pg.DataSource);

        await using (var scope = await policy.SuspendForBulkLoadAsync(Table))
        {
            Assert.True(scope.Dropped);
            Assert.False(scope.TableWasPopulated);
            Assert.Single(scope.DroppedIndexDefs);
            Assert.Equal(0, await SecondaryIndexCountAsync()); // dropped for index-free load

            await scope.RebuildAsync();
            Assert.True(scope.Rebuilt);
            Assert.Equal(1, await SecondaryIndexCountAsync()); // restored verbatim
        }

        // dispose after an explicit rebuild is a no-op; the index stays exactly once.
        Assert.Equal(1, await SecondaryIndexCountAsync());
    }

    [Fact]
    public async Task PopulatedTable_KeepsIndexesLive()
    {
        await ResetTableAsync(seedRow: true);
        var policy = new SecondaryIndexPolicy(_pg.DataSource);

        await using var scope = await policy.SuspendForBulkLoadAsync(Table);

        Assert.False(scope.Dropped);
        Assert.True(scope.TableWasPopulated);
        Assert.Empty(scope.DroppedIndexDefs);
        Assert.Equal(1, await SecondaryIndexCountAsync()); // untouched
    }

    [Fact]
    public async Task Dispose_RebuildsEvenWhenCallerForgets()
    {
        await ResetTableAsync(seedRow: false);
        var policy = new SecondaryIndexPolicy(_pg.DataSource);

        // The structural guarantee: a load that throws (or simply never rebuilds explicitly)
        // must NOT leave the table index-free. Dispose alone restores it.
        await using (var scope = await policy.SuspendForBulkLoadAsync(Table))
        {
            Assert.Equal(0, await SecondaryIndexCountAsync());
            Assert.False(scope.Rebuilt);
        }

        Assert.Equal(1, await SecondaryIndexCountAsync());
    }

    [Theory]
    [InlineData("attestations; DROP TABLE x")]
    [InlineData("foo bar")]
    [InlineData("")]
    public async Task UnsafeTableIdentifier_Throws(string table)
    {
        var policy = new SecondaryIndexPolicy(_pg.DataSource);
        await Assert.ThrowsAsync<ArgumentException>(() => policy.TableHasAnyRowsAsync(table));
    }
}
