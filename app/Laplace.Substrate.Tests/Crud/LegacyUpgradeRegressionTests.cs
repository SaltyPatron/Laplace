using Laplace.Decomposers.Abstractions.Tests;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class LegacyUpgradeRegressionTests(LocalPgFixture pg)
{
    [Fact]
    public async Task ExistingEvidence_GainsNeutralOpponentWithoutLosingWitnessFields()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // Recreate the pre-opponent-column state in this disposable fixture only.
        // The transaction restores the extension schema and all fixture rows.
        await Execute("""
            ALTER TABLE laplace.attestations DROP COLUMN opponent_rating_fp1e9;
            INSERT INTO laplace.attestations
                (id,subject_id,type_id,source_id,outcome,last_observed_at,
                 observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
            VALUES (decode('71aabe37f17c4c8f925517685224f901','hex'),
                    decode('71aabe37f17c4c8f925517685224f902','hex'),
                    decode('71aabe37f17c4c8f925517685224f903','hex'),
                    decode('71aabe37f17c4c8f925517685224f904','hex'),
                    2,'2026-01-01',7,6300000000,30000000000);
            SET LOCAL search_path=laplace,public;
            """);
        string module = Path.Combine(TypeIdLawTests.FindRepoRootPublic(),
            "extension", "laplace_substrate", "sql", "schema", "tables", "attestations.sql.in");
        string ddl = await File.ReadAllTextAsync(module);
        // pg_extension_config_dump belongs to CREATE/ALTER EXTENSION, not DDL replay.
        ddl = ddl[..ddl.IndexOf("SELECT pg_extension_config_dump", StringComparison.Ordinal)];
        await Execute(ddl);
        await Execute(ddl); // Upgrade is idempotent.
        await using var verify = new NpgsqlCommand("""
            SELECT opponent_rating_fp1e9,observation_count,sum_score_fp1e9,
                   opponent_rd_fp1e9,outcome
            FROM laplace.attestations
            WHERE id=decode('71aabe37f17c4c8f925517685224f901','hex')
            """, conn, tx);
        await using (var rows = await verify.ExecuteReaderAsync())
        {
            Assert.True(await rows.ReadAsync());
            Assert.Equal(1_500_000_000_000L, rows.GetInt64(0));
            Assert.Equal(7L, rows.GetInt64(1));
            Assert.Equal(6_300_000_000L, rows.GetInt64(2));
            Assert.Equal(30_000_000_000L, rows.GetInt64(3));
            Assert.Equal((short)2, rows.GetInt16(4));
            Assert.False(await rows.ReadAsync());
        }
        await tx.RollbackAsync();

        async Task Execute(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PartitionPressure_ExecutesWithRealPgStatsFrequencyTypes()
    {
        // Text-presence tests missed round(double precision, integer), which
        // PostgreSQL cannot resolve even when the MCV roster is empty.
        await using var cmd = pg.DataSource.CreateCommand(
            "SELECT tbl,relation,type_id,rows,pct_of_default FROM ops.consensus_partition_pressure()");
        await using var rows = await cmd.ExecuteReaderAsync();
        Assert.Equal(5, rows.FieldCount);
        Assert.Equal("numeric", rows.GetDataTypeName(4));
        while (await rows.ReadAsync())
            Assert.InRange(rows.GetDecimal(4), 0m, 100m);
    }
}
