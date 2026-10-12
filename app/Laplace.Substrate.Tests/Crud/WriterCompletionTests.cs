using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class WriterCompletionTests(LocalPgFixture pg)
{
    [Fact]
    public async Task CompletionDefersUnownedGinWithoutLosingQueryableRows()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var setup = conn.CreateCommand();
        setup.CommandText = """
            CREATE EXTENSION IF NOT EXISTS pgstattuple WITH SCHEMA public;
            CREATE TABLE laplace.writer_completion_unowned_probe (ids integer[])
                WITH (autovacuum_enabled=false);
            CREATE INDEX writer_completion_unowned_probe_gin
                ON laplace.writer_completion_unowned_probe USING gin(ids);
            INSERT INTO laplace.writer_completion_unowned_probe
                SELECT ARRAY[g,g+1] FROM generate_series(1,100) g;
            """;
        await setup.ExecuteNonQueryAsync();
        try
        {
            var connection = new global::Npgsql.NpgsqlConnectionStringBuilder(pg.ConnectionString);
            connection.Options = (connection.Options + " -c role=pg_read_all_data -c enable_seqscan=off").Trim();
            await using var restricted = global::Npgsql.NpgsqlDataSource.Create(connection.ConnectionString);
            await using var reader = await restricted.OpenConnectionAsync();
            var indexes = await NpgsqlIngestOps.CleanGinPendingListsAsync(reader);
            var deferred = Assert.Single(indexes, index =>
                index.Index.EndsWith("writer_completion_unowned_probe_gin", StringComparison.Ordinal));
            Assert.Null(deferred.PagesCleaned);

            var writer = new NpgsqlSubstrateWriter(restricted, NullLogger<NpgsqlSubstrateWriter>.Instance);
            await writer.CompleteBulkRunAsync();
            await writer.CompleteBulkRunAsync();

            await using var pending = conn.CreateCommand();
            pending.CommandText = "SELECT pending_pages FROM public.pgstatginindex('laplace.writer_completion_unowned_probe_gin'::regclass)";
            Assert.True(Convert.ToInt64(await pending.ExecuteScalarAsync()) > 0);
            await using var read = reader.CreateCommand();
            read.CommandText = "SELECT count(*) FROM laplace.writer_completion_unowned_probe WHERE ids @> ARRAY[50]";
            Assert.Equal(2L, Convert.ToInt64(await read.ExecuteScalarAsync()));
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = "DROP TABLE laplace.writer_completion_unowned_probe";
            await cleanup.ExecuteNonQueryAsync();
        }
    }

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
