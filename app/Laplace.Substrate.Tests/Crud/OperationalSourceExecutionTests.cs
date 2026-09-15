using System.Security.Cryptography;
using System.Text.Json;
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
    public Task AuthoredTaskSource_ExecutesNovelRequestAfterSharedAdmissionAndFold() =>
        AssertSourceExecution(throughWordNetSense: false);

    [Fact]
    public Task AuthoredTaskSource_BindsSynsetThroughTwoWitnessedNamingHops() =>
        AssertSourceExecution(throughWordNetSense: true);

    private async Task AssertSourceExecution(bool throughWordNetSense)
    {
        CodepointPerfcache.LoadDefault();
        LanguageReference.EnsureLoaded();
        string scope = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "laplace-operational-execution-" + scope);
        Directory.CreateDirectory(directory);
        string cue = throughWordNetSense ? "define" : "ζαλκ" + scope;
        string exampleWord = throughWordNetSense ? "justice" : "próbulo" + scope;
        string inputWord = "ñébulo" + scope;
        string exemplarText = cue + " " + exampleWord;
        string prompt = cue + " " + inputWord;
        Hash128 source = Hash128.OfCanonical("test/operational-execution/source/" + scope);
        Hash128 context = Hash128.OfCanonical("test/operational-execution/context/" + scope);
        Hash128 sense = Hash128.OfCanonical("test/operational-execution/sense/" + scope);
        Hash128 input = Hash128.OfCanonical("test/operational-execution/input/" + scope);
        Hash128 answer = Hash128.OfCanonical("test/operational-execution/answer/" + scope);
        Hash128 changedAnswer = Hash128.OfCanonical("test/operational-execution/changed-answer/" + scope);
        Hash128 definition = Hash128.OfCanonical("test/operational-execution/definition/" + scope);
        Hash128 causes = RelationTypeRegistry.Resolve("CAUSES").Id;
        Hash128 defines = RelationTypeRegistry.Resolve("HAS_DEFINITION").Id;
        Hash128 firstPredicate = throughWordNetSense ? defines : causes;
        Hash128 alternatePredicate = throughWordNetSense ? causes : defines;
        Hash128 acceptedType = throughWordNetSense ? EntityTypeRegistry.WordNetSynset : EntityTypeRegistry.CodeConcept;
        Hash128 isSenseOf = RelationTypeRegistry.Resolve("IS_SENSE_OF").Id;
        Hash128 hasParse = RelationTypeRegistry.Resolve("HAS_PARSE").Id;
        Hash128 exampleOf = RelationTypeRegistry.Resolve("IS_EXAMPLE_OF").Id;
        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource, persistEvidence: true);
        var runner = new IngestRunner(writer, new NpgsqlSubstrateReader(pg.DataSource),
            NullLoggerFactory.Instance, new NpgsqlIngestObservability(pg.DataSource, evidencePersisted: true));

        try
        {
            // The fixed authored exemplar is copied byte-for-byte with the
            // same relative source path as the deployed operational bundle.
            // The other case retains the upstream UD source admission contract.
            const string authoredRelative = "seeds/operational/exemplars/en_define.conllu";
            string udPath;
            DateTime? authoredStartedAt = null;
            Hash128 parseSource;
            if (throughWordNetSense)
            {
                string authoredRoot = Path.Combine(directory, "authored");
                string bundled = Path.Combine(OperationalDecomposer.BundledPath, authoredRelative);
                byte[] bundledBytes = await File.ReadAllBytesAsync(bundled);
                Assert.InRange(bundledBytes.Length, 1, 64 * 1024);
                udPath = Path.Combine(authoredRoot, authoredRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(udPath)!);
                await File.WriteAllBytesAsync(udPath, bundledBytes);
                Assert.Equal(bundledBytes, await File.ReadAllBytesAsync(udPath));
                await using (var clock = pg.DataSource.CreateCommand("SELECT clock_timestamp()"))
                    authoredStartedAt = (DateTime)(await clock.ExecuteScalarAsync())!;
                await Ingest(new OperationalDecomposer(), authoredRoot);
                parseSource = OperationalSource.SourceId;
            }
            else
            {
                udPath = Path.Combine(directory, "en_" + scope + "_test.conllu");
                await File.WriteAllTextAsync(udPath,
                    $"# sent_id = {scope}\n# text = {exemplarText}\n"
                    + $"1\t{cue}\t{cue}\tVERB\t_\t_\t0\troot\t_\t_\n"
                    + $"2\t{exampleWord}\t{exampleWord}\tNOUN\t_\t_\t1\tobj\t_\t_\n\n");
                await Ingest(new UDDecomposer(), udPath);
                parseSource = UDSource.SourceId;
            }
            Hash128 exemplarRoot = ContentTierSpine.ResolveRoot(exemplarText)!.Value;
            Hash128 parse;
            await using (var query = pg.DataSource.CreateCommand(
                "SELECT object_id FROM laplace.attestations "
                + "WHERE subject_id=$1 AND type_id=$2 AND source_id=$3 AND outcome=2"))
            {
                query.Parameters.AddWithValue(exemplarRoot.ToBytes());
                query.Parameters.AddWithValue(hasParse.ToBytes());
                query.Parameters.AddWithValue(parseSource.ToBytes());
                await using var rows = await query.ExecuteReaderAsync();
                Assert.True(await rows.ReadAsync(), "Actual source admission must persist its witnessed parse.");
                parse = Hash128.FromBytes(rows.GetFieldValue<byte[]>(0));
                Assert.False(await rows.ReadAsync());
            }

            if (throughWordNetSense)
                await RetainAuthoredExemplar(udPath, authoredRelative, authoredStartedAt!.Value, exemplarRoot, parse);

            var facts = new SubstrateChangeBuilder(source, "operational-execution-facts/" + scope);
            facts.AddEntity(source, EntityTier.Word, EntityTypeRegistry.SourceReference, source);
            facts.AddEntity(context, EntityTier.Word, EntityTypeRegistry.SourceReference, source);
            facts.AddEntity(input, EntityTier.Word, acceptedType, source);
            if (throughWordNetSense)
                facts.AddEntity(sense, EntityTier.Word, EntityTypeRegistry.WordNetSense, source);
            foreach (Hash128 id in new[] { answer, changedAnswer, definition })
                facts.AddEntity(id, EntityTier.Word, EntityTypeRegistry.CodeConcept, source);
            Assert.True(ContentTierSpine.TryStageIntoBuilder(facts, Encoding.UTF8.GetBytes(prompt), source, out Hash128 promptRoot));
            Hash128 inputSurface = ContentTierSpine.ResolveRoot(inputWord)!.Value;
            facts.AddAttestation(NativeAttestation.CategoricalResolved(inputSurface,
                RelationTypeRegistry.Resolve("HAS_SENSE").Id, throughWordNetSense ? sense : input,
                source, context, SourceTrust.SubstrateMandate));
            facts.AddAttestation(Fact(input, firstPredicate, answer));
            facts.AddAttestation(Fact(input, alternatePredicate, definition));
            await Apply(facts.Build());
            await AssertNoCurrentParseOrInvocation(promptRoot);
            Receipt absent = await Forward(prompt, throughWordNetSense);
            Assert.False(absent.Complete);
            Assert.Empty(absent.Emitted);

            string shapePath = Path.Combine(directory, "shape.json");
            var first = await AdmitShape(firstPredicate);
            await AssertPersistedContract(first.Shape, first.File);
            if (throughWordNetSense)
            {
                // A witnessed surface sense and an existing typed synset are
                // insufficient without the source-observed connecting relation.
                Receipt disconnected = await Forward(prompt, throughWordNetSense);
                Assert.False(disconnected.Complete);
                Assert.Empty(disconnected.Emitted);
                await AssertNoCurrentParseOrInvocation(promptRoot);
                await Apply(new SubstrateChangeBuilder(source, "operational-execution-sense-bridge/" + scope)
                    .AddAttestation(Fact(sense, isSenseOf, input)).Build());
            }
            Receipt original = await Forward(prompt, throughWordNetSense);
            Assert.True(original.Complete, original.Disposition);
            Assert.Equal(answer, Assert.Single(original.Emitted));
            Assert.Equal(promptRoot, original.Root);
            Assert.True(original.Required > 0);
            Assert.Equal(original.Required, original.Satisfied);
            Assert.Equal(0, original.Remaining);
            await AssertNoCurrentParseOrInvocation(promptRoot);

            // Withdraw one actual fact, then deposit its replacement through the
            // shared native witness/fold path. The task declaration is unchanged.
            await using (var retract = pg.DataSource.CreateCommand(
                "DELETE FROM laplace.attestations WHERE subject_id=$1 AND type_id=$2 AND object_id=$3 AND source_id=$4"))
            {
                retract.Parameters.AddWithValue(input.ToBytes());
                retract.Parameters.AddWithValue(firstPredicate.ToBytes());
                retract.Parameters.AddWithValue(answer.ToBytes());
                retract.Parameters.AddWithValue(source.ToBytes());
                Assert.Equal(1, await retract.ExecuteNonQueryAsync());
            }
            await using (var retract = pg.DataSource.CreateCommand(
                "DELETE FROM laplace.consensus WHERE subject_id=$1 AND type_id=$2 AND object_id=$3"))
            {
                retract.Parameters.AddWithValue(input.ToBytes());
                retract.Parameters.AddWithValue(firstPredicate.ToBytes());
                retract.Parameters.AddWithValue(answer.ToBytes());
                Assert.Equal(1, await retract.ExecuteNonQueryAsync());
            }
            await Apply(new SubstrateChangeBuilder(source, "operational-execution-replacement/" + scope)
                .AddAttestation(Fact(input, firstPredicate, changedAnswer)).Build());
            Receipt changed = await Forward(prompt, throughWordNetSense);
            Assert.True(changed.Complete, changed.Disposition);
            Assert.Equal(changedAnswer, Assert.Single(changed.Emitted));

            // Editing the file admits a distinct shape and file context. Both
            // witnessed declarations remain active until one is withdrawn.
            var second = await AdmitShape(alternatePredicate);
            Assert.NotEqual(first.Shape.Id, second.Shape.Id);
            Assert.NotEqual(first.File, second.File);
            await AssertPersistedContract(second.Shape, second.File);
            Receipt alternatives = await Forward(prompt, throughWordNetSense);
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
            Receipt replacement = await Forward(prompt, throughWordNetSense);
            Assert.True(replacement.Complete, replacement.Disposition);
            Assert.Equal(definition, Assert.Single(replacement.Emitted));
            Assert.NotEqual(original.ProgramId, replacement.ProgramId);
            await AssertNoCurrentParseOrInvocation(promptRoot);
            string? exemplarReceipt = Environment.GetEnvironmentVariable("LAPLACE_OPERATIONAL_EXEMPLAR_RECEIPT");
            if (throughWordNetSense && !string.IsNullOrWhiteSpace(exemplarReceipt))
            {
                string evidenceDirectory = Path.GetDirectoryName(Path.GetFullPath(exemplarReceipt))!;
                await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "execution.json"),
                    JsonSerializer.Serialize(new
                    {
                        schema = "laplace.operational-source-execution-proof/v1", disposition = "complete",
                        candidate_sha = Environment.GetEnvironmentVariable("LAPLACE_PR_TARGET_SHA"),
                        prompt, root_id = Hex(promptRoot), input_surface_id = Hex(inputSurface),
                        execution_defaults = new { steps = 128, max_stride = 5, spread = 0.6, top_k = 10,
                            hops = 2, fanout = 8, seed_recipe = "hash128_lo(blake3(prompt UTF8))", prior_frontier = "NULL" },
                        sense_id = Hex(sense), synset_id = Hex(input),
                        exemplar_parse_id = Hex(parse), shape_id = Hex(first.Shape.Id),
                        shape_file_id = Hex(first.File), program_id = Hex(original.ProgramId),
                        emitted_id = Hex(Assert.Single(original.Emitted)),
                        changed_fact_emitted_id = Hex(Assert.Single(changed.Emitted)),
                        replacement_shape_id = Hex(second.Shape.Id),
                        replacement_program_id = Hex(replacement.ProgramId),
                        replacement_emitted_id = Hex(Assert.Single(replacement.Emitted)),
                        required_obligations = original.Required, satisfied_obligations = original.Satisfied,
                        remaining_required = original.Remaining,
                        missing_sense_bridge_rejected = true, competing_shapes_ambiguous = true,
                        current_parse_or_invocation_manufactured = false,
                    }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }

            async Task<(OperationalTaskShapeWitness.Definition Shape, Hash128 File)> AdmitShape(Hash128 predicate)
            {
                string json = $$"""
                    {
                      "schema": "laplace/task-shape/relation-read/token-slots/v1",
                      "exemplar_parse_id": "{{Hex(parse)}}",
                      "predicate_id": "{{Hex(predicate)}}",
                      "slots": [{"exemplar_token_ref_id": "{{Hex(UdParseStructure.TokenRefId("2"))}}",
                                 "accepted_entity_type_id": "{{Hex(acceptedType)}}"}]
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

    private async Task RetainAuthoredExemplar(
        string path, string relativePath, DateTime startedAt, Hash128 root, Hash128 parse)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Hash128 fingerprint = IngestBatchPipeline.TryResolveFileIdentity(path)!.Value;
        using var ast = GrammarDecomposer.Parse(bytes, "markdown");
        using var composer = new GrammarRowComposer(bytes, ast, OperationalSource.SourceId,
            "markdown", GrammarCompositionMode.FullSource);
        FileIdentity expectedFile = FileEntity.Resolve(composer.RootComponent(),
            GrammarSourceFileSupport.MetadataFromPath(path, relativePath, "markdown"));
        Guid runId;
        await using (var query = pg.DataSource.CreateCommand(
            "SELECT f.run_id,f.file_id,f.resume_fingerprint,f.bytes,f.status,f.disposition,r.status,r.source_id "
            + "FROM laplace.ingest_file_journal f JOIN laplace.ingest_run_journal r ON r.run_id=f.run_id "
            + "WHERE r.source_name=$1 AND f.relative_path=$2 AND r.started_at >= $3"))
        {
            query.Parameters.AddWithValue(OperationalSource.SourceName);
            query.Parameters.AddWithValue(relativePath);
            query.Parameters.AddWithValue(startedAt);
            await using var row = await query.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync(), "The actual authored-file run must retain its journal receipt.");
            runId = row.GetGuid(0);
            Assert.Equal(expectedFile.FileId.ToBytes(), row.GetFieldValue<byte[]>(1));
            Assert.Equal(fingerprint.ToBytes(), row.GetFieldValue<byte[]>(2));
            Assert.Equal(bytes.LongLength, row.GetInt64(3));
            Assert.Equal("ok", row.GetString(4));
            Assert.Equal("admitted", row.GetString(5));
            Assert.Equal("ok", row.GetString(6));
            Assert.Equal(OperationalSource.SourceId.ToBytes(), row.GetFieldValue<byte[]>(7));
            Assert.False(await row.ReadAsync());
        }
        Hash128 hasParse = RelationTypeRegistry.Resolve("HAS_PARSE").Id;
        Hash128 occurrence;
        await using (var query = pg.DataSource.CreateCommand(
            "SELECT a.context_id,a.outcome,a.observation_count,a.opponent_rating_fp1e9,a.opponent_rd_fp1e9,c.rating "
            + "FROM laplace.attestations a JOIN laplace.consensus c "
            + "ON c.subject_id=a.subject_id AND c.type_id=a.type_id AND c.object_id=a.object_id "
            + "WHERE a.subject_id=$1 AND a.type_id=$2 AND a.object_id=$3 AND a.source_id=$4"))
        {
            query.Parameters.AddWithValue(root.ToBytes());
            query.Parameters.AddWithValue(hasParse.ToBytes());
            query.Parameters.AddWithValue(parse.ToBytes());
            query.Parameters.AddWithValue(OperationalSource.SourceId.ToBytes());
            await using var row = await query.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            occurrence = Hash128.FromBytes(row.GetFieldValue<byte[]>(0));
            Assert.NotEqual(expectedFile.FileId, occurrence);
            AttestationRow expected = NativeAttestation.CategoricalResolved(root, hasParse, parse,
                OperationalSource.SourceId, occurrence, SourceTrust.SubstrateMandate);
            Assert.Equal(2, row.GetInt16(1));
            Assert.Equal(1, row.GetInt64(2));
            Assert.Equal(expected.OpponentRatingFp1e9, row.GetInt64(3));
            Assert.Equal(expected.OpponentRdFp1e9, row.GetInt64(4));
            Assert.True(row.GetInt64(5) > Glicko2.DefaultRatingFp1e9);
            Assert.False(await row.ReadAsync());
        }
        await using (var query = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.attestations WHERE subject_id=$1 AND type_id=$2 AND object_id=$3 "
            + "AND source_id=$4 AND context_id=$1 AND outcome=2"))
        {
            query.Parameters.AddWithValue(expectedFile.FileId.ToBytes());
            query.Parameters.AddWithValue(RelationTypeRegistry.Resolve("CONTAINS").Id.ToBytes());
            query.Parameters.AddWithValue(occurrence.ToBytes());
            query.Parameters.AddWithValue(OperationalSource.SourceId.ToBytes());
            Assert.Equal(1L, (long)(await query.ExecuteScalarAsync())!);
        }
        await using (var query = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.attestations WHERE subject_id=$1 AND type_id=$2 AND source_id=$3"))
        {
            query.Parameters.AddWithValue(root.ToBytes());
            query.Parameters.AddWithValue(hasParse.ToBytes());
            query.Parameters.AddWithValue(UDSource.SourceId.ToBytes());
            Assert.Equal(0L, (long)(await query.ExecuteScalarAsync())!);
        }
        await using (var query = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.attestations WHERE subject_id=$1 AND object_id=$1 AND source_id=$1 "
            + "AND type_id=$2 AND context_id=$3"))
        {
            query.Parameters.AddWithValue(fingerprint.ToBytes());
            query.Parameters.AddWithValue(LayerCompletion.RelationTypeId(2).ToBytes());
            query.Parameters.AddWithValue(OperationalSource.SourceId.ToBytes());
            Assert.Equal(1L, (long)(await query.ExecuteScalarAsync())!);
        }
        string? receiptPath = Environment.GetEnvironmentVariable("LAPLACE_OPERATIONAL_EXEMPLAR_RECEIPT");
        if (!string.IsNullOrWhiteSpace(receiptPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(receiptPath))!);
            string json = JsonSerializer.Serialize(new
            {
                schema = "laplace.operational-exemplar-proof/v1", disposition = "source-admitted",
                candidate_sha = Environment.GetEnvironmentVariable("LAPLACE_PR_TARGET_SHA"),
                run_id = runId, relative_path = relativePath, source_id = Hex(OperationalSource.SourceId),
                trust_class = Hex(OperationalSource.TrustClass), trust = "SubstrateMandate",
                bytes = bytes.Length, bytes_base64 = Convert.ToBase64String(bytes),
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                file_id = Hex(expectedFile.FileId), occurrence_context_id = Hex(occurrence),
                resume_fingerprint = Hex(fingerprint),
                source_root_id = Hex(root), exemplar_parse_id = Hex(parse),
                exemplar_token_ref_id = Hex(UdParseStructure.TokenRefId("2")),
                accepted_entity_type_id = Hex(EntityTypeRegistry.WordNetSynset),
                predicate_id = Hex(RelationTypeRegistry.Resolve("HAS_DEFINITION").Id),
                file_occurrence_context_verified = true, source_trust_verified = true, completion_present = true,
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(receiptPath, json + "\n");
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

    private sealed record Receipt(Hash128[] Emitted, bool Complete, string Disposition, Hash128 ProgramId,
        Hash128 Root, int Required, int Satisfied, int Remaining);

    private async Task<Receipt> Forward(string prompt, bool ordinaryDefaults)
    {
        await using var command = pg.DataSource.CreateCommand(
            "SELECT event,entity,completion,disposition,program_id,root_id,required_obligations,satisfied_obligations,remaining_required "
            + (ordinaryDefaults
                ? "FROM generation.forward_program($1,128,5,0.6,10,"
                    + "laplace.hash128_lo(public.laplace_hash128_blake3(convert_to($1,'UTF8'))),2,8,NULL,NULL) ORDER BY step"
                : "FROM generation.forward_program($1,2,0,0.0,16,7,2,256,NULL,NULL) ORDER BY step"));
        command.Parameters.AddWithValue(prompt);
        await using var rows = await command.ExecuteReaderAsync();
        var emitted = new List<Hash128>();
        bool complete = false;
        string disposition = "no receipt";
        Hash128 program = default;
        Hash128 root = default;
        int required = 0, satisfied = 0, remaining = 0;
        while (await rows.ReadAsync())
        {
            string kind = rows.GetString(0);
            if (kind == "emit") emitted.Add(Hash128.FromBytes(rows.GetFieldValue<byte[]>(1)));
            if (kind is "complete" or "unresolved")
            {
                complete = rows.GetBoolean(2);
                disposition = rows.GetString(3);
                program = Hash128.FromBytes(rows.GetFieldValue<byte[]>(4));
                root = Hash128.FromBytes(rows.GetFieldValue<byte[]>(5));
                required = rows.GetInt32(6);
                satisfied = rows.GetInt32(7);
                remaining = rows.GetInt32(8);
            }
        }
        Assert.NotEqual(default, program);
        return new Receipt(emitted.ToArray(), complete, disposition, program, root, required, satisfied, remaining);
    }

    private static string Hex(Hash128 id) => Convert.ToHexString(id.ToBytes()).ToLowerInvariant();
}
