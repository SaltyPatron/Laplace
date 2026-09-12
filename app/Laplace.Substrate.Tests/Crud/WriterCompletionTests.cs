using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class WriterCompletionTests(LocalPgFixture pg)
{
    [Fact]
    public async Task CompletionDrainsGinWithoutCliValidation()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var setup = conn.CreateCommand();
        setup.CommandText = """
            CREATE EXTENSION IF NOT EXISTS pgstattuple WITH SCHEMA public;
            CREATE TABLE laplace.writer_completion_probe (ids integer[]) WITH (autovacuum_enabled=false);
            CREATE INDEX writer_completion_probe_gin ON laplace.writer_completion_probe USING gin(ids);
            INSERT INTO laplace.writer_completion_probe SELECT ARRAY[g,g+1] FROM generate_series(1,100) g;
            """;
        await setup.ExecuteNonQueryAsync();
        try
        {
            await using var pending = conn.CreateCommand();
            pending.CommandText = "SELECT pending_pages FROM public.pgstatginindex('laplace.writer_completion_probe_gin'::regclass)";
            Assert.True(Convert.ToInt64(await pending.ExecuteScalarAsync()) > 0);
            var writer = new NpgsqlSubstrateWriter(pg.DataSource, NullLogger<NpgsqlSubstrateWriter>.Instance);
            await writer.CompleteBulkRunAsync();
            Assert.Equal(0L, Convert.ToInt64(await pending.ExecuteScalarAsync()));
            await writer.CompleteBulkRunAsync();
            Assert.Equal(0L, Convert.ToInt64(await pending.ExecuteScalarAsync()));
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = "DROP TABLE laplace.writer_completion_probe";
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
