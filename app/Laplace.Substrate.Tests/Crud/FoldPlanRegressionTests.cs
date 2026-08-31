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
    public async Task Fold_NovelMatchedAndMixed_UsesKeyedPersistenceWithoutTargetMergeScans(bool routed)
    {
        const int count = 4096;
        string tag = Guid.NewGuid().ToString("N");
        string relation = "fold_plan_" + tag;
        byte[] type = Hash128.OfCanonical(relation).ToBytes();
        string typeLiteral = "'\\x" + Convert.ToHexString(type) + "'::bytea";
        byte[][] subjects = Enumerable.Range(0, count)
            .Select(i => Hash128.OfCanonical($"{tag}/existing/{i}").ToBytes()).ToArray();
        byte[][] novelHalf = Enumerable.Range(0, count / 2)
            .Select(i => Hash128.OfCanonical($"{tag}/novel/{i}").ToBytes()).ToArray();
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await Execute($"CREATE TABLE laplace.{relation} PARTITION OF laplace.consensus "
            + $"FOR VALUES IN ({typeLiteral}) PARTITION BY HASH (subject_id)");
        for (int i = 0; i < 8; i++)
            await Execute($"CREATE TABLE laplace.{relation}_{i} PARTITION OF laplace.{relation} "
                + $"FOR VALUES WITH (MODULUS 8, REMAINDER {i})");
        await Execute($"ANALYZE laplace.{relation}");

        // Reproduce the hostile seed-time planner environment from #1370. The
        // production repair must be structural, not dependent on these caller GUCs.
        await Execute("SET LOCAL plan_cache_mode=force_generic_plan; "
            + "SET LOCAL enable_hashjoin=off; SET LOCAL enable_mergejoin=off; "
            + "SET LOCAL enable_material=on; SET LOCAL enable_seqscan=on");
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
                || message.Contains("Query Text: INSERT INTO laplace.consensus")
                || message.Contains("Query Text: MERGE INTO laplace.consensus"))
                plans.Add(message);
        };

        // First fill: every cell is novel. This is the exact shape the former
        // test skipped and the live CILI run made catastrophic.
        var novelPlans = await Capture(() => Fold(subjects));
        AssertPersistencePlan(novelPlans, matched: false, novel: true);

        // Re-fold: every cell is matched and was row-locked by the keyed prior probe.
        var matchedPlans = await Capture(() => Fold(subjects));
        AssertPersistencePlan(matchedPlans, matched: true, novel: false);

        // Mixed: half the batch already exists and half is new.
        byte[][] mixed = subjects[..(count / 2)].Concat(novelHalf).ToArray();
        var mixedPlans = await Capture(() => Fold(mixed));
        AssertPersistencePlan(mixedPlans, matched: true, novel: true);

        await Execute("SET LOCAL auto_explain.log_min_duration=-1");
        await using (var verify = new NpgsqlCommand(
            $"SELECT count(*), count(*) FILTER (WHERE witness_count >= 2) "
            + $"FROM laplace.{relation}", conn, tx))
        await using (var result = await verify.ExecuteReaderAsync())
        {
            Assert.True(await result.ReadAsync());
            Assert.Equal((long)(count + count / 2), result.GetInt64(0));
            Assert.Equal((long)count, result.GetInt64(1));
        }

        // Function-local planner settings must restore the hostile caller state on return.
        await using var settings = new NpgsqlCommand(
            "SELECT current_setting('plan_cache_mode'), current_setting('enable_material'), "
            + "current_setting('enable_seqscan')", conn, tx);
        await using (var result = await settings.ExecuteReaderAsync())
        {
            Assert.True(await result.ReadAsync());
            Assert.Equal("force_generic_plan", result.GetString(0));
            Assert.Equal("on", result.GetString(1));
            Assert.Equal("on", result.GetString(2));
        }
        await tx.RollbackAsync();

        async Task<List<string>> Capture(Func<Task<long>> action)
        {
            int start = plans.Count;
            Assert.Equal(count, await action());
            return plans.Skip(start).ToList();
        }

        void AssertPersistencePlan(IReadOnlyCollection<string> phasePlans, bool matched, bool novel)
        {
            Assert.DoesNotContain(phasePlans, p => p.Contains("Query Text: MERGE INTO laplace.consensus"));
            Assert.DoesNotContain(phasePlans, p => Enumerable.Range(0, 8)
                .Any(i => p.Contains($"Seq Scan on {relation}_{i}")));
            Assert.Contains(phasePlans, p => p.Contains("Query Text: SELECT b.ord")
                && p.Contains("Index Scan") && p.Contains("Index Cond:")
                && p.Contains("b.id") && p.Contains("b.s"));
            Assert.Equal(matched, phasePlans.Any(p => p.Contains("WHERE b.seen ")));
            Assert.Equal(novel, phasePlans.Any(p => p.Contains("WHERE NOT b.seen")));
            if (matched)
            {
                Assert.Contains(phasePlans, p =>
                    p.Contains("ON CONFLICT (id, type_id, subject_id) DO UPDATE")
                    && p.Split('\n').Any(line => line.Contains("Conflict Arbiter Indexes:")
                        && line.Contains("consensus_pkey")));
            }
        }

        async Task Execute(string text)
        {
            await using var command = new NpgsqlCommand(text, conn, tx);
            await command.ExecuteNonQueryAsync();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fold_KeyedPersistence_LeafScansAreCallBounded_AndSettingsRestoreAfterError(bool routed)
    {
        const int count = 4096;
        string tag = Guid.NewGuid().ToString("N");
        string relation = "fold_counter_" + tag;
        byte[] type = Hash128.OfCanonical(relation).ToBytes();
        string typeLiteral = "'\\x" + Convert.ToHexString(type) + "'::bytea";
        byte[][] subjects = Enumerable.Range(0, count)
            .Select(i => Hash128.OfCanonical($"{tag}/existing/{i}").ToBytes()).ToArray();
        byte[][] novelHalf = Enumerable.Range(0, count / 2)
            .Select(i => Hash128.OfCanonical($"{tag}/novel/{i}").ToBytes()).ToArray();

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
            + "SET LOCAL enable_material=on; SET LOCAL enable_seqscan=on");

        // pg_stat_all_tables does not include this backend's in-flight transaction.
        // pg_stat_xact_all_tables does, continuously. Snapshot after DDL/ANALYZE and
        // subtract so this assertion cannot false-green on an unflushed zero counter.
        var before = await LeafCounters();

        Assert.Equal(count, await Fold(subjects));
        Assert.Equal(count, await Fold(subjects));
        byte[][] mixed = subjects[..(count / 2)].Concat(novelHalf).ToArray();
        Assert.Equal(count, await Fold(mixed));

        var after = await LeafCounters();
        long leafSeqScans = after.Seq - before.Seq;
        long leafIndexScans = after.Idx - before.Idx;
        Assert.True(leafSeqScans >= 0, $"leaf seq_scan counter moved backwards: {leafSeqScans}");
        Assert.True(leafSeqScans <= 24,
            $"target leaf sequential scans must be O(calls x leaves), got {leafSeqScans} for {count} rows");
        Assert.True(leafSeqScans < count,
            $"target leaf scans scaled with rows: {leafSeqScans} scans for {count} rows");
        Assert.True(leafIndexScans > 0,
            "the keyed prior/matched paths must exercise target indexes; zero index scans is not proof");

        // Exercise the error unwind under the same hostile caller GUCs. A savepoint
        // lets the test inspect the session after the function error instead of leaving
        // the transaction aborted. Function-local SET clauses and native SPI cleanup
        // must restore every caller-owned setting on both success and failure.
        await tx.SaveAsync("fold_failure");
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var bad = new NpgsqlCommand(routed
                ? "SELECT consensus.upsert_type($1,$2,$3,$4,$5,$6,$7,$8)"
                : "SELECT consensus.upsert($2,$1,$3,$4,$5,$6,$7,$8)", conn, tx);
            bad.Parameters.AddWithValue(routed ? NpgsqlDbType.Bytea : NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                routed ? type : new[] { type });
            bad.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { subjects[0] });
            bad.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, Array.Empty<byte[]>());
            bad.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, new[] { 30_000_000_000L });
            bad.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, new[] { 1L });
            bad.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, new[] { 900_000_000L });
            bad.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
                new[] { new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) });
            bad.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                new[] { 1_820_000_000_000L });
            await bad.ExecuteScalarAsync();
        });
        await tx.RollbackAsync("fold_failure");

        await using (var settings = new NpgsqlCommand(
            "SELECT current_setting('plan_cache_mode'), current_setting('enable_hashjoin'), "
            + "current_setting('enable_mergejoin'), current_setting('enable_material'), "
            + "current_setting('enable_seqscan')", conn, tx))
        await using (var result = await settings.ExecuteReaderAsync())
        {
            Assert.True(await result.ReadAsync());
            Assert.Equal("force_generic_plan", result.GetString(0));
            Assert.Equal("off", result.GetString(1));
            Assert.Equal("off", result.GetString(2));
            Assert.Equal("on", result.GetString(3));
            Assert.Equal("on", result.GetString(4));
        }

        await tx.RollbackAsync();

        async Task<(long Seq, long Idx)> LeafCounters()
        {
            await using var counters = new NpgsqlCommand(
                $"SELECT COALESCE(sum(seq_scan), 0)::bigint, COALESCE(sum(idx_scan), 0)::bigint "
                + $"FROM pg_stat_xact_all_tables "
                + $"WHERE schemaname='laplace' AND relname LIKE '{relation}_%'", conn, tx);
            await using var result = await counters.ExecuteReaderAsync();
            Assert.True(await result.ReadAsync());
            return (result.GetInt64(0), result.GetInt64(1));
        }

        async Task Execute(string sql)
        {
            await using var command = new NpgsqlCommand(sql, conn, tx);
            await command.ExecuteNonQueryAsync();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fold_ConcurrentNovelInsert_UsesCollisionFallbackWithoutLosingEitherFold(bool routed)
    {
        string tag = Guid.NewGuid().ToString("N");
        string relation = "fold_collision_" + tag;
        string blockerFunction = "fold_collision_block_" + tag;
        byte[] type = Hash128.OfCanonical(relation).ToBytes();
        byte[] collisionSubject = Hash128.OfCanonical($"{tag}/collision").ToBytes();
        byte[] sequentialSubject = Hash128.OfCanonical($"{tag}/sequential").ToBytes();
        string typeLiteral = "'\\x" + Convert.ToHexString(type) + "'::bytea";
        string collisionLiteral = "'\\x" + Convert.ToHexString(collisionSubject) + "'::bytea";
        int lockClass = 1370;
        int lockKey = Random.Shared.Next(1, int.MaxValue);

        await using var control = await pg.DataSource.OpenConnectionAsync();
        await using var primary = await pg.DataSource.OpenConnectionAsync();
        bool lockHeld = false;
        Task<long>? primaryFold = null;

        try
        {
            await Execute(control, $"CREATE TABLE laplace.{relation} PARTITION OF laplace.consensus "
                + $"FOR VALUES IN ({typeLiteral}) PARTITION BY HASH (subject_id)");
            for (int i = 0; i < 8; i++)
                await Execute(control, $"CREATE TABLE laplace.{relation}_{i} PARTITION OF laplace.{relation} "
                    + $"FOR VALUES WITH (MODULUS 8, REMAINDER {i})");
            await Execute(control, $@"
CREATE FUNCTION laplace.{blockerFunction}() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.subject_id = {collisionLiteral}
       AND current_setting('application_name') = 'fold-collision-primary' THEN
        PERFORM pg_advisory_xact_lock({lockClass}, {lockKey});
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER {blockerFunction}
BEFORE INSERT ON laplace.{relation}
FOR EACH ROW EXECUTE FUNCTION laplace.{blockerFunction}();
ANALYZE laplace.{relation};");

            // The sequential subject is the exact state expected after two folds.
            Assert.Equal(1, await Fold(control, sequentialSubject));
            Assert.Equal(1, await Fold(control, sequentialSubject));
            var expected = await State(control, sequentialSubject);

            await Execute(control, $"SELECT pg_advisory_lock({lockClass}, {lockKey})");
            lockHeld = true;
            await Execute(primary, "SET application_name='fold-collision-primary'; "
                + "LOAD 'auto_explain'; SET auto_explain.log_min_duration=0; "
                + "SET auto_explain.log_nested_statements=on; SET auto_explain.log_analyze=on; "
                + "SET auto_explain.log_timing=off; SET auto_explain.log_level=notice");
            int primaryPid = (int)(await new NpgsqlCommand(
                "SELECT pg_backend_pid()", primary).ExecuteScalarAsync())!;
            var plans = new List<string>();
            primary.Notice += (_, args) =>
            {
                if (args.Notice.MessageText.Contains("Query Text: MERGE INTO laplace.consensus"))
                    plans.Add(args.Notice.MessageText);
            };

            primaryFold = Fold(primary, collisionSubject);
            await WaitForAdvisoryLock(primaryPid);

            // The second backend commits the same phase-1-novel cell while the
            // first backend is paused in its BEFORE INSERT trigger. Releasing the
            // trigger makes the first insert collide and forces the race-only MERGE.
            Assert.Equal(1, await Fold(control, collisionSubject));
            await Execute(control, $"SELECT pg_advisory_unlock({lockClass}, {lockKey})");
            lockHeld = false;
            Assert.Equal(1, await primaryFold.WaitAsync(TimeSpan.FromSeconds(15)));

            var actual = await State(control, collisionSubject);
            Assert.Equal(expected, actual);
            Assert.Equal(2, actual.Witnesses);
            Assert.Contains(plans, p => p.Contains("Query Text: MERGE INTO laplace.consensus"));
        }
        finally
        {
            if (lockHeld)
                await Execute(control, $"SELECT pg_advisory_unlock({lockClass}, {lockKey})");
            if (primaryFold is not null && !primaryFold.IsCompleted)
            {
                try { await primaryFold.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch { /* Preserve the original assertion/setup failure. */ }
            }
            await Execute(control, $"DROP TABLE IF EXISTS laplace.{relation} CASCADE; "
                + $"DROP FUNCTION IF EXISTS laplace.{blockerFunction}()");
        }

        async Task WaitForAdvisoryLock(int pid)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT wait_event_type = 'Lock' AND wait_event = 'advisory' "
                    + "FROM pg_stat_activity WHERE pid = $1", control);
                cmd.Parameters.AddWithValue(pid);
                if (await cmd.ExecuteScalarAsync() is true)
                    return;
                await Task.Delay(20);
            }
            throw new TimeoutException("primary fold never reached the collision trigger");
        }

        async Task<(long Rating, long Rd, long Volatility, long Witnesses)> State(
            NpgsqlConnection conn, byte[] subject)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT rating, rd, volatility, witness_count FROM laplace.consensus "
                + "WHERE type_id=$1 AND subject_id=$2", conn);
            cmd.Parameters.AddWithValue(type);
            cmd.Parameters.AddWithValue(subject);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }

        async Task<long> Fold(NpgsqlConnection conn, byte[] subject)
        {
            await using var cmd = new NpgsqlCommand(routed
                ? "SELECT consensus.upsert_type($1,$2,$3,$4,$5,$6,$7,$8)"
                : "SELECT consensus.upsert($2,$1,$3,$4,$5,$6,$7,$8)", conn)
            {
                CommandTimeout = 15,
            };
            cmd.Parameters.AddWithValue(
                routed ? NpgsqlDbType.Bytea : NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                routed ? type : new[] { type });
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { subject });
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { subject });
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, new[] { 30_000_000_000L });
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, new[] { 1L });
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, new[] { 900_000_000L });
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
                new[] { new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) });
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                new[] { 1_820_000_000_000L });
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        static async Task Execute(NpgsqlConnection conn, string sql)
        {
            await using var command = new NpgsqlCommand(sql, conn);
            await command.ExecuteNonQueryAsync();
        }
    }
}
