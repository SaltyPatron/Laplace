using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Operational;
using Laplace.Decomposers.UD;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class OperationalSourceExecutionTests(LocalPgFixture pg)
{
    [Fact]
    public async Task AuthoredTaskSource_ExecutesNovelRequestAfterSharedAdmissionAndFold()
    {
        CodepointPerfcache.LoadDefault();
        LanguageReference.EnsureLoaded();
        string scope = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "laplace-operational-execution-" + scope);
        Directory.CreateDirectory(directory);
        string cue = "ζαλκ" + scope;
        string exampleWord = "próbulo" + scope;
        string inputWord = "ñébulo" + scope;
        string exemplarText = cue + " " + exampleWord;
        string prompt = cue + " " + inputWord;
        Hash128 source = Hash128.OfCanonical("test/operational-execution/source/" + scope);
        Hash128 context = Hash128.OfCanonical("test/operational-execution/context/" + scope);
        Hash128 input = Hash128.OfCanonical("test/operational-execution/input/" + scope);
        Hash128 answer = Hash128.OfCanonical("test/operational-execution/answer/" + scope);
        Hash128 changedAnswer = Hash128.OfCanonical("test/operational-execution/changed-answer/" + scope);
        Hash128 definition = Hash128.OfCanonical("test/operational-execution/definition/" + scope);
        Hash128 causes = RelationTypeRegistry.Resolve("CAUSES").Id;
        Hash128 defines = RelationTypeRegistry.Resolve("HAS_DEFINITION").Id;
        Hash128 hasParse = RelationTypeRegistry.Resolve("HAS_PARSE").Id;
        Hash128 exampleOf = RelationTypeRegistry.Resolve("IS_EXAMPLE_OF").Id;
        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource, persistEvidence: true);
        var runner = new IngestRunner(writer, new NpgsqlSubstrateReader(pg.DataSource),
            NullLoggerFactory.Instance, new NpgsqlIngestObservability(pg.DataSource, evidencePersisted: true));

        try
        {
            // The exemplar is admitted by the actual CoNLL-U source. The test
            // never manufactures a parse or shape trajectory in SQL.
            string udPath = Path.Combine(directory, "en_" + scope + "_test.conllu");
            await File.WriteAllTextAsync(udPath,
                $"# sent_id = {scope}\n# text = {exemplarText}\n"
                + $"1\t{cue}\t{cue}\tVERB\t_\t_\t0\troot\t_\t_\n"
                + $"2\t{exampleWord}\t{exampleWord}\tNOUN\t_\t_\t1\tobj\t_\t_\n\n");
            await Ingest(new UDDecomposer(), udPath);
            Hash128 exemplarRoot = ContentTierSpine.ResolveRoot(exemplarText)!.Value;
            Hash128 parse;
            await using (var query = pg.DataSource.CreateCommand(
                "SELECT object_id FROM laplace.attestations "
                + "WHERE subject_id=$1 AND type_id=$2 AND source_id=$3 AND outcome=2"))
            {
                query.Parameters.AddWithValue(exemplarRoot.ToBytes());
                query.Parameters.AddWithValue(hasParse.ToBytes());
                query.Parameters.AddWithValue(UDSource.SourceId.ToBytes());
                await using var rows = await query.ExecuteReaderAsync();
                Assert.True(await rows.ReadAsync(), "Actual UD admission must persist its witnessed parse.");
                parse = Hash128.FromBytes(rows.GetFieldValue<byte[]>(0));
                Assert.False(await rows.ReadAsync());
            }

            var facts = new SubstrateChangeBuilder(source, "operational-execution-facts/" + scope);
            facts.AddEntity(source, EntityTier.Word, EntityTypeRegistry.SourceReference, source);
            facts.AddEntity(context, EntityTier.Word, EntityTypeRegistry.SourceReference, source);
            foreach (Hash128 id in new[] { input, answer, changedAnswer, definition })
                facts.AddEntity(id, EntityTier.Word, EntityTypeRegistry.CodeConcept, source);
            Assert.True(ContentTierSpine.TryStageIntoBuilder(facts, Encoding.UTF8.GetBytes(prompt), source, out Hash128 promptRoot));
            Hash128 inputSurface = ContentTierSpine.ResolveRoot(inputWord)!.Value;
            facts.AddAttestation(NativeAttestation.CategoricalResolved(inputSurface,
                RelationTypeRegistry.Resolve("HAS_SENSE").Id, input, source, context, SourceTrust.SubstrateMandate));
            facts.AddAttestation(Fact(input, causes, answer));
            facts.AddAttestation(Fact(input, defines, definition));
            await Apply(facts.Build());
            await AssertNoCurrentParseOrInvocation(promptRoot);
            Receipt absent = await Forward(prompt);
            Assert.False(absent.Complete);
            Assert.Empty(absent.Emitted);

            string shapePath = Path.Combine(directory, "shape.json");
            var first = await AdmitShape(causes);
            await AssertPersistedContract(first.Shape, first.File);
            Receipt original = await Forward(prompt);
            Assert.True(original.Complete, original.Disposition);
            Assert.Equal(answer, Assert.Single(original.Emitted));
            await AssertNoCurrentParseOrInvocation(promptRoot);

            // Withdraw one actual fact, then deposit its replacement through the
            // shared native witness/fold path. The task declaration is unchanged.
            await using (var retract = pg.DataSource.CreateCommand(
                "DELETE FROM laplace.attestations WHERE subject_id=$1 AND type_id=$2 AND object_id=$3 AND source_id=$4"))
            {
                retract.Parameters.AddWithValue(input.ToBytes());
                retract.Parameters.AddWithValue(causes.ToBytes());
                retract.Parameters.AddWithValue(answer.ToBytes());
                retract.Parameters.AddWithValue(source.ToBytes());
                Assert.Equal(1, await retract.ExecuteNonQueryAsync());
            }
            await using (var retract = pg.DataSource.CreateCommand(
                "DELETE FROM laplace.consensus WHERE subject_id=$1 AND type_id=$2 AND object_id=$3"))
            {
                retract.Parameters.AddWithValue(input.ToBytes());
                retract.Parameters.AddWithValue(causes.ToBytes());
                retract.Parameters.AddWithValue(answer.ToBytes());
                Assert.Equal(1, await retract.ExecuteNonQueryAsync());
            }
            await Apply(new SubstrateChangeBuilder(source, "operational-execution-replacement/" + scope)
                .AddAttestation(Fact(input, causes, changedAnswer)).Build());
            Receipt changed = await Forward(prompt);
            Assert.True(changed.Complete, changed.Disposition);
            Assert.Equal(changedAnswer, Assert.Single(changed.Emitted));

            // Editing the file admits a distinct shape and file context. Both
            // witnessed declarations remain active until one is withdrawn.
            var second = await AdmitShape(defines);
            Assert.NotEqual(first.Shape.Id, second.Shape.Id);
            Assert.NotEqual(first.File, second.File);
            await AssertPersistedContract(second.Shape, second.File);
            Receipt alternatives = await Forward(prompt);
            Assert.False(alternatives.Complete);
            Assert.Equal("ambiguous", alternatives.Disposition);
            Assert.Empty(alternatives.Emitted);
            await using (var retract = pg.DataSource.CreateCommand(
                "DELETE FROM laplace.attestations WHERE subject_id=$1 AND type_id=$2 AND object_id=$3 "
                + "AND source_id=$4 AND context_id=$5"))
            {
                retract.Parameters.AddWithValue(parse.ToBytes());
                retract.Parameters.AddWithValue(exampleOf.ToBytes());
                retract.Parameters.AddWithValue(first.Shape.Id.ToBytes());
                retract.Parameters.AddWithValue(OperationalSource.SourceId.ToBytes());
                retract.Parameters.AddWithValue(first.File.ToBytes());
                Assert.Equal(1, await retract.ExecuteNonQueryAsync());
            }
            Receipt replacement = await Forward(prompt);
            Assert.True(replacement.Complete, replacement.Disposition);
            Assert.Equal(definition, Assert.Single(replacement.Emitted));
            Assert.NotEqual(original.ProgramId, replacement.ProgramId);
            await AssertNoCurrentParseOrInvocation(promptRoot);

            async Task<(OperationalTaskShapeWitness.Definition Shape, Hash128 File)> AdmitShape(Hash128 predicate)
            {
                string json = $$"""
                    {
                      "schema": "laplace/task-shape/relation-read/token-slots/v1",
                      "exemplar_parse_id": "{{Hex(parse)}}",
                      "predicate_id": "{{Hex(predicate)}}",
                      "slots": [{"exemplar_token_ref_id": "{{Hex(UdParseStructure.TokenRefId("2"))}}",
                                 "accepted_entity_type_id": "{{Hex(EntityTypeRegistry.CodeConcept)}}"}]
                    }
                    """;
                await File.WriteAllTextAsync(shapePath, json);
                await Ingest(new OperationalDecomposer(), shapePath);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                using var ast = GrammarDecomposer.Parse(bytes, "json");
                var declared = OperationalTaskShapeWitness.Read(ast, bytes);
                using var composer = new GrammarRowComposer(bytes, ast, OperationalSource.SourceId,
                    "json", GrammarCompositionMode.FullSource);
                FileIdentity file = FileEntity.Resolve(composer.RootComponent(),
                    GrammarSourceFileSupport.MetadataFromPath(shapePath, "shape.json", "json"));
                return (declared, file.FileId);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }

        AttestationRow Fact(Hash128 subject, Hash128 predicate, Hash128 result) =>
            NativeAttestation.CategoricalResolved(subject, predicate, result, source, context, SourceTrust.SubstrateMandate);

        async Task Ingest(IDecomposer decomposer, string path)
        {
            IngestRunResult result = await runner.RunAsync(decomposer, IngestRunOptions.Default with
            {
                EcosystemPath = path,
                SkipLayerOrderingCheck = true,
                SkipSourceCompletion = true,
            });
            Assert.Empty(result.Failures);
            Assert.Equal(0, result.UnitsFailed);
            Assert.Equal(1, result.FilesDone);
            Assert.Equal(1, result.InputUnitsDone);
        }

        async Task Apply(SubstrateChange change)
        {
            try { await writer.ApplyAsync(change); }
            finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
        }
    }

    private async Task AssertPersistedContract(OperationalTaskShapeWitness.Definition shape, Hash128 file)
    {
        var expected = new[]
        {
            (shape.ExemplarParseId, OperationalSource.ExampleOfTypeId, shape.Id),
            (shape.Id, OperationalSource.CallsTypeId, shape.PredicateId),
            (shape.Id, OperationalSource.InputTypeId, Assert.Single(shape.Slots).Id),
        };
        foreach (var (subject, relation, obj) in expected)
        {
            await using var query = pg.DataSource.CreateCommand(
                "SELECT a.context_id,a.outcome,a.observation_count,c.rating,c.witness_count "
                + "FROM laplace.attestations a JOIN laplace.consensus c "
                + "ON c.subject_id=a.subject_id AND c.type_id=a.type_id AND c.object_id=a.object_id "
                + "WHERE a.subject_id=$1 AND a.type_id=$2 AND a.object_id=$3 AND a.source_id=$4");
            query.Parameters.AddWithValue(subject.ToBytes());
            query.Parameters.AddWithValue(relation.ToBytes());
            query.Parameters.AddWithValue(obj.ToBytes());
            query.Parameters.AddWithValue(OperationalSource.SourceId.ToBytes());
            await using var row = await query.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(file.ToBytes(), row.GetFieldValue<byte[]>(0));
            Assert.Equal(2, row.GetInt16(1));
            Assert.Equal(1, row.GetInt64(2));
            Assert.True(row.GetInt64(3) > Glicko2.DefaultRatingFp1e9,
                "One source observation must acquire positive standing through the actual fold.");
            Assert.Equal(1, row.GetInt64(4));
            Assert.False(await row.ReadAsync());
        }
        await using var physicality = pg.DataSource.CreateCommand(
            "SELECT id,type,n_constituents FROM laplace.physicalities WHERE entity_id=$1 AND type=8");
        physicality.Parameters.AddWithValue(shape.Id.ToBytes());
        await using (var row = await physicality.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.Equal(PhysicalityId.Compute(shape.Id, PhysicalityType.ParseStructure).ToBytes(), row.GetFieldValue<byte[]>(0));
            Assert.Equal(8, row.GetInt16(1));
            Assert.Equal(shape.Constituents.Length, row.GetInt32(2));
            Assert.False(await row.ReadAsync());
        }
        await using var trajectory = pg.DataSource.CreateCommand(
            "SELECT entity_id FROM generation.trajectory_unpacked_points($1,8::smallint)");
        trajectory.Parameters.AddWithValue(shape.Id.ToBytes());
        await using var rows = await trajectory.ExecuteReaderAsync();
        var constituents = new List<Hash128>();
        while (await rows.ReadAsync()) constituents.Add(Hash128.FromBytes(rows.GetFieldValue<byte[]>(0)));
        Assert.Equal(shape.Constituents, constituents);
    }

    private async Task AssertNoCurrentParseOrInvocation(Hash128 root)
    {
        await using var command = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.attestations WHERE subject_id=$1 AND type_id=ANY($2)");
        command.Parameters.AddWithValue(root.ToBytes());
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            new[] { "HAS_PARSE", "CALLS", "HAS_INPUT" }.Select(n => RelationTypeRegistry.Resolve(n).Id.ToBytes()).ToArray());
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    private sealed record Receipt(Hash128[] Emitted, bool Complete, string Disposition, Hash128 ProgramId);

    private async Task<Receipt> Forward(string prompt)
    {
        await using var command = pg.DataSource.CreateCommand(
            "SELECT event,entity,completion,disposition,program_id "
            + "FROM generation.forward_program($1,2,0,0.0,16,7,2,256,NULL,NULL) ORDER BY step");
        command.Parameters.AddWithValue(prompt);
        await using var rows = await command.ExecuteReaderAsync();
        var emitted = new List<Hash128>();
        bool complete = false;
        string disposition = "no receipt";
        Hash128 program = default;
        while (await rows.ReadAsync())
        {
            string kind = rows.GetString(0);
            if (kind == "emit") emitted.Add(Hash128.FromBytes(rows.GetFieldValue<byte[]>(1)));
            if (kind is "complete" or "unresolved")
            {
                complete = rows.GetBoolean(2);
                disposition = rows.GetString(3);
                program = Hash128.FromBytes(rows.GetFieldValue<byte[]>(4));
            }
        }
        Assert.NotEqual(default, program);
        return new Receipt(emitted.ToArray(), complete, disposition, program);
    }

    private static string Hex(Hash128 id) => Convert.ToHexString(id.ToBytes()).ToLowerInvariant();
}
