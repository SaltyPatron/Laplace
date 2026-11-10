using System.Text;
using Laplace.Ingestion;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed partial class NativeSqlCatalogPreparationTests
{
    [Fact]
    public async Task NativeRecordTestimonyFeedsTheSharedReaderWithoutLegacyRowsOrFabricatedOutcomes()
    {
        await using var native = LaplaceDataSource.Create(SubstrateAccess.Ingest, _source.ConnectionString);
        await using var connection = await native.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var window = new StagedSourceWriter(10000, 100000, 1 << 24);
        string suffix = Guid.NewGuid().ToString("N");
        StagedReference Text(string value) => window.Text(Encoding.UTF8.GetBytes(value));
        StagedReference Compose(StagedReference[] children, ulong mask = 0) =>
            window.Compose(children, checked((byte)(children.Max(c => c.Tier) + 1)), mask, mask);
        var subject = Text("testimony" + suffix); var predicate = Text("relation" + suffix); var target = Text("target" + suffix);
        var claim = Compose([subject, predicate, target], 1);
        var saying = claim; saying.Said = 1;
        var positive = Compose([Text("positive" + suffix), saying, saying]);
        saying.Outcome = 1;
        var draw = Compose([Text("draw" + suffix), saying]);
        saying.Outcome = 2;
        var negative = Compose([Text("negative" + suffix), saying]);
        saying.Outcome = 3; saying.Spare = 1u | (1u << 26); saying.Position = 17; // Native LP_SPARE_SCORE, exact 0.25.
        var dependence = Text("DerivedCalculation"); dependence.Said = 7;
        var scored = Compose([Text("scored" + suffix), dependence, saying]);
        var witness = Compose([positive, positive, draw, negative, scored]);
        await new NpgsqlSubstrateWriter(native).ApplyCanonicalWorkingSetAsync(window, connection, transaction, [witness.Id]);
        // A witness is a trunk with attestations: one fixture claim makes it known, so the
        // claims under test are still read from containment.
        await using (var register = new NpgsqlCommand("""
            INSERT INTO public.attestation(claim,witness,lineage,games,tokens,score,"position",trust)
            VALUES(md5('testimony-witness-'||$1::text)::public.blake3,$1,NULL,1,1,1.0,NULL,0.85)
            """, connection, transaction))
        {
            register.Parameters.AddWithValue(witness.Id);
            await register.ExecuteNonQueryAsync();
        }
        await using (var absent = new NpgsqlCommand("SELECT count(*) FROM laplace.attestations WHERE subject_id=$1", connection, transaction))
        {
            absent.Parameters.AddWithValue(subject.Id.ToBytes());
            Assert.Equal(0L, await absent.ExecuteScalarAsync());
        }
        var expected = new Dictionary<string, (short? Outcome, long Count, long Tokens, double Score, bool Calculation)>
        {
            [Convert.ToHexString(positive.Id.ToBytes())] = (2, 2, 4, 1, false),
            [Convert.ToHexString(draw.Id.ToBytes())] = (1, 1, 1, 0.5, false),
            [Convert.ToHexString(negative.Id.ToBytes())] = (0, 1, 1, 0, false),
            [Convert.ToHexString(scored.Id.ToBytes())] = (null, 1, 1, 0.25, true)
        };
        await using (var exact = NpgsqlCatalog.Command(connection, transaction, "native.observation_cells",
            new[] { subject.Id.ToBytes() }, new[] { predicate.Id.ToBytes() }, new[] { target.Id.ToBytes() }, new[] { witness.Id.ToBytes() }))
        await using (var rows = await exact.ExecuteReaderAsync())
        {
            int count = 0;
            while (await rows.ReadAsync())
            {
                var record = expected[Convert.ToHexString(rows.GetFieldValue<byte[]>(5))];
                Assert.Equal(claim.Id.ToBytes(), rows.GetFieldValue<byte[]>(0));
                Assert.Equal(witness.Id.ToBytes(), rows.GetFieldValue<byte[]>(4));
                Assert.Equal(record.Outcome, rows.IsDBNull(6) ? null : rows.GetInt16(6));
                Assert.Equal(record.Count, rows.GetInt64(7)); Assert.Equal(record.Tokens, rows.GetInt64(10));
                Assert.Equal(record.Score, rows.GetDouble(9)); Assert.Equal(record.Calculation, rows.GetBoolean(14));
                Assert.Equal(record.Calculation ? 17 : 0, rows.GetInt32(15));
                if (record.Calculation) Assert.Equal(dependence.Id.ToBytes(), rows.GetFieldValue<byte[]>(13));
                else Assert.True(rows.IsDBNull(13));
                count++;
            }
            Assert.Equal(4, count);
        }
        // This is the same C reader used by query evidence, prompt structure and task shapes.
        await using (var shared = new NpgsqlCommand("SELECT * FROM generation.observation_bindings($1,$2,$3,3)", connection, transaction))
        {
            shared.Parameters.AddWithValue(new[] { subject.Id.ToBytes(), subject.Id.ToBytes(), target.Id.ToBytes() });
            shared.Parameters.AddWithValue(new[] { witness.Id.ToBytes() });
            shared.Parameters.AddWithValue(new[] { predicate.Id.ToBytes() });
            await using var rows = await shared.ExecuteReaderAsync();
            var seen = new HashSet<(int, string)>();
            var references = new HashSet<string>();
            while (await rows.ReadAsync())
            {
                int ordinal = rows.GetInt32(0);
                Assert.NotEqual(claim.Id.ToBytes(), rows.GetFieldValue<byte[]>(2));
                references.Add(Convert.ToHexString(rows.GetFieldValue<byte[]>(2)));
                Assert.Equal((short)(ordinal == 3 ? 2 : 1), rows.GetInt16(1));
                string context = Convert.ToHexString(rows.GetFieldValue<byte[]>(7));
                var record = expected[context];
                Assert.Equal(record.Outcome, rows.IsDBNull(8) ? null : rows.GetInt16(8));
                Assert.Equal(record.Count, rows.GetInt64(9));
                Assert.True(seen.Add((ordinal, context)));
            }
            Assert.Equal(12, seen.Count);
            Assert.Equal(4, references.Count);
        }
        // Response support uses these same record witnesses, independently of
        // pooled standing. Source/context filters narrow support, not rating.
        await using (var standing = new NpgsqlCommand("""
            INSERT INTO public.consensus(claim,rating,deviation,volatility,matches)
            SELECT $1,1700,50,0.06,5
            """, connection, transaction))
        {
            standing.Parameters.AddWithValue(claim.Id);
            await standing.ExecuteNonQueryAsync();
        }
        foreach (var (sources, context, expectedCount) in new[]
        {
            ((byte[][]?)null, (byte[]?)null, 1L),
            (new[] { witness.Id.ToBytes() }, positive.Id.ToBytes(), 1L),
            (new[] { witness.Id.ToBytes() }, scored.Id.ToBytes(), 1L),
            (Array.Empty<byte[]>(), (byte[]?)null, 0L),
            (new[] { target.Id.ToBytes() }, (byte[]?)null, 0L),
            ((byte[][]?)null, target.Id.ToBytes(), 0L),
        })
        {
            await using var response = new NpgsqlCommand("""
                SELECT count(*),min(source_count),min(rating_fp1e9),min(rd_fp1e9)
                FROM ops.attestation_response_batch($1,$2,$3,$4,NULL,false)
                """, connection, transaction);
            response.Parameters.AddWithValue(new[] { subject.Id.ToBytes(), subject.Id.ToBytes() });
            response.Parameters.AddWithValue(predicate.Id.ToBytes());
            response.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea, (object?)sources ?? DBNull.Value);
            response.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, (object?)context ?? DBNull.Value);
            await using var rows = await response.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(expectedCount, rows.GetInt64(0));
            if (expectedCount != 0)
            {
                Assert.Equal(1, rows.GetInt32(1));
                Assert.Equal(1700000000000L, rows.GetInt64(2));
                Assert.Equal(50000000000L, rows.GetInt64(3));
            }
        }
        await transaction.RollbackAsync();
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
        // A witness is a trunk with attestations: one fixture claim makes it known, so the
        // classification claims are still read from containment.
        await using (var register = new NpgsqlCommand("""
            INSERT INTO public.attestation(claim,witness,lineage,games,tokens,score,"position",trust)
            VALUES(md5('classification-witness-'||$1::text)::public.blake3,$1,NULL,1,1,1.0,NULL,1.0)
            """, connection, transaction))
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
              FROM public.laplace_attested(ARRAY(SELECT encode(id,'hex')::public.blake3 FROM unnest($1::bytea[]) u(id)),false)
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
}
