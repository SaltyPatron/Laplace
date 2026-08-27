using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class FoldPlanRegressionTests(LocalPgFixture pg)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fold_AfterEmptyPartitionPlan_ProbesKeysInsteadOfRescanningTheRelation(bool routed)
    {
        const int count = 4096;
        string tag = Guid.NewGuid().ToString("N");
        string relation = "fold_plan_" + tag;
        byte[] type = Hash128.OfCanonical(relation).ToBytes();
        string typeLiteral = "'\\x" + Convert.ToHexString(type) + "'::bytea";
        byte[][] subjects = Enumerable.Range(0, count)
            .Select(i => Hash128.OfCanonical($"{tag}/{i}").ToBytes()).ToArray();
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await Execute($"CREATE TABLE laplace.{relation} PARTITION OF laplace.consensus "
            + $"FOR VALUES IN ({typeLiteral}) PARTITION BY HASH (subject_id)");
        for (int i = 0; i < 8; i++)
            await Execute($"CREATE TABLE laplace.{relation}_{i} PARTITION OF laplace.{relation} "
                + $"FOR VALUES WITH (MODULUS 8, REMAINDER {i})");
        await Execute($"ANALYZE laplace.{relation}");
        await Execute("SET LOCAL plan_cache_mode=force_generic_plan; "
            + "SET LOCAL enable_hashjoin=off; SET LOCAL enable_mergejoin=off; "
            + "SET LOCAL enable_material=on");
        // Cache plans while the relation is empty, then grow it without ANALYZE.
        // This is the foundation-seed transition, not a throughput benchmark.
        await Fold(subjects[..1]);
        await using (var seed = new NpgsqlCommand($"""
            INSERT INTO laplace.{relation}
                (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
            SELECT laplace.consensus_id(s,{typeLiteral},s),s,{typeLiteral},s,
                1500000000000,350000000000,60000000,1,'2026-01-01'::timestamptz
            FROM unnest($1::bytea[]) s
            ON CONFLICT DO NOTHING
            """, conn, tx))
        {
            seed.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, subjects);
            await seed.ExecuteNonQueryAsync();
        }

        await Execute("LOAD 'auto_explain'; SET LOCAL auto_explain.log_min_duration=0; "
            + "SET LOCAL auto_explain.log_nested_statements=on; SET LOCAL auto_explain.log_analyze=on; "
            + "SET LOCAL auto_explain.log_timing=off; SET LOCAL auto_explain.log_format=text; "
            + "SET LOCAL auto_explain.log_parameter_max_length=0; "
            + "SET LOCAL auto_explain.log_level=notice");
        var plans = new List<string>();
        conn.Notice += (_, args) =>
        {
            string message = args.Notice.MessageText;
            if (message.Contains("Query Text: SELECT b.ord")
                || message.Contains("Query Text: MERGE INTO laplace.consensus"))
                plans.Add(message);
        };
        Assert.Equal(count, await Fold(subjects));
        await Execute("SET LOCAL auto_explain.log_min_duration=-1");
        Assert.Equal(2, plans.Count);
        foreach (var plan in plans)
        {
            Assert.DoesNotContain("Join Filter:", plan);
            Assert.Contains(plan.Split('\n'), line => line.Contains("Index Cond:")
                && line.Contains("b.id") && line.Contains("b.s"));
        }
        await using var verify = new NpgsqlCommand(
            $"SELECT count(*) FROM laplace.{relation} WHERE witness_count=2", conn, tx);
        Assert.Equal((long)count, (long)(await verify.ExecuteScalarAsync())!);
        await using var settings = new NpgsqlCommand(
            "SELECT current_setting('plan_cache_mode'),current_setting('enable_material')", conn, tx);
        await using (var result = await settings.ExecuteReaderAsync())
        {
            Assert.True(await result.ReadAsync());
            Assert.Equal("force_generic_plan", result.GetString(0));
            Assert.Equal("on", result.GetString(1));
        }
        await tx.RollbackAsync();

        async Task Execute(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync();
        }

        async Task<long> Fold(byte[][] ids)
        {
            await using var cmd = new NpgsqlCommand(routed
                ? "SELECT consensus.upsert_type($1,$2,$3,$4,$5,$6,$7,$8)"
                : "SELECT consensus.upsert($2,$1,$3,$4,$5,$6,$7,$8)", conn, tx);
            cmd.Parameters.AddWithValue(routed ? NpgsqlDbType.Bytea : NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                routed ? type : Enumerable.Repeat(type, ids.Length).ToArray());
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, ids);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, ids);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                Enumerable.Repeat(30_000_000_000L, ids.Length).ToArray());
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                Enumerable.Repeat(1L, ids.Length).ToArray());
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                Enumerable.Repeat(900_000_000L, ids.Length).ToArray());
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
                Enumerable.Repeat(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), ids.Length).ToArray());
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                Enumerable.Repeat(1_820_000_000_000L, ids.Length).ToArray());
            return (long)(await cmd.ExecuteScalarAsync())!;
        }
    }
}
