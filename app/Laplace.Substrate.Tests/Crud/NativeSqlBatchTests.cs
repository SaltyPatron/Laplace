using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class NativeSqlBatchTests(LocalPgFixture pg)
{
    [Fact]
    public async Task IndependentCatalogReadsKeepParameterAndResultPositions()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        byte[] first = Hash128.OfCanonical("catalog-batch/first").ToBytes();
        byte[] second = Hash128.OfCanonical("catalog-batch/second").ToBytes();
        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO laplace.entities(id,tier,type_id)
                VALUES($1,0,laplace.entity_type_id('Type')),($2,0,laplace.entity_type_id('Type'));
                """;
            setup.Parameters.AddWithValue(NpgsqlDbType.Bytea, first);
            setup.Parameters.AddWithValue(NpgsqlDbType.Bytea, second);
            await setup.ExecuteNonQueryAsync();
            setup.CommandText = "INSERT INTO laplace.canonical_names(id,name) VALUES($1,'First'),($2,'Second')";
            await setup.ExecuteNonQueryAsync();
        }
        var result = await NpgsqlRead.ReadBatchRowsAsync(conn,
            SqlCatalog.Get("entity.facets"),
            p => p.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { first }),
            r => r.GetFieldValue<byte[]>(0),
            SqlCatalog.Get("display.labels"),
            p => p.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { second, first, second }),
            r => r.GetString(1));
        Assert.Equal(first, Assert.Single(result.First));
        Assert.Equal(new[] { "Second", "First", "Second" }, result.Second);
        await transaction.RollbackAsync();
    }
}
