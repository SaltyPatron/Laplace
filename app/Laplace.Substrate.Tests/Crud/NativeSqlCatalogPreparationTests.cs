using System.Text.RegularExpressions;
using System.Text;
using Laplace.Ingestion;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Laplace.SubstrateCRUD;
using Npgsql;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>Parse and resolve fixed shipped statements against an installed schema without executing them.</summary>
[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed partial class NativeSqlCatalogPreparationTests : IAsyncLifetime
{
    private LocalPgFixture? _fixture;
    private NpgsqlDataSource _source = null!;

    [Fact]
    public async Task ReadinessPresenceUsesTheNativeTablesRatherThanLegacyAdapters()
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        // These are bounded committed-presence probes, not planner estimates.
        // A populated Native database can retain empty legacy adapter tables.
        await using var expected = new NpgsqlCommand("""
            SELECT EXISTS(SELECT FROM public.entity), EXISTS(SELECT FROM public.consensus)
            """, connection, transaction);
        (bool entities, bool consensus) presence;
        await using (var rows = await expected.ExecuteReaderAsync())
        {
            Assert.True(await rows.ReadAsync());
            presence = (rows.GetBoolean(0), rows.GetBoolean(1));
        }
        Assert.Equal(presence, await NpgsqlSubstrateReads.EntitiesAndConsensusExistAsync(connection, default));
        var inventory = await NpgsqlSubstrateReads.ReadinessInventoryAsync(connection, default);
        Assert.Equal(presence.entities, inventory.HasEntities);
        Assert.Equal(presence.consensus, inventory.HasConsensus);
        Assert.Empty(inventory.MissingNativeApi);
        Assert.Empty(inventory.MissingNativeIndexes);
        await using (var removeIndex = new NpgsqlCommand("DROP INDEX public.physicality_assertions", connection, transaction))
            await removeIndex.ExecuteNonQueryAsync();
        inventory = await NpgsqlSubstrateReads.ReadinessInventoryAsync(connection, default);
        Assert.Equal(new[] { "physicality_assertions" }, inventory.MissingNativeIndexes);
        await using (var repairIndex = new NpgsqlCommand("SELECT public.laplace_schema_indexes()", connection, transaction))
            await repairIndex.ExecuteNonQueryAsync();
        inventory = await NpgsqlSubstrateReads.ReadinessInventoryAsync(connection, default);
        Assert.Empty(inventory.MissingNativeIndexes);
        await using (var remove = new NpgsqlCommand("""
            ALTER EXTENSION laplace DROP FUNCTION public.laplace_testimony(public.blake3[],public.blake3[]);
            DROP FUNCTION public.laplace_testimony(public.blake3[],public.blake3[]);
            """, connection, transaction)) await remove.ExecuteNonQueryAsync();
        inventory = await NpgsqlSubstrateReads.ReadinessInventoryAsync(connection, default);
        Assert.Equal(new[] { "laplace_testimony" }, inventory.MissingNativeApi);
        await using (var repair = new NpgsqlCommand("SELECT public.laplace_restore_function_bindings()", connection, transaction))
            await repair.ExecuteNonQueryAsync();
        inventory = await NpgsqlSubstrateReads.ReadinessInventoryAsync(connection, default);
        Assert.Empty(inventory.MissingNativeApi);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ExecutorReadersPreserveSupportStandingAndCompleteFacetPaging()
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // Exercise retained adapter rows above both former fetch sizes. These
        // are declared test projections; no corpus testimony is synthesized.
        await using (var seed = new NpgsqlCommand("""
            CREATE TEMP TABLE executor_case AS
            SELECT decode(public.laplace_cp_id(65)::text,'hex') AS subject,
              decode(public.laplace_text_id('executor-support')::text,'hex') AS relation,
              decode(public.laplace_cp_id(66)::text,'hex') AS object;
            INSERT INTO laplace.attestations
              (id,subject_id,type_id,object_id,source_id,context_id,outcome,last_observed_at,
               observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
            SELECT decode(public.laplace_compose(ARRAY[
                encode(t.subject,'hex')::public.blake3,encode(t.relation,'hex')::public.blake3,
                encode(t.object,'hex')::public.blake3,public.laplace_text_id('executor-source/'||g),
                public.laplace_cp_id(c)])::text,'hex'),
              t.subject,t.relation,t.object,
              decode(public.laplace_text_id('executor-source/'||g)::text,'hex'),
              decode(public.laplace_cp_id(c)::text,'hex'),1,now(),1,1000000000,350000000000
            FROM executor_case t CROSS JOIN generate_series(1,5000) g CROSS JOIN generate_series(67,68) c;
            INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
            SELECT decode(public.laplace_compose(ARRAY[encode(subject,'hex')::public.blake3,
                encode(relation,'hex')::public.blake3,encode(object,'hex')::public.blake3])::text,'hex'),
              subject,relation,object,1700000000000,50000000000,60000000,7000,now() FROM executor_case;
            INSERT INTO laplace.attestations
              (id,subject_id,type_id,object_id,source_id,context_id,outcome,last_observed_at,
               observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
            SELECT public.blake3_send(public.laplace_id('executor-unary/'||encode(a.id,'hex'))),
              a.subject_id,a.type_id,NULL,a.source_id,a.context_id,a.outcome,a.last_observed_at,
              a.observation_count,a.sum_score_fp1e9,a.opponent_rd_fp1e9
            FROM laplace.attestations a JOIN executor_case t ON t.subject=a.subject_id;
            INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
            SELECT laplace.consensus_id(subject,relation,NULL),subject,relation,NULL,
              1700000000000,50000000000,60000000,7000,now() FROM executor_case;
            """, connection, transaction)) await seed.ExecuteNonQueryAsync();

        foreach (var unary in new[] { "false", "true" })
        foreach (var (scope, context, count) in new[]
        {
            ("NULL::bytea[]", "NULL::bytea", 5000),
            ("ARRAY[decode(public.laplace_text_id('executor-source/1')::text,'hex')]", "NULL::bytea", 1),
            ("NULL::bytea[]", "decode(public.laplace_cp_id(67)::text,'hex')", 5000),
        })
        {
            await using var read = new NpgsqlCommand($"""
                SELECT r.* FROM executor_case t CROSS JOIN LATERAL ops.attestation_response_batch(
                  ARRAY[t.subject,t.subject],t.relation,{scope},{context},NULL,{unary}) r
                """, connection, transaction);
            await using var rows = await read.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(count, rows.GetInt32(3));
            Assert.Equal(1700000000000L, rows.GetInt64(4));
            Assert.Equal(50000000000L, rows.GetInt64(5));
            Assert.Equal("pooled", rows.GetString(6));
            Assert.False(await rows.ReadAsync());
        }
        await using (var absent = new NpgsqlCommand("""
            SELECT count(*) FROM executor_case t CROSS JOIN LATERAL ops.attestation_response_batch(
              ARRAY[t.subject],t.relation,NULL,decode(public.laplace_cp_id(90)::text,'hex'),NULL,false) r
            """, connection, transaction)) Assert.Equal(0L, await absent.ExecuteScalarAsync());

        // Every requested floor identity must survive the streamed facet read;
        // the caller's page applies after the complete matched set is known.
        foreach (var (offset, limit, expected) in new[] { (0, 5000, 4001L), (4000, 5, 1L) })
        {
            await using var browse = new NpgsqlCommand("""
                SELECT count(*),min(matched_entity_count),bool_and(tier=0 AND rating IS NULL)
                FROM consensus.browse_named_entities(ARRAY(
                  SELECT decode(public.laplace_cp_id(g)::text,'hex') FROM generate_series(1024,5024) g),
                  NULL,$1,$2,1)
                """, connection, transaction);
            browse.Parameters.AddWithValue(offset); browse.Parameters.AddWithValue(limit);
            await using var rows = await browse.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(expected, rows.GetInt64(0));
            Assert.Equal(4001L, rows.GetInt64(1));
            Assert.True(rows.GetBoolean(2));
        }
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task TrajectoryPlanesShareCompressedCountsAndPreserveVocabularyDenominators()
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var window = new StagedSourceWriter(10000, 100000, 1 << 24);
        var a = window.Text("A"u8);
        var b = window.Text("B"u8);
        var c = window.Text("C"u8);
        var separator = window.Text("\r\n"u8);
        var children = new[] { a, a, separator, b, a, c };
        var root = window.Compose(children, checked((byte)(children.Max(x => x.Tier) + 1)), 0, 0);
        var writer = new NpgsqlSubstrateWriter(_source);
        await writer.ApplyCanonicalWorkingSetAsync(window, connection, transaction, [root.Id]);
        // A witness root is a trunk with attestations; one fixture claim makes this root walkable.
        await using (var register = new NpgsqlCommand("""
            INSERT INTO public.attestation(claim,witness,lineage,games,tokens,score,"position",trust)
            VALUES(md5('trajectory-witness-'||$1::text)::public.blake3,$1,NULL,1,1,1.0,NULL,1.0)
            """, connection, transaction))
        {
            register.Parameters.AddWithValue(root.Id);
            await register.ExecuteNonQueryAsync();
        }
        byte[][] ids = [a.Id.ToBytes(), b.Id.ToBytes(), c.Id.ToBytes()];
        await using (var path = new NpgsqlCommand("SET LOCAL search_path=pg_catalog,pg_temp", connection, transaction))
            await path.ExecuteNonQueryAsync();

        static string Key(byte[] a, byte[] b) => Convert.ToHexString(a) + ":" + Convert.ToHexString(b);
        var vocab = new[] { ids[0], ids[1], ids[0] };
        var planes = new Dictionary<(int Gap, string Pair), double>();
        await using (var read = NpgsqlCatalog.Command(connection, transaction, "generation.trajectory_plane", vocab, 2, 8))
        await using (var rows = await read.ExecuteReaderAsync())
            while (await rows.ReadAsync())
                planes.Add((rows.GetInt32(0), Key((byte[])rows[1], (byte[])rows[2])), rows.GetDouble(3));
        Assert.Equal(5, planes.Count);
        Assert.Equal(1d / 3, planes[(1, Key(ids[0], ids[0]))], 12);
        Assert.Equal(1d / 3, planes[(1, Key(ids[0], ids[1]))], 12);
        Assert.Equal(1, planes[(1, Key(ids[1], ids[0]))]);
        Assert.Equal(.5, planes[(2, Key(ids[0], ids[0]))]);
        Assert.Equal(.5, planes[(2, Key(ids[0], ids[1]))]);

        foreach (var (name, arg) in new[] { ("gap", 1), ("window", 2) })
        {
            var actual = new Dictionary<string, double>();
            await using var read = NpgsqlCatalog.Command(connection, transaction, "generation.relation_plane", "traj", name, arg, vocab);
            await using var rows = await read.ExecuteReaderAsync();
            while (await rows.ReadAsync()) actual.Add(Key((byte[])rows[0], (byte[])rows[1]), rows.GetDouble(2));
            Assert.Equal(3, actual.Count);
            Assert.Equal(name == "gap" ? 1d / 3 : .4, actual[Key(ids[0], ids[0])], 12);
            Assert.Equal(name == "gap" ? 1d / 3 : .4, actual[Key(ids[0], ids[1])], 12);
            Assert.Equal(name == "gap" ? 1 : .5, actual[Key(ids[1], ids[0])], 12);
        }
        await using var empty = NpgsqlCatalog.Command(connection, transaction, "generation.trajectory_plane", Array.Empty<byte[]>(), 2, 8);
        await using var emptyRows = await empty.ExecuteReaderAsync();
        Assert.False(await emptyRows.ReadAsync());
    }

    [Fact]
    public async Task BrokenBackendLogsRollbackFailureAndPreservesThePrimaryDatabaseException()
    {
        await using var connection = await _source.OpenConnectionAsync();
        var log = new CleanupLog();
        var failure = await Assert.ThrowsAnyAsync<NpgsqlException>(() => AdvisoryTxLock.BeginWithLockAsync(
            connection, "laplace_apply_batch", PostgresWriteDurability.Synchronous, log,
            CancellationToken.None, async (transaction, _, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(pg_backend_pid())", connection, transaction);
                await command.ExecuteNonQueryAsync(token);
            }));
        Assert.NotNull(failure);
        Assert.Contains(log.Errors, x => x.Exception is not null
            && x.Message.Contains("rollback failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedTransactionSettingRollsBackAndLeavesTheConnectionReusable()
    {
        await using var connection = await _source.OpenConnectionAsync();
        var log = new CleanupLog();
        var failure = await Assert.ThrowsAsync<PostgresException>(() => AdvisoryTxLock.BeginWithLockAsync(
            connection, "laplace_apply_batch", PostgresWriteDurability.Synchronous, log,
            CancellationToken.None, async (transaction, _, token) =>
            {
                await using var command = new NpgsqlCommand("SET LOCAL work_mem='invalid-setting'", connection, transaction);
                await command.ExecuteNonQueryAsync(token);
            }));
        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, failure.SqlState);
        Assert.Empty(log.Errors);
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT 1";
        Assert.Equal(1, await probe.ExecuteScalarAsync());
    }

    private sealed class CleanupLog : ILogger
    {
        public List<(Exception? Exception, string Message)> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Errors.Add((exception, formatter(state, exception)));
    }

    public async Task InitializeAsync()
    {
        var selected = Environment.GetEnvironmentVariable("LAPLACE_SQL_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(selected))
        {
            _source = LaplaceDataSource.Create(SubstrateAccess.Ingest, selected);
            return;
        }
        // Normal managed-db CI runs this check too. Complete the disposable
        // fixture with the canonical migrations for both catalog domains.
        _fixture = new LocalPgFixture();
        await _fixture.InitializeAsync();
        _source = _fixture.DataSource;
        foreach (var name in new[] { "20260606000000_layer1_database.sql", "20260611000000_app_billing.sql",
                     "20261015000000_substrate_runtime_grants.sql" })
        {
            var migration = Path.Combine(Laplace.Decomposers.Abstractions.Tests.TypeIdLawTests.FindRepoRootPublic(),
                "db", "migrations", name);
            await using var command = _source.CreateCommand(await File.ReadAllTextAsync(migration));
            await command.ExecuteNonQueryAsync();
        }
        await using var nativeConnection = await _source.OpenConnectionAsync();
        await using var nativeTransaction = await nativeConnection.BeginTransactionAsync();
        await using (var native = NpgsqlCatalog.Command(nativeConnection, nativeTransaction, "migration.canonical_substrate"))
            await native.ExecuteNonQueryAsync();
        await nativeTransaction.CommitAsync();
        // ALTER DATABASE cache settings apply to new sessions. Discard pooled
        // sessions created before migration rather than testing build defaults.
        _source.Clear();
        await using var configured = await _source.OpenConnectionAsync();
        await using var verifyCachePaths = new NpgsqlCommand("""
            SELECT current_setting('laplace.tier0')=current_setting('laplace_substrate.perfcache_path')
               AND current_setting('laplace.highway')=current_setting('laplace_substrate.highway_perfcache_path')
            """, configured);
        Assert.Equal(true, await verifyCachePaths.ExecuteScalarAsync());
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null) await _fixture.DisposeAsync();
        else if (_source is not null) await _source.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentAppliesDoNotExhaustAnExplicitTwoConnectionPool()
    {
        var configuration = new NpgsqlConnectionStringBuilder(_source.ConnectionString)
        {
            MaxPoolSize = 2, MinPoolSize = 0, Timeout = 3, CommandTimeout = 10,
            ApplicationName = "apply-pool-" + Guid.NewGuid().ToString("N")
        };
        await using var limited = LaplaceDataSource.Create(SubstrateAccess.Ingest, configuration.ConnectionString);
        var firstWriter = new NpgsqlSubstrateWriter(limited);
        var secondWriter = new NpgsqlSubstrateWriter(limited);
        var source = Hash128.Text("pool-source-" + Guid.NewGuid().ToString("N"));
        using var firstBuilder = new SubstrateChangeBuilder(source, "first-pool-apply").DeclareSourcePrior(0.5);
        using var secondBuilder = new SubstrateChangeBuilder(source, "second-pool-apply").DeclareSourcePrior(0.5);
        ContentEmitter.Emit(firstBuilder, "first pool observation " + Guid.NewGuid().ToString("N"), source);
        ContentEmitter.Emit(secondBuilder, "second pool observation " + Guid.NewGuid().ToString("N"), source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // A temporarily occupied connection makes both control requests queue
        // before their presence/COPY phases can obtain a worker connection.
        await using var occupied = await limited.OpenConnectionAsync(timeout.Token);
        var first = firstWriter.ApplyAsync(firstBuilder.Build(), timeout.Token);
        var second = secondWriter.ApplyAsync(secondBuilder.Build(), timeout.Token);
        await Task.Delay(250, timeout.Token);
        await occupied.CloseAsync();
        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.True(result.EntitiesInserted > 0));
        Assert.Equal(2, new NpgsqlConnectionStringBuilder(limited.ConnectionString).MaxPoolSize);
    }

    [Fact]
    public async Task NativeGeometrySelectorsMatchExhaustiveCanonicalMetrics()
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO public.entity(id,tier,coord,hilbert)
            SELECT public.laplace_id(s),public.laplace_tier(s),public.laplace_coord(s),public.laplace_hilbert(s)
            FROM (SELECT 'nativegeometry'||i::text AS s FROM generate_series(1,160) i
                  UNION ALL SELECT repeat('n',16384)||i::text FROM generate_series(1,160) i
                  UNION ALL SELECT chr(i) FROM generate_series(256,415) i
                  UNION ALL SELECT chr(i) FROM generate_series(900000,900160) i) words
            ON CONFLICT(id) DO NOTHING
            """, connection, transaction))
            await seed.ExecuteNonQueryAsync();
        var fixtures = new List<(byte[] Id, short Tier, double[] Coord)>();
        await using (var anchors = new NpgsqlCommand("""
            SELECT DISTINCT ON(hilbert<0) public.blake3_send(id),tier,
                public.ST_X(coord),public.ST_Y(coord),public.ST_Z(coord),public.ST_M(coord)
            FROM public.entity ORDER BY hilbert<0,id
            """, connection, transaction))
        await using (var rows = await anchors.ExecuteReaderAsync())
            while (await rows.ReadAsync()) fixtures.Add((rows.GetFieldValue<byte[]>(0), rows.GetInt16(1),
                Enumerable.Range(2,4).Select(rows.GetDouble).ToArray()));
        Assert.Equal(2, fixtures.Count);
        await using (var tied = new NpgsqlCommand("""
            SELECT public.blake3_send(e.id),e.tier,public.ST_X(e.coord),public.ST_Y(e.coord),
                public.ST_Z(e.coord),public.ST_M(e.coord)
            FROM public.entity e JOIN (
                SELECT tier,hilbert FROM public.entity GROUP BY tier,hilbert
                HAVING count(*)>8 ORDER BY count(*) DESC,tier,hilbert LIMIT 1
            ) b USING(tier,hilbert) ORDER BY e.id LIMIT 1
            """, connection, transaction))
        await using (var rows = await tied.ExecuteReaderAsync())
        {
            Assert.True(await rows.ReadAsync(), "Canonical content must exercise a boundary with more ties than the fanout.");
            fixtures.Add((rows.GetFieldValue<byte[]>(0), rows.GetInt16(1),
                Enumerable.Range(2,4).Select(rows.GetDouble).ToArray()));
        }
        for (int fixture = 0; fixture < fixtures.Count; fixture++)
        {
            var anchor = fixtures[fixture];
            int fan = fixture < 2 ? 7 : 40;
            object[] arguments = [.. anchor.Coord.Cast<object>(), anchor.Id, fan, anchor.Tier];
            var angular = new List<byte[]>();
            await using (var query = NpgsqlCatalog.Command(connection, transaction, "geometry.prompt.angular", arguments))
            await using (var rows = await query.ExecuteReaderAsync())
                while (await rows.ReadAsync()) angular.Add(rows.GetFieldValue<byte[]>(0));
            var expectedAngular = new List<byte[]>();
            await using (var query = new NpgsqlCommand("""
                SELECT public.blake3_send(id) FROM public.entity
                WHERE tier=$1 AND public.blake3_send(id)<>$2
                ORDER BY public.laplace_angular4d(coord,public.ST_MakePoint($3,$4,$5,$6)),id LIMIT $7
                """, connection, transaction))
            {
                query.Parameters.AddWithValue(anchor.Tier); query.Parameters.AddWithValue(anchor.Id);
                foreach (double coordinate in anchor.Coord) query.Parameters.AddWithValue(coordinate);
                query.Parameters.AddWithValue(fan);
                await using var rows = await query.ExecuteReaderAsync();
                while (await rows.ReadAsync()) expectedAngular.Add(rows.GetFieldValue<byte[]>(0));
            }
            Assert.Equal(expectedAngular.Select(Convert.ToHexString), angular.Select(Convert.ToHexString));
            var hilbert = new List<(byte[] Id, ulong Delta)>();
            await using (var query = NpgsqlCatalog.Command(connection, transaction, "geometry.prompt.hilbert", arguments))
            await using (var rows = await query.ExecuteReaderAsync())
                while (await rows.ReadAsync())
                {
                    ulong key = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(rows.GetFieldValue<byte[]>(1));
                    ulong origin = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(rows.GetFieldValue<byte[]>(2));
                    hilbert.Add((rows.GetFieldValue<byte[]>(0), key > origin ? key - origin : origin - key));
                }
            var expectedHilbert = new List<string>();
            await using (var query = new NpgsqlCommand("""
                WITH origin AS (SELECT hilbert h FROM public.entity WHERE public.blake3_send(id)=$2)
                SELECT encode(public.blake3_send(e.id),'hex') FROM public.entity e CROSS JOIN origin o
                WHERE e.tier=$1 AND public.blake3_send(e.id)<>$2
                ORDER BY abs(e.hilbert::numeric-o.h::numeric),e.id LIMIT $3
                """, connection, transaction))
            {
                query.Parameters.AddWithValue(anchor.Tier); query.Parameters.AddWithValue(anchor.Id);
                query.Parameters.AddWithValue(fan);
                await using var rows = await query.ExecuteReaderAsync();
                while (await rows.ReadAsync()) expectedHilbert.Add(rows.GetString(0));
            }
            Assert.Equal(expectedHilbert, hilbert.OrderBy(item => item.Delta)
                .ThenBy(item => Convert.ToHexString(item.Id), StringComparer.Ordinal).Take(fan)
                .Select(item => Convert.ToHexStringLower(item.Id)));
        }
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task EveryFixedStatementPreparesAgainstTheInstalledSchema()
    {
        await using var connection = await _source.OpenConnectionAsync();
        using var resource = typeof(SqlCatalog).Assembly.GetManifestResourceStream("Laplace.SqlCatalog.def")!;
        using var reader = new StreamReader(resource);
        var catalog = await reader.ReadToEndAsync();
        var names = Regex.Matches(catalog, "SQL_QUERY\\(\\s*\"(?<name>[^\"]+)\"")
            .Select(match => match.Groups["name"].Value).ToArray();
        Assert.NotEmpty(names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        var failures = new List<string>();
        var templates = new List<string>();
        foreach (var name in names)
        {
            try
            {
                var contract = SqlCatalog.Get(name);
                // Identifier-bearing operation and maintenance templates have their
                // own lifecycle checks; they cannot be prepared without expansion.
                if (Regex.IsMatch(contract.Text, @"\{[a-z_][a-z0-9_]*\}", RegexOptions.IgnoreCase))
                {
                    if (name.StartsWith("migration.", StringComparison.Ordinal))
                    {
                        // Parse utility syntax without executing lifecycle operations.
                        // Confirmed recreation remains a separate operator action.
                        using var identifiers = new NpgsqlCommandBuilder();
                        string statement = Regex.Replace(contract.Text, @"\{([a-z_]+)\}", match =>
                            match.Groups[1].Value switch
                            {
                                "database" => identifiers.QuoteIdentifier(connection.Database),
                                "replacement" => identifiers.QuoteIdentifier("laplace_catalog_prepare"),
                                "options" => "TEMPLATE template0",
                                "owner" => "CURRENT_USER",
                                _ => throw new InvalidOperationException($"Unknown bootstrap DDL slot in {name}."),
                            });
                        await using var utility = new NpgsqlCommand(statement, connection);
                        await utility.PrepareAsync();
                        await utility.UnprepareAsync();
                        continue;
                    }
                    Assert.True(name.StartsWith("maintenance.vacuum_", StringComparison.Ordinal)
                        || name.StartsWith("operation.", StringComparison.Ordinal)
                        // StagedSourceWriterTests executes these exact CREATE,
                        // COPY and DROP templates in a caller-rolled-back transaction.
                        || name is "write.canonical_stage_create" or "write.canonical_stage_drop"
                            or "write.canonical_copy_entities" or "write.canonical_copy_paths",
                        $"No lifecycle verification owns template {name}.");
                    templates.Add(name);
                    continue;
                }
                await using var command = NpgsqlCatalog.Command(connection, null, name,
                    new object?[contract.ParameterTypes.Length]);
                // The native router quotes the physical owner; policy/ACL fallback
                // uses the parent. Prepare that real fallback statement here.
                if (name == "consensus.evidence_rows_batch")
                    command.CommandText = command.CommandText.Replace("%s", "laplace.attestations", StringComparison.Ordinal);
                await command.PrepareAsync();
                await command.UnprepareAsync();
            }
            catch (PostgresException error)
            {
                failures.Add($"{name}: [{error.SqlState}] {error.MessageText}");
            }
        }
        Assert.NotEmpty(templates);
        Assert.True(failures.Count == 0,
            $"{failures.Count} of {names.Length - templates.Count} fixed native SQL contracts failed preparation " +
            $"({templates.Count} identifier templates require their lifecycle tests):\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task CatalogVacuumExecutesEveryModeOnOneQuotedRelationOutsideATransaction()
    {
        var table = "catalog vacuum " + Guid.NewGuid().ToString("N");
        using var identifiers = new NpgsqlCommandBuilder();
        var relation = "public." + identifiers.QuoteIdentifier(table);
        await using var connection = await _source.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand($"CREATE TABLE {relation}(n integer); INSERT INTO {relation} VALUES (1),(2)", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            foreach (var (full, analyze, expected) in new[]
            {
                (false, false, "VACUUM "),
                (false, true, "VACUUM (ANALYZE) "),
                (true, false, "VACUUM (FULL, ANALYZE) "),
            })
            {
                string executed = await NpgsqlMaintenance.VacuumAsync(_source, relation, full, analyze, 30);
                Assert.Equal(expected + relation, executed);
                await using var count = new NpgsqlCommand($"SELECT count(*) FROM {relation}", connection);
                Assert.Equal(2L, await count.ExecuteScalarAsync());
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP TABLE {relation}", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CatalogOperationTemplateExecutesNamedTypedArgumentsAgainstPostgres()
    {
        // The real PostgreSQL operation supplies the names/types. Only the command
        // rendering is under test here; this does not bypass ops.api in InvokeAsync.
        await using var metadata = _source.CreateCommand(
            "SELECT pg_get_function_arguments('pg_catalog.make_interval(integer,integer,integer,integer,integer,integer,double precision)'::regprocedure)");
        var signature = InstalledOpInvoker.ParseSignature((string)(await metadata.ExecuteScalarAsync())!);
        var days = signature.Single(parameter => parameter.Name == "days");
        var seconds = signature.Single(parameter => parameter.Name == "secs");
        var operation = InstalledOpInvoker.QualifiedCatalogName("pg_catalog.make_interval");
        var arguments = new (string Name, string Type, NpgsqlParameter Parameter)[]
        {
            (days.Name, days.Type, InstalledOpInvoker.BindArg("a0", "2")),
            (seconds.Name, seconds.Type, InstalledOpInvoker.BindArg("a1", "1.5")),
        };
        await using var command = NpgsqlCatalog.InstalledOperationCommand(_source, false, operation, arguments, 2);
        Assert.Equal(TimeSpan.FromDays(2) + TimeSpan.FromSeconds(1.5), await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CompletionReadsPropagateDatabaseFailuresInsteadOfReprocessing()
    {
        await using var connection = await _source.OpenConnectionAsync();
        var role = "completion_read_test_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE ROLE {role}; GRANT USAGE ON SCHEMA laplace TO {role}", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            var limited = new NpgsqlConnectionStringBuilder(_source.ConnectionString)
            {
                Options = $"-c role={role}",
                Pooling = false,
            };
            await using var deniedSource = NpgsqlDataSource.Create(limited.ConnectionString);
            var reader = new NpgsqlSubstrateReader(deniedSource);
            var id = Hash128.Blake3("completion-failure"u8);
            Func<Task>[] reads =
            [
                async () => await reader.HasSourceEverCompletedAsync(1),
                async () => await reader.HasSourceCompletedAsync(id, 1),
                async () => await reader.HasSourcesCompletedAsync([id], 1),
                async () => await reader.HasFilesCompletedAsync([id], id, 1),
            ];
            var swallowed = new List<int>();
            for (var index = 0; index < reads.Length; index++)
            {
                var error = await Record.ExceptionAsync(reads[index]);
                if (error is null) swallowed.Add(index);
                else Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(error).SqlState);
            }
            Assert.Empty(swallowed);
        }
        finally
        {
            await using var remove = new NpgsqlCommand($"REVOKE USAGE ON SCHEMA laplace FROM {role}; DROP ROLE {role}", connection);
            await remove.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CompletionReadsReturnExactWitnessLayerAndUnversionedFileMatches()
    {
        var id = Hash128.Blake3(Guid.NewGuid().ToByteArray());
        var other = Hash128.Blake3(id.ToBytes());
        var versioned = Hash128.Blake3(other.ToBytes());
        var absent = Hash128.Blake3(versioned.ToBytes());
        var reader = new NpgsqlSubstrateReader(_source);
        Assert.False(await reader.HasSourceCompletedAsync(id, 255));
        Assert.Empty(await reader.HasSourcesCompletedAsync([id, other], 255));
        Assert.Empty(await reader.HasFilesCompletedAsync([id, other], id, 255));
        try
        {
            await using var insert = _source.CreateCommand("""
                WITH layers AS (
                    INSERT INTO laplace.ingest_layer_completion(witness_id,layer) VALUES ($1,255),($2,254)
                )
                INSERT INTO laplace.ingest_unit_completion(witness_id,unit_id,layer,digest)
                VALUES ($1,$1,255,NULL),($1,$2,254,NULL),($1,$3,255,$3),($2,$4,255,NULL)
                """);
            foreach (var value in new[] { id, other, versioned, absent }) insert.Parameters.AddWithValue(value.ToBytes());
            await insert.ExecuteNonQueryAsync();
            Assert.True(await reader.HasSourceEverCompletedAsync(255));
            Assert.True(await reader.HasSourceCompletedAsync(id, 255));
            Assert.False(await reader.HasSourceCompletedAsync(other, 255));
            Assert.Equal(new[] { id }, await reader.HasSourcesCompletedAsync([id, other, absent], 255));
            Assert.Equal(new[] { id }, await reader.HasFilesCompletedAsync([id, other, versioned, absent], id, 255));
            Assert.Empty(await reader.HasSourcesCompletedAsync([], 255));
            Assert.Empty(await reader.HasFilesCompletedAsync([], id, 255));
        }
        finally
        {
            await using var remove = _source.CreateCommand("""
                WITH units AS (
                    DELETE FROM laplace.ingest_unit_completion WHERE witness_id=ANY($1)
                )
                DELETE FROM laplace.ingest_layer_completion WHERE witness_id=ANY($1)
                """);
            remove.Parameters.AddWithValue(new[] { id.ToBytes(), other.ToBytes() });
            await remove.ExecuteNonQueryAsync();
        }
    }
}
