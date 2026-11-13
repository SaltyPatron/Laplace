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
        var localizedDefinition = Compose([definition,language],4);
        var nonLanguagePair = Compose([definition,context],4);
        var declaredClaims = new[] { Compose([defined, senseRelation, definedSense], 1),
            Compose([definedSense, referenceRelation, context], 1), Compose([context, definitionRelation, definition], 1) };
        for (int index = 0; index < declaredClaims.Length; index++) declaredClaims[index].Said = 1;
        var witness = Compose([xc, xc, xc, yc, link, .. declaredClaims,localizedDefinition,nonLanguagePair]);
        var writer = new NpgsqlSubstrateWriter(native);
        await writer.ApplyCanonicalWorkingSetAsync(window, connection, transaction, [witness.Id]);
        await using (var curve = new NpgsqlCommand("SELECT public.ST_NPoints(structural.entity_curve($1))", connection, transaction))
        {
            curve.Parameters.AddWithValue(witness.Id.ToBytes());
            // The repeated xc run must remain three ordered points. The curve
            // comes entirely from Native carriers while compatibility rows are absent.
            // Eight original source positions and the two literal-shape records.
            Assert.Equal(10, await curve.ExecuteScalarAsync());
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
        var attested = new[] { xc, yc, link }.Concat(declaredClaims).ToArray();
        foreach (var claim in attested)
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
        // Cardinality belongs to claim/witness pairs. Before the source publishes
        // its attestations it has none; three games on xc do not make three assertions.
        await using (var totals = new NpgsqlCommand("SELECT ops.attestation_count()", connection, transaction))
            Assert.Equal(0L, await totals.ExecuteScalarAsync());
        await using (var sourceCount = new NpgsqlCommand("SELECT ops.evidence_count(p_source=>$1)", connection, transaction))
        {
            sourceCount.Parameters.AddWithValue(witness.Id.ToBytes());
            Assert.Equal(0L, await sourceCount.ExecuteScalarAsync());
        }
        foreach (var claim in attested)
        {
            long games = claim.Id == xc.Id ? 3L : 1L;
            await using var publish = new NpgsqlCommand("""
                INSERT INTO public.attestation(claim,witness,lineage,games,tokens,score,"position",trust)
                VALUES($1,$2,NULL,$3,$3,1.0,NULL,1.0)
                """, connection, transaction);
            publish.Parameters.AddWithValue(claim.Id);
            publish.Parameters.AddWithValue(witness.Id);
            publish.Parameters.AddWithValue(games);
            Assert.Equal(1, await publish.ExecuteNonQueryAsync());
        }
        await using (var totals = new NpgsqlCommand("SELECT ops.attestation_count()", connection, transaction))
            Assert.Equal(6L, await totals.ExecuteScalarAsync());
        await using (var sourceCount = new NpgsqlCommand("SELECT ops.evidence_count(p_source=>$1)", connection, transaction))
        {
            sourceCount.Parameters.AddWithValue(witness.Id.ToBytes());
            Assert.Equal(6L, await sourceCount.ExecuteScalarAsync());
        }
        await using (var selected = new NpgsqlCommand("SELECT ops.evidence_count(p_type=>$1)", connection, transaction))
        {
            selected.Parameters.AddWithValue(predicate.Id.ToBytes());
            Assert.Equal(3L, await selected.ExecuteScalarAsync());
        }
        // The catalogue count is the planner-statistics estimate (ops.source_counts_approx); a partition
        // never analyzed is left out, so the published attestations are analyzed first. ANALYZE counts
        // this transaction's own inserts as live.
        await using (var analyze = new NpgsqlCommand("ANALYZE public.attestation", connection, transaction))
            await analyze.ExecuteNonQueryAsync();
        var inventory = await NpgsqlSubstrateReads.SourceCountsAsync(connection, CancellationToken.None);
        Assert.Equal(6L, Assert.Single(inventory).Evidence);
        Assert.Null(inventory[0].Content);
        var health = await NpgsqlSubstrateReads.ConsensusStatsExactAsync(connection, CancellationToken.None);
        Assert.Equal(6L, health!.Value.EvidenceRows);
        Assert.Equal(6L, health.Value.ConsensusRows);
        await using (var sourceState = new NpgsqlCommand("SELECT evidence_approx,ingested FROM ops.source_status(NULL) WHERE source_id=$1", connection, transaction))
        {
            sourceState.Parameters.AddWithValue(witness.Id.ToBytes());
            await using var rows = await sourceState.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(6L, rows.GetInt64(0));
            Assert.True(rows.GetBoolean(1));
            Assert.False(await rows.ReadAsync());
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
        // Inspection must also expose non-dictionary claims, repeated operand
        // occurrences, and structural routes without adding compatibility edges.
        var field = await NpgsqlSubstrateReads.ResponseFieldAsync(connection,
            [x.Id.ToBytes(), y.Id.ToBytes(), x.Id.ToBytes()], 512, 0);
        foreach (int ordinal in new[] { 1, 3 })
        {
            var response = Assert.Single(field, row => row.Round == 1 && row.Occurrence == ordinal
                && row.Route == 0 && row.Via!.AsSpan().SequenceEqual(xc.Id.ToBytes()));
            Assert.Equal(3L, response.Matches);
            Assert.Null(response.Witnesses);
            Assert.Equal(true, response.Outbound);
            Assert.Equal(value.Id.ToBytes(), response.Entity);
            Assert.Equal(predicate.Id.ToBytes(), response.Relation);
            Assert.Equal([ordinal], response.Origins);
        }
        Assert.Contains(field, row => row.Round == 1 && row.Route == 1
            && row.Entity.AsSpan().SequenceEqual(xsense.Id.ToBytes()));
        Assert.Contains(field, row => row.Round == 2 && row.Route == 0
            && row.Via!.AsSpan().SequenceEqual(link.Id.ToBytes()) && row.Outbound == false);
        Assert.All(field.Where(row => row.Via is not null && row.Via.AsSpan().SequenceEqual(link.Id.ToBytes())),row =>
        {
            Assert.Empty(row.Targets);
            Assert.Empty(row.Anchors);
        });
        var descriptors = await NpgsqlSubstrateReads.LexiconRelationsAsync(connection,
            [definitionRelation.Id.ToBytes(),predicate.Id.ToBytes()],["definition","example"]);
        Assert.Contains(descriptors,row => row.Id.AsSpan().SequenceEqual(definitionRelation.Id.ToBytes()) && row.Descriptive);
        Assert.Contains(descriptors,row => row.Id.AsSpan().SequenceEqual(predicate.Id.ToBytes())
            && !row.Descriptive && row.Canonical == "IS_ANTONYM_OF");
        Assert.All(field.Where(row => row.Route == 0), row => Assert.NotNull(row.Via));
        // A shared constituent or a related claim does not bind these words
        // to a meaning. The declared word/language/key record does.
        Assert.All(field.Where(row => row.Anchor is not null
            && !row.Anchor.AsSpan().SequenceEqual(x.Id.ToBytes()) && !row.Anchor.AsSpan().SequenceEqual(y.Id.ToBytes())),
            row => Assert.Empty(row.ReadingOrigins));
        Assert.Contains(field,row => row.Anchor is not null && row.Anchor.AsSpan().SequenceEqual(x.Id.ToBytes())
            && row.ReadingOrigins.SequenceEqual([1,3]));
        await using (var binding = new NpgsqlCommand(
            "SELECT key,binding,witnesses FROM converse.binding_evidence(ARRAY[$1::bytea])",connection,transaction))
        {
            binding.Parameters.AddWithValue(defined.Id.ToBytes());
            await using var bindingRows = await binding.ExecuteReaderAsync();
            Assert.True(await bindingRows.ReadAsync());
            Assert.Equal(context.Id.ToBytes(),bindingRows.GetFieldValue<byte[]>(0));
            Assert.Equal(definedSense.Id.ToBytes(),bindingRows.GetFieldValue<byte[]>(1));
            Assert.True(bindingRows.GetInt64(2)>0);
            Assert.False(await bindingRows.ReadAsync());
        }
        var entry = await NpgsqlSubstrateReads.LexiconFieldAsync(connection,[x.Id.ToBytes()]);
        var external = Assert.Single(entry,row=>row.Via!.AsSpan().SequenceEqual(xc.Id.ToBytes()));
        Assert.Equal(value.Id.ToBytes(),external.Entity);
        Assert.Equal(3L,external.Matches);
        Assert.Null(external.Witnesses);
        Assert.Empty(external.Targets);
        Assert.Equal([1],external.ReadingOrigins);
        var boundField = await NpgsqlSubstrateReads.ResponseFieldAsync(connection,[defined.Id.ToBytes()],512,0);
        Assert.Contains(boundField,row => row.Label == "native witnessed definition" && row.ReadingOrigins.SequenceEqual([1])
            && row.BindingClaims.Length>0 && row.Matches>0 && row.Witnesses is null);
        await using (var literalRead = new NpgsqlCommand("SELECT public.blake3_send(entity),content,textual FROM converse.lexicon_texts(ARRAY[$1,$2]::public.blake3[])",connection,transaction))
        {
            literalRead.Parameters.AddWithValue(localizedDefinition.Id);
            literalRead.Parameters.AddWithValue(nonLanguagePair.Id);
            await using var literals=await literalRead.ExecuteReaderAsync();
            int returned=0;
            while(await literals.ReadAsync())
            {
                bool localized=literals.GetFieldValue<byte[]>(0).AsSpan().SequenceEqual(localizedDefinition.Id.ToBytes());
                Assert.Equal(localized,literals.GetBoolean(2));
                if(localized)Assert.Equal("native witnessed definition",literals.GetString(1));
                returned++;
            }
            Assert.Equal(2,returned);
        }
        var descriptions = await NpgsqlSubstrateReads.LexiconDescriptionsAsync(connection,[context.Id.ToBytes(),absentId.ToBytes()]);
        var description = Assert.Single(descriptions);
        Assert.Equal(context.Id.ToBytes(),description.Id);
        Assert.Equal("native witnessed definition",description.Label);
        var lexicalField = await NpgsqlSubstrateReads.LexiconFieldAsync(connection,[defined.Id.ToBytes()]);
        Assert.Contains(lexicalField,row => row.Label == "native witnessed definition" && row.ReadingOrigins.SequenceEqual([1])
            && row.BindingClaims.Length>0 && row.Matches>0 && row.Witnesses is null);
        Assert.All(lexicalField,row => Assert.Equal(0,row.Route));
        await using (var storedBinding = new NpgsqlCommand(
            "SELECT key,witnesses FROM converse.binding_evidence(ARRAY[$1::bytea],NULL,false)",connection,transaction))
        {
            storedBinding.Parameters.AddWithValue(defined.Id.ToBytes());
            await using var bindingRows = await storedBinding.ExecuteReaderAsync();
            Assert.True(await bindingRows.ReadAsync());
            Assert.Equal(context.Id.ToBytes(),bindingRows.GetFieldValue<byte[]>(0));
            Assert.True(bindingRows.IsDBNull(1));
            Assert.False(await bindingRows.ReadAsync());
        }
        Assert.All(field.Where(row => row.Route != 0), row =>
        {
            Assert.Null(row.Rating); Assert.Null(row.Matches); Assert.Null(row.Witnesses);
        });
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
}
