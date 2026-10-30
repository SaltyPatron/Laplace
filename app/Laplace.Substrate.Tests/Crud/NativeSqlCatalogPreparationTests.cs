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
public sealed class NativeSqlCatalogPreparationTests : IAsyncLifetime
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
            """, connection, transaction)) await seed.ExecuteNonQueryAsync();

        foreach (var (scope, context, count) in new[]
        {
            ("NULL::bytea[]", "NULL::bytea", 5000),
            ("ARRAY[decode(public.laplace_text_id('executor-source/1')::text,'hex')]", "NULL::bytea", 1),
            ("NULL::bytea[]", "decode(public.laplace_cp_id(67)::text,'hex')", 5000),
        })
        {
            await using var read = new NpgsqlCommand($"""
                SELECT r.* FROM executor_case t CROSS JOIN LATERAL ops.attestation_response_batch(
                  ARRAY[t.subject,t.subject],t.relation,{scope},{context},NULL,false) r
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
        await using (var register = new NpgsqlCommand("INSERT INTO public.witness(id,trust) VALUES($1,1)", connection, transaction))
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
    public async Task NativeClassificationsUseCanonicalBanksWithoutInventingPredicatesOrNormalizingRawValues()
    {
        await using var native = LaplaceDataSource.Create(SubstrateAccess.Ingest, _source.ConnectionString);
        await using var connection = await native.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var window = new StagedSourceWriter(10000, 100000, 1 << 24);
        var word = window.Text(Encoding.UTF8.GetBytes("classification" + Guid.NewGuid().ToString("N")));
        var noun = window.Text("NOUN"u8);
        var raw = window.Text("n"u8);
        var dependency = window.Text("nsubj"u8);
        var auxiliaryWord = window.Text(Encoding.UTF8.GetBytes("auxiliary" + Guid.NewGuid().ToString("N")));
        var auxiliary = window.Text("AUX"u8);
        var binding = window.Text("lexical sense"u8);
        var program = await NativeRecipeExecution.QueryProgramAsync();
        Assert.Equal(binding.Id.ToBytes(), program.Paths[0][0].Entity);
        var oldPredicate = window.Text("HAS_POS"u8);
        StagedReference Compose(StagedReference[] children, ulong mask = 0) =>
            window.Compose(children, checked((byte)(children.Max(c => c.Tier) + 1)), mask, mask);
        var typed = Compose([word, noun], 1);
        var untyped = Compose([word, raw], 1);
        var otherBank = Compose([word, dependency], 1);
        var obsoleteShape = Compose([word, oldPredicate, noun], 1);
        var auxiliaryType = Compose([auxiliaryWord, auxiliary], 1);
        var nounBinding = Compose([word, binding, Compose([word, raw], 4)], 1);
        var auxiliaryBinding = Compose([auxiliaryWord, binding, Compose([auxiliaryWord, raw], 4)], 1);
        var claims = new[] { typed, untyped, otherBank, obsoleteShape, auxiliaryType, nounBinding, auxiliaryBinding };
        for (int index = 0; index < claims.Length; index++) claims[index].Said = 1;
        var witness = Compose(claims);
        await new NpgsqlSubstrateWriter(native).ApplyCanonicalWorkingSetAsync(window, connection, transaction, [witness.Id]);
        await using (var register = new NpgsqlCommand("INSERT INTO public.witness(id,trust) VALUES($1,1)", connection, transaction))
        {
            register.Parameters.AddWithValue(witness.Id);
            await register.ExecuteNonQueryAsync();
        }
        await using (var attest = new NpgsqlCommand("""
            WITH observations AS MATERIALIZED (
              SELECT claim,array_agg(trunk ORDER BY trunk) witnesses,
                array_agg(games::bigint ORDER BY trunk) games,
                array_agg(score/games ORDER BY trunk) scores,
                array_agg(trust ORDER BY trunk) trusts
              FROM public.laplace_attested($1::public.blake3[],false)
              WHERE witness GROUP BY claim
            )
            INSERT INTO public.consensus(claim,rating,deviation,volatility,matches)
            SELECT o.claim,s.rating,s.deviation,s.volatility,s.matches
            FROM observations o CROSS JOIN LATERAL public.laplace_series(o.witnesses,o.games,o.scores,o.trusts) s
            """, connection, transaction))
        {
            attest.Parameters.AddWithValue(claims.Select(c => c.Id.ToBytes()).ToArray());
            await attest.ExecuteNonQueryAsync();
        }

        foreach (var (bank, value, claim) in new[] { ("upos", noun, typed), ("deprel", dependency, otherBank) })
        {
            await using var read = NpgsqlCatalog.Command(connection, transaction, "native.classification_values",
                new[] { word.Id.ToBytes(), word.Id.ToBytes() }, bank);
            await using var rows = await read.ExecuteReaderAsync();
            if (!await rows.ReadAsync())
            {
                await rows.DisposeAsync();
                await using var diagnostic = new NpgsqlCommand("""
                    SELECT jsonb_build_object('value',encode($2::bytea,'hex'),
                      'bank',to_jsonb(public.laplace_bank_of(encode($2::bytea,'hex')::public.blake3)),
                      'claims',(SELECT jsonb_agg(to_jsonb(c)) FROM public.laplace_claims_each(
                        ARRAY[encode($1::bytea,'hex')::public.blake3],NULL,ARRAY[public.laplace_bank_bit('kind','claim')],
                        '{}',1::smallint,NULL,2) c))::text
                    """, connection, transaction);
                diagnostic.Parameters.AddWithValue(word.Id.ToBytes());
                diagnostic.Parameters.AddWithValue(value.Id.ToBytes());
                Assert.Fail($"Missing {bank} classification: {await diagnostic.ExecuteScalarAsync()}");
            }
            Assert.Equal(word.Id.ToBytes(), rows.GetFieldValue<byte[]>(0));
            Assert.Equal(value.Id.ToBytes(), rows.GetFieldValue<byte[]>(1));
            Assert.True(rows.GetInt64(2) > 0); Assert.True(rows.GetInt64(3) > 0);
            Assert.Equal(1L, rows.GetInt64(5)); Assert.Equal(claim.Id.ToBytes(), rows.GetFieldValue<byte[]>(6));
            Assert.False(await rows.ReadAsync());
        }
        foreach (var (ids, bank) in new[] { (Array.Empty<byte[]>(), "upos"), (new[] { word.Id.ToBytes() }, "unknown-bank") })
        {
            await using var read = NpgsqlCatalog.Command(connection, transaction, "native.classification_values", ids, bank);
            await using var rows = await read.ExecuteReaderAsync();
            Assert.False(await rows.ReadAsync());
        }
        await using (var content = new NpgsqlCommand("SELECT converse.content_terms($1)", connection, transaction))
        {
            content.Parameters.AddWithValue(new[] { word.Id.ToBytes(), auxiliaryWord.Id.ToBytes(), word.Id.ToBytes() });
            var result = Assert.IsType<byte[][]>(await content.ExecuteScalarAsync());
            Assert.Single(result);
            Assert.Equal(word.Id.ToBytes(), result[0]);
        }
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task PurposeReadAdaptersRemainBoundAndVisibleToThePlanner()
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var migrate = NpgsqlCatalog.Command(connection, transaction, "migration.canonical_substrate"))
            await migrate.ExecuteNonQueryAsync();
        await using (var bindings = new NpgsqlCommand("""
            SELECT count(*)=3 AND bool_and(p.proconfig IS NULL AND NOT p.prosecdef AND p.prosqlbody IS NOT NULL)
            FROM pg_catalog.pg_proc p WHERE p.oid=ANY(ARRAY[
              'ops.entity_record(bytea)'::regprocedure,
              'ops.entity_evidence_counts(bytea[])'::regprocedure,
              'ops.entity_evidence_count(bytea)'::regprocedure])
            """, connection, transaction))
            Assert.Equal(true, await bindings.ExecuteScalarAsync());

        // Definition-time binding must survive a caller without public or any
        // purpose schema on its path. EXPLAIN also detects an opaque SQL wrapper.
        await using (var path = new NpgsqlCommand("SET LOCAL search_path=pg_catalog,pg_temp", connection, transaction))
            await path.ExecuteNonQueryAsync();
        foreach (var call in new[] { "ops.entity_record(decode(repeat('00',16),'hex'))",
                     "ops.entity_evidence_counts(ARRAY[decode(repeat('00',16),'hex')])" })
        {
            await using var plan = new NpgsqlCommand("EXPLAIN (FORMAT JSON) SELECT * FROM " + call, connection, transaction);
            var json = Assert.IsType<string>(await plan.ExecuteScalarAsync());
            Assert.DoesNotContain("\"Function Name\": \"entity_record\"", json);
            Assert.DoesNotContain("\"Function Name\": \"entity_evidence_counts\"", json);
            await using var read = new NpgsqlCommand("SELECT count(*) FROM " + call, connection, transaction);
            Assert.Equal(1L, await read.ExecuteScalarAsync());
        }
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task CanonicalMigrationRestoresNativeFunctionBindings()
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var caller = new NpgsqlCommand("SET LOCAL search_path=pg_catalog,pg_temp", connection, transaction))
            await caller.ExecuteNonQueryAsync();
        // Exercise recovery of an installation carrying the previous guard.
        await using (var previous = new NpgsqlCommand(
            "ALTER FUNCTION public.blake3_cmp(public.blake3,public.blake3) SET search_path=public,pg_temp; "
            + "ALTER FUNCTION public.laplace_mask_has_value(bit,text,text) SET search_path=public,pg_temp; "
            + "ALTER FUNCTION public.laplace_coord(text) SET search_path=public,pg_temp",
            connection, transaction)) await previous.ExecuteNonQueryAsync();
        await using (var migrate = NpgsqlCatalog.Command(connection, transaction, "migration.canonical_substrate"))
            await migrate.ExecuteNonQueryAsync();
        await using (var settings = NpgsqlCatalog.Command(connection, transaction, "maintenance.index_settings", "64MB", "1"))
            await settings.ExecuteNonQueryAsync();
        await using (var caller = new NpgsqlCommand("SELECT current_setting('search_path')", connection, transaction))
            Assert.Equal("pg_catalog, pg_temp", await caller.ExecuteScalarAsync());
        await using (var audit = new NpgsqlCommand("""
            SELECT count(*) FROM pg_opclass oc
            JOIN pg_amproc a ON a.amprocfamily=oc.opcfamily
            JOIN pg_proc p ON p.oid=a.amproc
            WHERE oc.opcintype='public.blake3'::regtype
              AND EXISTS(SELECT FROM unnest(p.proconfig) setting WHERE setting LIKE 'search_path=%')
            """, connection, transaction))
            Assert.Equal(0L, await audit.ExecuteScalarAsync());
        await using (var bindings = new NpgsqlCommand("""
            SELECT bool_and(p.proconfig IS NULL AND NOT p.prosecdef)
            FROM pg_proc p JOIN public.laplace_function_bindings() c ON c.symbol=p.prosrc
            JOIN pg_depend d ON d.objid=p.oid AND d.classid='pg_proc'::regclass
            JOIN pg_extension e ON e.oid=d.refobjid AND d.refclassid='pg_extension'::regclass
            WHERE e.extname='laplace' AND d.deptype='e'
            """, connection, transaction))
            Assert.Equal(true, await bindings.ExecuteScalarAsync());
        await using (var adapters = new NpgsqlCommand("""
            SELECT bool_and(p.prosqlbody IS NOT NULL AND NOT EXISTS(
              SELECT FROM unnest(p.proconfig) setting WHERE setting LIKE 'search_path=%'))
            FROM pg_proc p JOIN pg_language l ON l.oid=p.prolang
            JOIN pg_depend d ON d.objid=p.oid AND d.classid='pg_proc'::regclass
            JOIN pg_extension e ON e.oid=d.refobjid AND d.refclassid='pg_extension'::regclass
            WHERE e.extname='laplace' AND d.deptype='e' AND l.lanname='sql' AND NOT p.prosecdef
            """, connection, transaction))
            Assert.Equal(true, await adapters.ExecuteScalarAsync());
        await using (var scoped = new NpgsqlCommand("""
            SELECT count(*)>0 AND bool_and(p.proconfig IS NULL AND NOT p.prosecdef)
            FROM pg_proc p WHERE p.proname='laplace_contained_series'
              AND p.pronamespace='public'::regnamespace
            """, connection, transaction))
            Assert.Equal(true, await scoped.ExecuteScalarAsync());
        await using (var probe = new NpgsqlCommand("""
            SET LOCAL search_path=pg_catalog,pg_temp;
            SELECT public.blake3_cmp(public.laplace_cp_id(65),public.laplace_cp_id(65))=0
              AND public.blake3_eq(public.laplace_cp_id(65),public.laplace_cp_id(65))
              AND public.blake3_hash(public.laplace_cp_id(65))=public.blake3_hash(public.laplace_cp_id(65))
              AND public.laplace_mask_has_value(public.laplace_mask_slot('kind','claim'),'kind','claim')
              AND public.ST_AsEWKB(public.laplace_coord('A'))=public.laplace_coord_ewkb('A')
            """, connection, transaction))
            Assert.Equal(true, await probe.ExecuteScalarAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task CanonicalMigrationMakesSourceCompletionUseTheManagedNativeIdentity()
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var migrate = NpgsqlCatalog.Command(connection, transaction, "migration.canonical_substrate"))
            await migrate.ExecuteNonQueryAsync();
        var source = SubstrateCanonicalIds.Source("OperationalDecomposer");
        await using (var identity = new NpgsqlCommand(
            "SELECT laplace.source_id('OperationalDecomposer')", connection, transaction))
            Assert.Equal(source.ToBytes(), Assert.IsType<byte[]>(await identity.ExecuteScalarAsync()));
        await using (var record = new NpgsqlCommand("""
            INSERT INTO laplace.ingest_layer_completion(witness_id,layer)
            VALUES ($1,2) ON CONFLICT (witness_id,layer) DO NOTHING
            """, connection, transaction))
        {
            record.Parameters.AddWithValue(source.ToBytes());
            await record.ExecuteNonQueryAsync();
        }
        await using (var complete = new NpgsqlCommand(
            "SELECT ops.layer_completed(laplace.source_id('OperationalDecomposer'),2)", connection, transaction))
            Assert.Equal(true, await complete.ExecuteScalarAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task CompareReadsNativeClaimsWhenCompatibilityEntitiesAreAbsent()
    {
        await using var native = LaplaceDataSource.Create(SubstrateAccess.Ingest, _source.ConnectionString);
        await using var connection = await native.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var migrate = NpgsqlCatalog.Command(connection, transaction, "migration.canonical_substrate"))
            await migrate.ExecuteNonQueryAsync();
        using var window = new StagedSourceWriter(10000, 100000, 1 << 24);
        string xName = "comparex" + Guid.NewGuid().ToString("N");
        string yName = "comparey" + Guid.NewGuid().ToString("N");
        var x = window.Text(Encoding.UTF8.GetBytes(xName));
        var y = window.Text(Encoding.UTF8.GetBytes(yName));
        var predicate = window.Text("!"u8);
        var value = window.Text("shared"u8);
        // Parent tier follows the real Native DAG, including generated text levels.
        StagedReference Compose(StagedReference[] children, ulong mask = 0) =>
            window.Compose(children, checked((byte)(children.Max(c => c.Tier) + 1)), mask, mask);
        var xc = Compose([x, predicate, value], 1);
        var yc = Compose([y, predicate, value], 1);
        // LP_SAID_CLAIM: the source actually asserts these Native claim carriers.
        xc.Said = 2; // LP_SAID_RECORD: repeated asserted records carry three games.
        yc.Said = 1;
        var language = window.Text("eng"u8);
        var context = window.Text("context"u8);
        // LP_KIND_TUPLE (bit 2): actual Native structural responders for each word.
        var xsense = Compose([x, language, context], 4);
        var ysense = Compose([y, language, context], 4);
        var link = Compose([xsense, predicate, ysense], 1);
        link.Said = 1;
        string definedName = "defined" + Guid.NewGuid().ToString("N");
        var defined = window.Text(Encoding.UTF8.GetBytes(definedName));
        var definition = window.Text("native witnessed definition"u8);
        var declared = await NativeRecipeExecution.QueryProgramAsync();
        var senseRelation = window.Text("lexical sense"u8);
        var referenceRelation = window.Text("reference"u8);
        var definitionRelation = window.Text("definition of the synset"u8);
        Assert.Equal(senseRelation.Id.ToBytes(), declared.Paths[0][0].Entity);
        Assert.Equal(referenceRelation.Id.ToBytes(), declared.Paths[0][1].Entity);
        Assert.Equal(definitionRelation.Id.ToBytes(), declared.Paths[0][2].Entity);
        var definedSense = Compose([defined, language, context], 4);
        var declaredClaims = new[] { Compose([defined, senseRelation, definedSense], 1),
            Compose([definedSense, referenceRelation, context], 1), Compose([context, definitionRelation, definition], 1) };
        for (int index = 0; index < declaredClaims.Length; index++) declaredClaims[index].Said = 1;
        var witness = Compose([xc, xc, xc, yc, link, .. declaredClaims]);
        var writer = new NpgsqlSubstrateWriter(native);
        await writer.ApplyCanonicalWorkingSetAsync(window, connection, transaction, [witness.Id]);
        await using (var curve = new NpgsqlCommand("SELECT public.ST_NPoints(structural.entity_curve($1))", connection, transaction))
        {
            curve.Parameters.AddWithValue(witness.Id.ToBytes());
            // The repeated xc run must remain three ordered points. The curve
            // comes entirely from Native carriers while compatibility rows are absent.
            Assert.Equal(8, await curve.ExecuteScalarAsync());
        }
        await using (var absent = new NpgsqlCommand("SELECT count(*) FROM laplace.entities WHERE id=ANY($1)", connection, transaction))
        {
            absent.Parameters.AddWithValue(new[] { x.Id.ToBytes(), y.Id.ToBytes() });
            Assert.Equal(0L, await absent.ExecuteScalarAsync());
        }
        await using (var neighbors = NpgsqlCatalog.Command(connection, transaction, "structural.neighbors_of", x.Id.ToBytes(), 3))
        await using (var rows = await neighbors.ExecuteReaderAsync())
        {
            int count = 0;
            while (await rows.ReadAsync())
            {
                Assert.Equal(32, rows.GetString(0).Length);
                Assert.False(string.IsNullOrWhiteSpace(rows.GetString(1)));
                Assert.True(double.IsFinite(rows.GetDouble(2)));
                count++;
            }
            Assert.Equal(3, count);
        }
        await using (var register = new NpgsqlCommand("INSERT INTO public.witness(id,trust) VALUES($1,1)", connection, transaction))
        {
            register.Parameters.AddWithValue(witness.Id);
            await register.ExecuteNonQueryAsync();
        }
        foreach (var claim in new[] { xc, yc, link }.Concat(declaredClaims))
        {
            await using var standing = new NpgsqlCommand("""
                INSERT INTO public.consensus(claim,rating,deviation,volatility,matches)
                SELECT $1,s.rating,s.deviation,s.volatility,s.matches
                FROM public.laplace_series(ARRAY[$2]::public.blake3[],ARRAY[$3]::bigint[],
                    ARRAY[1.0]::double precision[],ARRAY[1.0]::double precision[]) s
                """, connection, transaction);
            standing.Parameters.AddWithValue(claim.Id);
            standing.Parameters.AddWithValue(witness.Id);
            standing.Parameters.AddWithValue(claim.Id == xc.Id ? 3L : 1L);
            await standing.ExecuteNonQueryAsync();
        }
        await using (var games = new NpgsqlCommand("SELECT matches FROM public.consensus WHERE claim=$1", connection, transaction))
        {
            games.Parameters.AddWithValue(xc.Id);
            Assert.Equal(3L, await games.ExecuteScalarAsync());
        }
        await using (var registry = NpgsqlCatalog.Command(connection, transaction, "foundry.source_ids",
            Convert.ToHexString(witness.Id.ToBytes())))
        await using (var rows = await registry.ExecuteReaderAsync())
        {
            Assert.True(await rows.ReadAsync()); Assert.Equal(witness.Id.ToBytes(), rows.GetFieldValue<byte[]>(0));
            Assert.False(await rows.ReadAsync());
        }
        // Foundry consumes the same Native carriers, including their actual
        // identity in path evidence and in scoped database royalty settlement.
        foreach (string selector in new[] { "!", "IS_ANTONYM_OF" })
        foreach (bool reverse in new[] { false, true })
        {
            string steps = System.Text.Json.JsonSerializer.Serialize(new[] { new { types = new[] { selector }, reverse } });
            await using var path = NpgsqlCatalog.Command(connection, transaction, "generation.path_plane",
                new[] { (reverse ? value : x).Id.ToBytes() }, steps, 10, 10, false, DBNull.Value);
            await using var rows = await path.ExecuteReaderAsync();
            int found = 0;
            while (await rows.ReadAsync())
            {
                using var evidence = System.Text.Json.JsonDocument.Parse(rows.GetString(3));
                foreach (var route in evidence.RootElement.EnumerateArray())
                foreach (var claim in route.GetProperty("claims").EnumerateArray())
                {
                    string id = claim.GetProperty("claim_id").GetString()!;
                    Assert.Contains(id, new[] { "\\x" + Convert.ToHexStringLower(xc.Id.ToBytes()), "\\x" + Convert.ToHexStringLower(yc.Id.ToBytes()) });
                    found++;
                }
            }
            Assert.Equal(reverse ? 2 : 1, found);
        }
        foreach (bool present in new[] { true, false })
        {
            await using var scoped = NpgsqlCatalog.Command(connection, transaction, "generation.path_plane",
                new[] { x.Id.ToBytes() }, "[{\"types\":[\"!\"]}]", 10, 10, false,
                new[] { present ? witness.Id.ToBytes() : new byte[16] });
            await using var rows = await scoped.ExecuteReaderAsync();
            Assert.Equal(present, await rows.ReadAsync());
            Assert.False(await rows.ReadAsync());
        }
        await using (var royalty = NpgsqlCatalog.Command(connection, transaction, "generation.export_source_attribution",
            new[] { xc.Id.ToBytes(), yc.Id.ToBytes(), xc.Id.ToBytes() }, new[] { witness.Id.ToBytes() }))
        await using (var rows = await royalty.ExecuteReaderAsync())
        {
            Assert.True(await rows.ReadAsync());
            Assert.Equal(witness.Id.ToBytes(), rows.GetFieldValue<byte[]>(0));
            Assert.Equal(2L, rows.GetInt64(1)); Assert.Equal(4L, rows.GetInt64(2));
            Assert.True(rows.GetDouble(3) > 0); Assert.Equal(10000, rows.GetInt32(4));
            Assert.False(await rows.ReadAsync());
        }
        // The witness repeats xc three times. Evidence counts one (claim,witness) pair.
        await using (var selected = new NpgsqlCommand("""
            SELECT nbr,witness_count FROM consensus.explore_web_neighbors(
                ARRAY[$1,$1]::bytea[],ARRAY[$2]::bytea[],1,false) WHERE outbound
            """, connection, transaction))
        {
            selected.Parameters.AddWithValue(x.Id.ToBytes()); selected.Parameters.AddWithValue(predicate.Id.ToBytes());
            await using var rows = await selected.ExecuteReaderAsync();
            for (int i = 0; i < 2; i++)
            {
                Assert.True(await rows.ReadAsync());
                Assert.Equal(value.Id.ToBytes(), rows.GetFieldValue<byte[]>(0));
                Assert.Equal(1L, rows.GetInt64(1));
            }
            Assert.False(await rows.ReadAsync());
        }
        await using (var counts = NpgsqlCatalog.Command(connection, transaction, "native.claim_witness_counts",
            (object)new[] { xc.Id.ToBytes(), xc.Id.ToBytes(), new byte[16] }))
        await using (var rows = await counts.ExecuteReaderAsync())
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.True(await rows.ReadAsync()); Assert.Equal(i + 1L, rows.GetInt64(0));
                Assert.Equal(i == 2 ? 0L : 1L, rows.GetInt64(1));
            }
            Assert.False(await rows.ReadAsync());
        }
        await using (var evidence = NpgsqlCatalog.Command(connection, transaction, "ops.entity_evidence_count", x.Id.ToBytes()))
            Assert.Equal(1L, await evidence.ExecuteScalarAsync());
        await using (var evidence = NpgsqlCatalog.Command(connection, transaction, "ops.evidence_counts_batch", new[] { x.Id.ToBytes(), y.Id.ToBytes() }))
        await using (var read = await evidence.ExecuteReaderAsync())
        {
            Assert.True(await read.ReadAsync()); Assert.Equal(1L, read.GetInt64(1));
            Assert.True(await read.ReadAsync()); Assert.Equal(1L, read.GetInt64(1));
            Assert.False(await read.ReadAsync());
        }
        await using (var resolve = NpgsqlCatalog.Command(connection, transaction, "inspection.explore_resolve", x.Id.ToBytes(), xName))
        await using (var read = await resolve.ExecuteReaderAsync())
        {
            Assert.True(await read.ReadAsync()); Assert.True(read.GetBoolean(3));
            Assert.Equal(x.Id.ToBytes(), read.GetFieldValue<byte[]>(0));
        }
        await using (var receipt = NpgsqlCatalog.Command(connection, transaction, "ops.evidence_receipt", x.Id.ToBytes(), 6))
        await using (var read = await receipt.ExecuteReaderAsync())
        {
            Assert.True(await read.ReadAsync()); Assert.Equal("IS_ANTONYM_OF", read.GetString(1));
            Assert.Equal(Convert.ToHexStringLower(witness.Id.ToBytes()), read.GetString(4)); Assert.Equal(1L, read.GetInt64(5));
        }
        await using (var couple = NpgsqlCatalog.Command(connection, transaction, "conversation.couple_topics", x.Id.ToBytes(), y.Id.ToBytes()))
        await using (var read = await couple.ExecuteReaderAsync())
        {
            Assert.True(await read.ReadAsync());
            Assert.Equal(xsense.Id.ToBytes(), read.GetFieldValue<byte[]>(0));
            Assert.Equal(ysense.Id.ToBytes(), read.GetFieldValue<byte[]>(1));
        }
        var absentId = Hash128.Text("unwitnessed-" + Guid.NewGuid().ToString("N"));
        await using (var resolve = NpgsqlCatalog.Command(connection, transaction, "conversation.resolve_refs_with_labels",
            new[] { x.Id.ToBytes(), y.Id.ToBytes(), x.Id.ToBytes(), absentId.ToBytes() },
            new string?[] { xName, yName, null, null }))
        await using (var read = await resolve.ExecuteReaderAsync())
        {
            foreach (var expected in new[] { x.Id, y.Id, x.Id, absentId }.Select((id, i) => (id, i)))
            {
                Assert.True(await read.ReadAsync());
                Assert.Equal(expected.i + 1L, read.GetInt64(0));
                Assert.Equal(expected.id.ToBytes(), read.GetFieldValue<byte[]>(1));
                Assert.Equal(expected.i != 3, read.GetBoolean(3));
                if (expected.i == 2) Assert.Equal(xName, read.GetString(2));
            }
            Assert.False(await read.ReadAsync());
        }
        await using (var record = NpgsqlCatalog.Command(connection, transaction, "ops.entity_record", x.Id.ToBytes()))
        await using (var read = await record.ExecuteReaderAsync())
        {
            Assert.True(await read.ReadAsync());
            Assert.Equal(1L, Enumerable.Range(0, 4).Sum(read.GetInt64));
        }
        await using (var facts = NpgsqlCatalog.Command(connection, transaction, "consensus.salient_facts_rendered", x.Id.ToBytes(), 6))
        await using (var read = await facts.ExecuteReaderAsync())
        {
            Assert.True(await read.ReadAsync());
            Assert.Equal("IS_ANTONYM_OF", read.GetString(0));
            Assert.Equal("shared", read.GetString(1));
            Assert.Equal(1L, read.GetInt64(3));
        }
        await using (var tape = NpgsqlCatalog.Command(connection, transaction, "converse.contrast", x.Id.ToBytes(), y.Id.ToBytes(), 60))
        await using (var read = await tape.ExecuteReaderAsync())
        {
            Assert.True(await read.ReadAsync());
            Assert.Equal("both", read.GetString(0));
            Assert.Equal("IS_ANTONYM_OF", read.GetString(1));
            Assert.Equal("shared", read.GetString(2));
            Assert.False(await read.ReadAsync());
        }
        foreach (bool reverse in new[] { false, true })
        {
            await using var summary = NpgsqlCatalog.Command(connection, transaction, "consensus.relation_summary",
                (reverse ? value : x).Id.ToBytes(), (reverse ? x : value).Id.ToBytes());
            await using var rows = await summary.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal("IS_ANTONYM_OF", rows.GetString(0));
            Assert.Equal("direct witnessed claims", rows.GetString(1));
            Assert.True(rows.GetDecimal(2) > 0); Assert.Equal(3L, rows.GetInt64(3));
            Assert.False(await rows.ReadAsync());
        }
        await using (var predicateOnly = NpgsqlCatalog.Command(connection, transaction, "consensus.relation_summary",
            x.Id.ToBytes(), predicate.Id.ToBytes()))
        await using (var rows = await predicateOnly.ExecuteReaderAsync())
        {
            Assert.True(await rows.ReadAsync());
            Assert.True(rows.IsDBNull(0)); Assert.True(rows.IsDBNull(2)); Assert.True(rows.IsDBNull(3));
            Assert.False(await rows.ReadAsync());
        }
        // Identity resolution, UI path reads and recall share canonical stored
        // content and the same producer-compiled program, with no legacy rows.
        await using (var resolve = new NpgsqlCommand("SELECT converse.resolve_phrase($1),converse.resolve_last_word($1)", connection, transaction))
        {
            resolve.Parameters.AddWithValue(definedName);
            await using var rows = await resolve.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(defined.Id.ToBytes(), rows.GetFieldValue<byte[]>(0));
            Assert.Equal(defined.Id.ToBytes(), rows.GetFieldValue<byte[]>(1));
        }
        await using (var compiled = new NpgsqlCommand("SELECT converse.query_program()::text", connection, transaction))
        {
            var installed = NativeQueryProgram.Parse(Encoding.UTF8.GetBytes((string)(await compiled.ExecuteScalarAsync())!));
            Assert.Equal(declared.Paths.SelectMany(path => path.Select(r => (r.Declaration, Convert.ToHexString(r.Entity)))),
                installed.Paths.SelectMany(path => path.Select(r => (r.Declaration, Convert.ToHexString(r.Entity)))));
        }
        var meanings = await NpgsqlSubstrateReads.DeclaredPathsAsync(connection, [defined.Id.ToBytes()], declared);
        var meaning = Assert.Single(meanings);
        Assert.Equal(3, meaning.Steps.Count);
        Assert.Equal(definition.Id.ToBytes(), meaning.Steps[^1].EntityId);
        Assert.Equal("native witnessed definition", meaning.Steps[^1].Label);
        Assert.Equal(1, meaning.Steps[^1].Witnesses);
        var part = new NativeQueryRelation("@fixture/relation", predicate.Id.ToBytes());
        var duplicateProgram = new NativeQueryProgram([[part], [part], [part, part]], [part]);
        var repeated = await NpgsqlSubstrateReads.DeclaredPathsAsync(connection,
            [x.Id.ToBytes(), y.Id.ToBytes(), x.Id.ToBytes(), absentId.ToBytes()], duplicateProgram);
        Assert.Equal(6, repeated.Count);
        Assert.All(repeated, path =>
        {
            Assert.InRange(path.RootIndex, 0, 2); Assert.InRange(path.ProgramIndex, 0, 1);
            Assert.Equal(value.Id.ToBytes(), Assert.Single(path.Steps).EntityId);
        });
        await using (var recall = new NpgsqlCommand("SELECT reply,witnesses FROM converse.recall($1)", connection, transaction))
        {
            recall.Parameters.AddWithValue(definedName);
            await using var rows = await recall.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(definedName + ": native witnessed definition", rows.GetString(0));
            Assert.Equal(1L, rows.GetInt64(1));
            Assert.False(await rows.ReadAsync());
        }
        await transaction.RollbackAsync();
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
