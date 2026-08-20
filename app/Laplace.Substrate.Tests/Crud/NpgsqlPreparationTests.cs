using Laplace.SubstrateCRUD.Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class NpgsqlPreparationTests
{
    private readonly LocalPgFixture _pg;

    public NpgsqlPreparationTests(LocalPgFixture pg) => _pg = pg;

    [Fact]
    public async Task TypedRead_PreparesTheDeclaredStatementExplicitly()
    {
        await using var conn = await _pg.DataSource.OpenConnectionAsync();
        const string sql = "SELECT $1::integer + 1 /* laplace_explicit_prepare_test */";

        int value = await NpgsqlRead.ExecuteScalarAsync<int>(
            conn,
            sql,
            p => p.AddWithValue(NpgsqlDbType.Integer, 41));

        Assert.Equal(42, value);
        await using var check = conn.CreateCommand();
        check.CommandText =
            "SELECT EXISTS (SELECT 1 FROM pg_prepared_statements "
            + "WHERE statement LIKE '%laplace_explicit_prepare_test%')";
        Assert.True(await check.ExecuteScalarAsync() is true);
    }
}
