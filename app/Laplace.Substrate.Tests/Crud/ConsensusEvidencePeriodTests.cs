using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Laplace.Decomposers.Abstractions.Tests;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class ConsensusEvidencePeriodTests(LocalPgFixture pg)
{
    private const long Scale = 1_000_000_000;
    private static Hash128 Id(string value) => Hash128.OfCanonical("test/evidence-period/" + value);
    private sealed record Witness(int Index, long Games, long Score, long Opponent = 1_756_000_000_000,
        long Rd = 62_000_000_000, bool Replayable = true);
    private sealed record Cell(Hash128 Subject, Hash128 Type, Hash128? Object, Hash128 Source);
    private sealed record Standing(long Rating, long Rd, long Volatility, long Witnesses, DateTime Time);

    private static Cell Target(string name) => new(Id(name + "/subject"), Id(name + "/type"),
        Id(name + "/object"), Id(name + "/source"));
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalAcceptedEvidenceIsExactAcrossChunkPartitionsShuffleReplayAndCanonicalFold(bool unary)
    {
        var cell = Target("partition-" + unary);
        if (unary) cell = cell with { Object = null };
        Witness[] rows = [
            new(0,458,458*Scale),new(1,458,0),new(2,458,458*Scale),
            new(3,37,18*Scale,1_600_000_000_000,90_000_000_000),
            new(4,53,21*Scale,1_300_000_000_000,170_000_000_000),
            new(5,11,5*Scale),new(6,17,9*Scale)];
        int[][] schedules = [
            [0,1,2,3,4,5,6],
            [6,4,2,0,5,3,1],
            [1,5,0,6,3,2,4]];
        int[][] widths = [[7],[1,1,1,1,1,1,1],[2,3,2]];
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        Standing? first = null;
        for (int plan=0;plan<schedules.Length;plan++)
        {
            await ExecuteAsync(connection,"SAVEPOINT identical_evidence");
            int offset=0;
            foreach (int width in widths[plan])
            {
                var chunk=schedules[plan].Skip(offset).Take(width).Select(i=>rows[i]).ToArray();
                await InsertAsync(connection,cell,chunk);
                Assert.Equal(1L,await FoldAsync(connection,cell,chunk));
                offset+=width;
            }
            var actual=await ReadAsync(connection,cell);
            Assert.Equal(rows.Sum(x=>x.Games),actual.Witnesses);
            Assert.Equal(Epoch.AddSeconds(6),actual.Time);
            Assert.Equal(await CanonicalAsync(connection,cell),actual);
            if (first is null) first=actual; else Assert.Equal(first,actual);

            // The exact same A IDs and bodies are replayed. The direct evidence
            // route is idempotent even if an orchestration caller repeats it.
            await InsertAsync(connection,cell,rows);
            Assert.Equal(1L,await FoldAsync(connection,cell,rows));
            Assert.Equal(actual,await ReadAsync(connection,cell));
            Assert.Equal(7L,await EvidenceCountAsync(connection,cell));
            await ExecuteAsync(connection,"ROLLBACK TO SAVEPOINT identical_evidence");
        }
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task CommittedEvidenceBackedStandingIsUnchangedByActualSourceRefold()
    {
        const string sourceName="ConsensusEvidencePeriodRefold";
        var cell=Target("source-refold") with { Source=SubstrateCanonicalIds.Source(sourceName) };
        try
        {
            await using var connection=await OpenAsync();
            Witness[] rows=[
                new(0,89,89*Scale),new(1,20,0),new(2,49,49*Scale),
                new(3,57,57*Scale),new(4,36,0),new(5,70,35*Scale),new(6,18,0)];
            await using (var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted))
            {
                foreach (var chunk in rows.Chunk(2))
                {
                    await InsertAsync(connection,cell,chunk);
                    await FoldAsync(connection,cell,chunk);
                }
                await transaction.CommitAsync();
            }
            var before=await ReadAsync(connection,cell);
            Assert.Equal(rows.Sum(x=>x.Games),before.Witnesses);
            Assert.Equal(await CanonicalAsync(connection,cell),before);
            // This procedure commits its bounded relation batches itself, so it
            // must be invoked outside the fixture's admission transaction.
            await using (var refold=connection.CreateCommand())
            {
                refold.CommandText="CALL ops.refold_source(@name)";
                refold.CommandTimeout=20;
                refold.Parameters.AddWithValue("name",sourceName);
                await refold.ExecuteNonQueryAsync();
            }
            Assert.Equal(before,await ReadAsync(connection,cell));
            Assert.Equal(7L,await EvidenceCountAsync(connection,cell));
        }
        finally { await CleanupAsync(cell); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAcceptedEvidenceAfterTargetLockIsIncluded(bool existing)
    {
        var cell=Target("concurrent-"+existing);
        try
        {
            await using var first=await OpenAsync();
            await using var second=await OpenAsync();
            await using var observer=await OpenAsync();
            if (existing)
            {
                Witness[] seed=[new(0,7,3*Scale)];
                await InsertAsync(first,cell,seed);
                await FoldAsync(first,cell,seed);
            }
            await using var firstTransaction=await first.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await using var secondTransaction=await second.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            Witness[] a=[new(1,19,19*Scale)];
            Witness[] b=[new(2,23,0)];
            await InsertAsync(first,cell,a);
            await FoldAsync(first,cell,a);
            await InsertAsync(second,cell,b);
            await using var pending=new RunningFold(second,cell,b);
            await WaitForBlockerAsync(observer,second.ProcessID,first.ProcessID);
            await firstTransaction.CommitAsync();
            Assert.Equal(1L,(long)(await pending.Completion.WaitAsync(TimeSpan.FromSeconds(15)))!);
            await secondTransaction.CommitAsync();
            var actual=await ReadAsync(observer,cell);
            Assert.Equal(existing?49L:42L,actual.Witnesses);
            Assert.Equal(await CanonicalAsync(observer,cell),actual);
            Assert.Equal(existing?3L:2L,await EvidenceCountAsync(observer,cell));
        }
        finally { await CleanupAsync(cell); }
    }

    [Fact]
    public async Task ActualSourceRefoldWaitsForConcurrentAdmissionAndReadsItsCommittedEvidence()
    {
        const string sourceName="ConsensusEvidencePeriodConcurrentRefold";
        var cell=Target("concurrent-source-refold") with { Source=SubstrateCanonicalIds.Source(sourceName) };
        try
        {
            await using var writer=await OpenAsync();
            await using var repair=await OpenAsync();
            await using var observer=await OpenAsync();
            Witness[] seed=[new(0,7,3*Scale)];
            await InsertAsync(writer,cell,seed);
            await FoldAsync(writer,cell,seed);
            await using var transaction=await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            Witness[] accepted=[new(1,31,31*Scale)];
            await InsertAsync(writer,cell,accepted);
            await FoldAsync(writer,cell,accepted);
            await using var pending=new RunningFold(RefoldCommand(repair,sourceName));
            await WaitForBlockerAsync(observer,repair.ProcessID,writer.ProcessID);
            await transaction.CommitAsync();
            await pending.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            var actual=await ReadAsync(observer,cell);
            Assert.Equal(38L,actual.Witnesses);
            Assert.Equal(Epoch.AddSeconds(1),actual.Time);
            Assert.Equal(await CanonicalAsync(observer,cell),actual);
            Assert.Equal(2L,await EvidenceCountAsync(observer,cell));
        }
        finally { await CleanupAsync(cell); }
    }

    [Fact]
    public async Task AnyRetainedTransientReceiptPreservesContinuousScoreAndStoredPrior()
    {
        var cell=Target("transient");
        await using var connection=await OpenAsync();
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        Witness[] transient=[new(0,5,5*Scale,Replayable:false)];
        await InsertAsync(connection,cell,transient);
        // The durable receipt says Confirm, but its actual transient native
        // score is .713579246. Substituting the receipt's 1.0 would change standing.
        const long continuous=713_579_246;
        Witness[] exact=[transient[0] with { Score=5*continuous }];
        Assert.Equal(1L,await FoldAsync(connection,cell,exact));
        var first=await ReadAsync(connection,cell);
        var neutral=new Standing(1_500_000_000_000,350_000_000_000,60_000_000,0,Epoch);
        Assert.Equal(await DeltaMathAsync(connection,neutral,exact[0]),first);
        Assert.NotEqual((await DeltaMathAsync(connection,neutral,transient[0])).Rating,first.Rating);

        // A later replayable witness still cannot replace the earlier exact
        // continuous update with a categorical all-A refold.
        Witness[] later=[new(1,3,0)];
        await InsertAsync(connection,cell,later);
        await FoldAsync(connection,cell,later);
        Assert.Equal(await DeltaMathAsync(connection,first,later[0]),await ReadAsync(connection,cell));
        Assert.Equal(2L,await EvidenceCountAsync(connection,cell));
        var beforeRefusal=await ReadAsync(connection,cell);
        await ExecuteAsync(connection,"SAVEPOINT mixed_refold_refusal");
        await using (var refold=Command(connection,
            "SELECT consensus.refold_evidence_type(@type,ARRAY[@subject],ARRAY[@object])",cell))
        {
            var error=await Assert.ThrowsAsync<PostgresException>(async()=> { await refold.ExecuteScalarAsync(); });
            Assert.Equal(PostgresErrorCodes.DataException,error.SqlState);
            Assert.Contains("non-replayable",error.MessageText,StringComparison.Ordinal);
        }
        await ExecuteAsync(connection,"ROLLBACK TO SAVEPOINT mixed_refold_refusal");
        Assert.Equal(beforeRefusal,await ReadAsync(connection,cell));
        Assert.Equal(2L,await EvidenceCountAsync(connection,cell));
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDurableEvidenceRollsBackEveryNeutralPlaceholder(bool repair)
    {
        var cell=Target("missing-"+repair);
        await using var connection=await OpenAsync();
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var request=repair
            ? Command(connection,"SELECT consensus.refold_evidence_type(@type,ARRAY[@subject],ARRAY[@object])",cell)
            : FoldCommand(connection,cell,[new(0,1,Scale)]);
        var error=await Assert.ThrowsAsync<PostgresException>(
            async()=> { await request.ExecuteScalarAsync(); });
        Assert.Equal(PostgresErrorCodes.DataException,error.SqlState);
        await transaction.RollbackAsync();
        await using var check=Command(connection,"SELECT count(*) FROM laplace.consensus WHERE type_id=@type AND subject_id=@subject",cell);
        Assert.Equal(0L,(long)(await check.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task TransactionSnapshotRefusesBeforeTargetsAreCreated(IsolationLevel isolation)
    {
        var cell=Target("isolation-"+isolation);
        await using var connection=await OpenAsync();
        await using var transaction=await connection.BeginTransactionAsync(isolation);
        Witness[] rows=[new(0,1,Scale)];
        await InsertAsync(connection,cell,rows);
        var error=await Assert.ThrowsAsync<PostgresException>(async()=> { await FoldAsync(connection,cell,rows); });
        Assert.Equal(PostgresErrorCodes.SerializationFailure,error.SqlState);
        Assert.Contains("READ COMMITTED",error.MessageText,StringComparison.Ordinal);
        await transaction.RollbackAsync();
        Assert.Equal(0L,await EvidenceCountAsync(connection,cell));
        await using var check=Command(connection,"SELECT count(*) FROM laplace.consensus WHERE type_id=@type AND subject_id=@subject",cell);
        Assert.Equal(0L,(long)(await check.ExecuteScalarAsync())!);
    }


    [Fact]
    public async Task ProductionEvidenceQueryPreservesWholeCellsOrdinalsAndEveryObservation()
    {
        // Execute the SQL owned by the C route with the installed native aggregate.
        // Missing and duplicate requested cells are query-level cases; the public
        // mutating route separately owns missing-evidence/duplicate-target refusal.
        string name = "query-guard-" + Guid.NewGuid().ToString("N");
        var good = Target(name + "/good");
        var mixed = Target(name + "/mixed") with { Type = good.Type };
        var transient = Target(name + "/transient") with { Type = good.Type };
        var unary = Target(name + "/unary") with { Type = good.Type, Object = null };
        var missing = Target(name + "/missing") with { Type = good.Type };
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        Witness[] rows = [
            new(0,1,0,1_500_000_000_000,350_000_000_000),
            new(1,2,2*Scale,1_500_000_000_000,350_000_000_000),
            new(2,5,2*Scale,1_600_000_000_000,90_000_000_000),
            new(3,3,Scale,1_300_000_000_000,170_000_000_000)];
        await InsertAsync(connection, good, rows);
        await using (var equalTimes = Command(connection,
            "UPDATE laplace.attestations SET last_observed_at=@time "
            + "WHERE type_id=@type AND subject_id=@subject", good))
        {
            equalTimes.Parameters.AddWithValue("time", NpgsqlDbType.TimestampTz, Epoch);
            Assert.Equal(rows.Length, await equalTimes.ExecuteNonQueryAsync());
        }
        // These retained mathematical inputs are deliberately invalid. A false
        // flag excludes its whole cell before the native aggregate is evaluated,
        // including the true row in the mixed cell. Filtering individual false
        // rows, or testing bool_and only after folding, would reach invalid math.
        await InsertAsync(connection, mixed,
            [new(0,1,long.MaxValue,Replayable:false), new(1,1,long.MaxValue)]);
        await InsertAsync(connection, transient,
            [new(0,1,long.MaxValue,Replayable:false), new(1,2,long.MaxValue,Replayable:false)]);
        await InsertAsync(connection, unary, [new(0,3,Scale), new(1,7,5*Scale)]);
        var goodExpected = await CanonicalAsync(connection, good);
        var unaryExpected = await CanonicalAsync(connection, unary);
        Assert.Equal(11L, goodExpected.Witnesses);
        Assert.Equal(Epoch, goodExpected.Time);
        Assert.Equal(10L, unaryExpected.Witnesses);
        Cell[] requested = [good, mixed, transient, unary, missing, good];

        string source = File.ReadAllText(Path.Combine(TypeIdLawTests.FindRepoRootPublic(),
            "extension", "laplace_substrate", "src", "fold_route.c"));
        const string marker = "static const char *EVIDENCE_FOLD_SQL =";
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "the native evidence query owner must exist");
        start += marker.Length;
        int end = source.IndexOf(";\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "the native query must have a complete literal");
        string query = string.Concat(source[start..end]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<string>(line.Trim())
                ?? throw new InvalidDataException("null native SQL fragment")));
        query = query.Replace("'\\x%s'::bytea", "@type", StringComparison.Ordinal)
            .Replace("$1", "@subjects", StringComparison.Ordinal)
            .Replace("$2", "@objects", StringComparison.Ordinal);
        Assert.DoesNotContain("%s", query, StringComparison.Ordinal);

        foreach (string mode in new[] { "force_custom_plan", "force_generic_plan" })
        {
            await ExecuteAsync(connection, $"SET LOCAL plan_cache_mode={mode}");
            var actual = await ReadQueryAsync(requested);
            Assert.Equal(6, actual.Length);
            Assert.Equal(new EvidenceResult(1,true,4,goodExpected), actual[0]);
            Assert.Equal(new EvidenceResult(2,false,2,null), actual[1]);
            Assert.Equal(new EvidenceResult(3,false,2,null), actual[2]);
            Assert.Equal(new EvidenceResult(4,true,2,unaryExpected), actual[3]);
            Assert.Equal(new EvidenceResult(5,null,null,null), actual[4]);
            Assert.Equal(actual[0] with { Ordinal = 6 }, actual[5]);
            Assert.Empty(await ReadQueryAsync([]));
        }

        // Production disallows NULL flags; do not silently fabricate a nullable
        // storage contract to exercise the window's SQL three-valued logic.
        await ExecuteAsync(connection, "SAVEPOINT null_replayable");
        await using (var invalid = Command(connection,
            "UPDATE laplace.attestations SET fold_replayable=NULL "
            + "WHERE type_id=@type AND subject_id=@subject", good))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(
                async () => { await invalid.ExecuteNonQueryAsync(); });
            Assert.Equal(PostgresErrorCodes.NotNullViolation, error.SqlState);
        }
        await ExecuteAsync(connection, "ROLLBACK TO SAVEPOINT null_replayable");
        Assert.Equal(goodExpected, await CanonicalAsync(connection, good));
        Assert.Equal(4L, await EvidenceCountAsync(connection, good));
        await transaction.RollbackAsync();

        async Task<EvidenceResult[]> ReadQueryAsync(Cell[] cells)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM ({query}) result ORDER BY ord";
            command.CommandTimeout = 20;
            command.Parameters.AddWithValue("type", NpgsqlDbType.Bytea, good.Type.ToBytes());
            command.Parameters.AddWithValue("subjects", NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                cells.Select(cell => cell.Subject.ToBytes()).ToArray());
            command.Parameters.AddWithValue("objects", NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                cells.Select(cell => cell.Object?.ToBytes()).ToArray());
            await command.PrepareAsync();
            var results = new List<EvidenceResult>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                bool? replayable = reader.IsDBNull(1) ? null : reader.GetBoolean(1);
                long? count = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                Standing? standing = null;
                if (replayable == true)
                {
                    standing = new Standing(reader.GetInt64(3),reader.GetInt64(4),
                        reader.GetInt64(5),reader.GetInt64(6),reader.GetDateTime(7).ToUniversalTime());
                }
                else
                {
                    for (int field=3;field<8;field++) Assert.True(reader.IsDBNull(field));
                }
                results.Add(new EvidenceResult(reader.GetInt64(0),replayable,count,standing));
            }
            return results.ToArray();
        }
    }

    private sealed record EvidenceResult(long Ordinal, bool? Replayable, long? Rows, Standing? Value);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection=await pg.DataSource.OpenConnectionAsync();
        try
        {
            await ExecuteAsync(connection,"SET default_transaction_isolation='read committed'; SET statement_timeout='20s'");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection,string sql,Cell cell)
    {
        var command=connection.CreateCommand();
        command.CommandText=sql;
        command.CommandTimeout=20;
        command.Parameters.AddWithValue("subject",NpgsqlDbType.Bytea,cell.Subject.ToBytes());
        command.Parameters.AddWithValue("type",NpgsqlDbType.Bytea,cell.Type.ToBytes());
        command.Parameters.AddWithValue("object",NpgsqlDbType.Bytea,(object?)cell.Object?.ToBytes() ?? DBNull.Value);
        command.Parameters.AddWithValue("source",NpgsqlDbType.Bytea,cell.Source.ToBytes());
        return command;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection,string sql)
    {
        await using var command=connection.CreateCommand();
        command.CommandText=sql;
        command.CommandTimeout=20;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertAsync(NpgsqlConnection connection,Cell cell,Witness[] rows)
    {
        await using var command=Command(connection,"""
            INSERT INTO laplace.attestations
              (id,subject_id,type_id,object_id,source_id,context_id,outcome,last_observed_at,
               observation_count,sum_score_fp1e9,opponent_rating_fp1e9,opponent_rd_fp1e9,fold_replayable)
            SELECT a.id,@subject,@type,@object,@source,a.context,2,a.ts,
                   a.games,a.score,a.opponent,a.rd,a.replayable
            FROM unnest(@ids::bytea[],@contexts::bytea[],@times::timestamptz[],
                        @games::bigint[],@scores::bigint[],@opponents::bigint[],
                        @rds::bigint[],@replayable::boolean[])
              AS a(id,context,ts,games,score,opponent,rd,replayable)
            ON CONFLICT (id,type_id,subject_id) DO NOTHING
            """,cell);
        command.Parameters.AddWithValue("ids",NpgsqlDbType.Array|NpgsqlDbType.Bytea,
            rows.Select(x=>NativeAttestation.ComputeId(cell.Subject,cell.Type,cell.Object,cell.Source,
                Id(cell.Subject+"/context/"+x.Index)).ToBytes()).ToArray());
        command.Parameters.AddWithValue("contexts",NpgsqlDbType.Array|NpgsqlDbType.Bytea,
            rows.Select(x=>Id(cell.Subject+"/context/"+x.Index).ToBytes()).ToArray());
        command.Parameters.AddWithValue("times",NpgsqlDbType.Array|NpgsqlDbType.TimestampTz,rows.Select(x=>Epoch.AddSeconds(x.Index)).ToArray());
        command.Parameters.AddWithValue("games",NpgsqlDbType.Array|NpgsqlDbType.Bigint,rows.Select(x=>x.Games).ToArray());
        command.Parameters.AddWithValue("scores",NpgsqlDbType.Array|NpgsqlDbType.Bigint,rows.Select(x=>x.Score).ToArray());
        command.Parameters.AddWithValue("opponents",NpgsqlDbType.Array|NpgsqlDbType.Bigint,rows.Select(x=>x.Opponent).ToArray());
        command.Parameters.AddWithValue("rds",NpgsqlDbType.Array|NpgsqlDbType.Bigint,rows.Select(x=>x.Rd).ToArray());
        command.Parameters.AddWithValue("replayable",NpgsqlDbType.Array|NpgsqlDbType.Boolean,rows.Select(x=>x.Replayable).ToArray());
        await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand FoldCommand(NpgsqlConnection connection,Cell cell,Witness[] rows)
    {
        var command=Command(connection,"""
            SELECT consensus.upsert_evidence_type(@type,ARRAY[@subject],ARRAY[@object],
                ARRAY[@phi]::bigint[],ARRAY[@games]::bigint[],ARRAY[@score]::bigint[],
                ARRAY[@time]::timestamptz[],ARRAY[@opponent]::bigint[],
                ARRAY[0,@n]::bigint[],@opponents,@phis,@counts,@sums)
            """,cell);
        command.Parameters.AddWithValue("phi",rows[0].Rd);
        command.Parameters.AddWithValue("games",rows.Sum(x=>x.Games));
        command.Parameters.AddWithValue("score",rows.Sum(x=>x.Score));
        command.Parameters.AddWithValue("time",NpgsqlDbType.TimestampTz,rows.Max(x=>Epoch.AddSeconds(x.Index)));
        command.Parameters.AddWithValue("opponent",rows[0].Opponent);
        command.Parameters.AddWithValue("n",(long)rows.Length);
        command.Parameters.AddWithValue("opponents",NpgsqlDbType.Array|NpgsqlDbType.Bigint,rows.Select(x=>x.Opponent).ToArray());
        command.Parameters.AddWithValue("phis",NpgsqlDbType.Array|NpgsqlDbType.Bigint,rows.Select(x=>x.Rd).ToArray());
        command.Parameters.AddWithValue("counts",NpgsqlDbType.Array|NpgsqlDbType.Bigint,rows.Select(x=>x.Games).ToArray());
        command.Parameters.AddWithValue("sums",NpgsqlDbType.Array|NpgsqlDbType.Bigint,rows.Select(x=>x.Score).ToArray());
        return command;
    }

    private static NpgsqlCommand RefoldCommand(NpgsqlConnection connection,string sourceName)
    {
        var command=connection.CreateCommand();
        command.CommandText="CALL ops.refold_source(@name)";
        command.CommandTimeout=20;
        command.Parameters.AddWithValue("name",sourceName);
        return command;
    }

    private static async Task<long> FoldAsync(NpgsqlConnection connection,Cell cell,Witness[] rows)
    {
        await using var command=FoldCommand(connection,cell,rows);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Standing> ReadAsync(NpgsqlConnection connection,Cell cell)
    {
        await using var command=Command(connection,"""
            SELECT rating,rd,volatility,witness_count,last_observed_at
            FROM laplace.consensus WHERE type_id=@type AND subject_id=@subject AND object_id IS NOT DISTINCT FROM @object
            """,cell);
        return await StandingAsync(command);
    }

    private static async Task<Standing> CanonicalAsync(NpgsqlConnection connection,Cell cell)
    {
        await using var command=Command(connection,"""
            SELECT (f).rating,(f).rd,(f).volatility,(f).witness_count,ts FROM (
              SELECT laplace.consensus_fold(false,NULL,NULL,NULL,
                opponent_rating_fp1e9,opponent_rd_fp1e9,GREATEST(observation_count,1),
                sum_score_fp1e9,consensus.glicko2_tau() ORDER BY last_observed_at,id) AS f,
                max(last_observed_at) AS ts
              FROM laplace.attestations
              WHERE type_id=@type AND subject_id=@subject AND object_id IS NOT DISTINCT FROM @object
            ) canonical
            """,cell);
        return await StandingAsync(command);
    }

    private static async Task<Standing> DeltaMathAsync(NpgsqlConnection connection,Standing prior,Witness witness)
    {
        await using var command=connection.CreateCommand();
        command.CommandText="""
            SELECT (f).rating,(f).rd,(f).volatility FROM (
              SELECT laplace.laplace_glicko2_accumulate_period(
                @rating,@rd,@volatility,ARRAY[@opponent]::bigint[],ARRAY[@phi]::bigint[],
                ARRAY[@games]::bigint[],ARRAY[@sum]::bigint[],consensus.glicko2_tau()) AS f
            ) expected
            """;
        command.CommandTimeout=20;
        command.Parameters.AddWithValue("rating",prior.Rating);
        command.Parameters.AddWithValue("rd",prior.Rd);
        command.Parameters.AddWithValue("volatility",prior.Volatility);
        command.Parameters.AddWithValue("opponent",witness.Opponent);
        command.Parameters.AddWithValue("phi",witness.Rd);
        command.Parameters.AddWithValue("games",witness.Games);
        command.Parameters.AddWithValue("sum",witness.Score);
        await using var reader=await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),
            prior.Witnesses+witness.Games,Epoch.AddSeconds(witness.Index));
    }

    private static async Task<Standing> StandingAsync(NpgsqlCommand command)
    {
        await using var reader=await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result=new Standing(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),
            reader.GetInt64(3),reader.GetDateTime(4).ToUniversalTime());
        Assert.False(await reader.ReadAsync());
        return result;
    }

    private static async Task<long> EvidenceCountAsync(NpgsqlConnection connection,Cell cell)
    {
        await using var command=Command(connection,
            "SELECT count(*) FROM laplace.attestations WHERE type_id=@type AND subject_id=@subject AND object_id IS NOT DISTINCT FROM @object",cell);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitForBlockerAsync(NpgsqlConnection observer,int blocked,int holder)
    {
        var timer=Stopwatch.StartNew();
        while (timer.Elapsed<TimeSpan.FromSeconds(15))
        {
            await using var command=observer.CreateCommand();
            command.CommandText="SELECT @holder=ANY(pg_blocking_pids(@blocked))";
            command.CommandTimeout=20;
            command.Parameters.AddWithValue("holder",holder);
            command.Parameters.AddWithValue("blocked",blocked);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Backend {blocked} never waited for {holder}");
    }

    private sealed class RunningFold : IAsyncDisposable
    {
        private readonly NpgsqlCommand _command;
        internal Task<object?> Completion { get; }
        internal RunningFold(NpgsqlConnection connection,Cell cell,Witness[] rows)
            : this(FoldCommand(connection,cell,rows)) { }
        internal RunningFold(NpgsqlCommand command)
        {
            _command=command;
            Completion=_command.ExecuteScalarAsync();
        }
        public async ValueTask DisposeAsync()
        {
            if (!Completion.IsCompleted) _command.Cancel();
            try { await Completion; } catch { }
            await _command.DisposeAsync();
        }
    }

    private async Task CleanupAsync(Cell cell)
    {
        await using var connection=await OpenAsync();
        await using var command=Command(connection,"""
            DELETE FROM laplace.attestations WHERE type_id=@type AND subject_id=@subject AND object_id IS NOT DISTINCT FROM @object;
            DELETE FROM laplace.consensus WHERE type_id=@type AND subject_id=@subject AND object_id IS NOT DISTINCT FROM @object
            """,cell);
        await command.ExecuteNonQueryAsync();
    }
}
