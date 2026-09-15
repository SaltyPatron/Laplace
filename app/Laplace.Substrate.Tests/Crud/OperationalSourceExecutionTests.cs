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

    [Fact]
    public async Task AuthoredAntonymExemplar_AdmitsCompleteSourceWithNativeParseProvenance()
    {
        CodepointPerfcache.LoadDefault();
        LanguageReference.EnsureLoaded();
        const string relative = "seeds/operational/exemplars/en_antonym.conllu";
        const string text = "The opposite of empty is";
        string directory = Path.Combine(Path.GetTempPath(), "laplace-antonym-exemplar-" + Guid.NewGuid().ToString("N"));
        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource, persistEvidence: true);
        var runner = new IngestRunner(writer, new NpgsqlSubstrateReader(pg.DataSource),
            NullLoggerFactory.Instance, new NpgsqlIngestObservability(pg.DataSource, evidencePersisted: true));
        try
        {
            await CopyBundledSource(directory);
            DateTime startedAt;
            await using (var clock = pg.DataSource.CreateCommand("SELECT clock_timestamp()"))
                startedAt = (DateTime)(await clock.ExecuteScalarAsync())!;
            await IngestSource(runner, new OperationalDecomposer(), directory, 15, reobservePresent: true);
            Hash128 root = ContentTierSpine.ResolveRoot(text)!.Value;
            Hash128 parse;
            await using (var query = pg.DataSource.CreateCommand(
                "SELECT object_id FROM laplace.attestations "
                + "WHERE subject_id=$1 AND type_id=$2 AND source_id=$3 AND outcome=2"))
            {
                query.Parameters.AddWithValue(root.ToBytes());
                query.Parameters.AddWithValue(RelationTypeRegistry.Resolve("HAS_PARSE").Id.ToBytes());
                query.Parameters.AddWithValue(OperationalSource.SourceId.ToBytes());
                await using var row = await query.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync(), "The ordinary source admission must persist the authored fragment's parse.");
                parse = Hash128.FromBytes(row.GetFieldValue<byte[]>(0));
                Assert.False(await row.ReadAsync());
            }
            AuthoredExemplarEvidence evidence = await ReadAuthoredExemplar(
                Path.Combine(directory, relative), relative, startedAt, root, parse);
            await AssertFullBundleReceipt(directory, evidence.RunId, retainReceipt: false);
            Hash128 physicalityId;
            int count;
            await using (var query = pg.DataSource.CreateCommand(
                "SELECT id,n_constituents FROM laplace.physicalities WHERE entity_id=$1 AND type=8"))
            {
                query.Parameters.AddWithValue(parse.ToBytes());
                await using var row = await query.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync());
                physicalityId = Hash128.FromBytes(row.GetFieldValue<byte[]>(0));
                Assert.Equal(PhysicalityId.Compute(parse, PhysicalityType.ParseStructure), physicalityId);
                count = row.GetInt32(1);
                Assert.False(await row.ReadAsync());
            }
            var flat = new List<Hash128>();
            await using (var query = pg.DataSource.CreateCommand(
                "SELECT entity_id FROM generation.trajectory_unpacked_points($1,8::smallint)"))
            {
                query.Parameters.AddWithValue(parse.ToBytes());
                await using var row = await query.ExecuteReaderAsync();
                while (await row.ReadAsync()) flat.Add(Hash128.FromBytes(row.GetFieldValue<byte[]>(0)));
            }
            Assert.Equal(count, flat.Count);
            Assert.Equal(parse, Hash128.Merkle(EntityTier.Document, flat.ToArray()));
            // Compare complete stored structure and occurrence to the same
            // unchanged source's normal compositor output, including every
            // feature value. This expected change is never written to the DB.
            var record = await OperationalDecomposer.ReadContractAsync(Path.Combine(directory, relative), relative);
            var handler = new GrammarComposeHandler(OperationalSource.SourceId, SourceTrust.SubstrateMandate, null);
            using (var unit = handler.CreateDeferredUnit(record))
            {
                var builder = new SubstrateChangeBuilder(OperationalSource.SourceId, "antonym-source-readback");
                Hash128 file = unit.DrainInto(builder, SourceTrust.SubstrateMandate, null);
                handler.WalkWitness(record, file, builder, unit);
                SubstrateChange expected = builder.Build();
                try
                {
                    Assert.Equal(evidence.FileId, file);
                    AttestationRow claim = Assert.Single(expected.Attestations, a => a.TypeId == RelationTypeRegistry.Resolve("HAS_PARSE").Id);
                    Assert.Equal(root, claim.SubjectId);
                    Assert.Equal(parse, claim.ObjectId);
                    Assert.Equal(evidence.Occurrence, claim.ContextId);
                    PhysicalityRow structure = Assert.Single(expected.Physicalities, p => p.Id == physicalityId);
                    Assert.Equal(Trajectory.Constituents(structure.TrajectoryXyzm!), flat);
                }
                finally { foreach (var stage in expected.IntentStages) stage.Dispose(); }
            }
            Assert.True(UdParseStructure.TryDecode(flat.ToArray(), out var parsed));
            Assert.NotNull(parsed);
            Assert.Equal(root, parsed.SentenceId);
            Assert.Equal(LanguageReference.Resolve("en"), parsed.LanguageId);
            Assert.Empty(parsed.Mwts);
            Assert.Equal(5, parsed.Tokens.Count);
            string[] forms = ["The", "opposite", "of", "empty", "is"];
            string[] lemmas = ["the", "opposite", "of", "empty", "be"];
            string[] upos = ["DET", "NOUN", "ADP", "ADJ", "AUX"];
            string[] deprels = ["det", "nsubj", "case", "nmod", "root"];
            int[] heads = [2, 5, 4, 2, 0];
            int[] featureCounts = [2, 1, 0, 1, 5];
            for (int i = 0; i < parsed.Tokens.Count; i++)
            {
                var token = parsed.Tokens[i];
                Assert.Equal(UdParseStructure.TokenRefId((i + 1).ToString()), token.RefId);
                Assert.Equal(ContentTierSpine.ResolveRoot(forms[i]), token.FormId);
                Assert.Equal(ContentTierSpine.ResolveRoot(lemmas[i]), token.LemmaId);
                Assert.Equal(PosReference.Resolve(upos[i], PosReference.PosTagset.Upos), token.UposId);
                Assert.Equal(UdParseStructure.NoneId, token.XposId);
                Assert.Equal(heads[i] == 0 ? UdParseStructure.RootId : UdParseStructure.TokenRefId(heads[i].ToString()), token.HeadRefId);
                Assert.Equal(RelationTypeRegistry.ResolveDeprel(deprels[i]).Id, token.DeprelId);
                Assert.Equal(featureCounts[i], token.Features.Count);
                Assert.Empty(token.Enhanced);
                Assert.Empty(token.Misc);
            }
            Assert.Single(parsed.Tokens, t => t.HeadRefId == UdParseStructure.RootId);
            Hash128 prospectiveToken = parsed.Tokens[3].RefId;
            Assert.Equal(UdParseStructure.TokenRefId("4"), prospectiveToken);
            var task = await ReadBundledAntonymTask(directory);
            Assert.Equal(parse, task.Shape.ExemplarParseId);
            Assert.Equal(prospectiveToken, Assert.Single(task.Shape.Slots).TokenRefId);
            await AssertPersistedContract(task.Shape, task.File);
            string? receiptPath = Environment.GetEnvironmentVariable("LAPLACE_OPERATIONAL_EXEMPLAR_RECEIPT");
            if (!string.IsNullOrWhiteSpace(receiptPath))
            {
                string receiptDirectory = Path.GetDirectoryName(Path.GetFullPath(receiptPath))!;
                Directory.CreateDirectory(receiptDirectory);
                byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = "laplace.operational-antonym-exemplar-proof/v1", disposition = "source-admitted",
                    candidate_sha = Environment.GetEnvironmentVariable("LAPLACE_PR_TARGET_SHA"),
                    run_id = evidence.RunId, relative_path = relative, source_id = Hex(OperationalSource.SourceId),
                    trust_class = Hex(OperationalSource.TrustClass), trust = "SubstrateMandate",
                    utf8_base64 = Convert.ToBase64String(evidence.Bytes),
                    bytes = evidence.Bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(evidence.Bytes)).ToLowerInvariant(),
                    file_id = Hex(evidence.FileId), occurrence_context_id = Hex(evidence.Occurrence),
                    resume_fingerprint = Hex(evidence.Fingerprint), source_root_id = Hex(root),
                    exemplar_parse_id = Hex(parse), parse_physicality_id = Hex(physicalityId),
                    language_id = Hex(parsed.LanguageId), prospective_variable_ordinal = 4,
                    exemplar_token_ref_id = Hex(prospectiveToken), constituents = flat.Select(Hex).ToArray(),
                    tokens = parsed.Tokens.Select((token, i) => new
                    {
                        ordinal = i + 1, token_ref_id = Hex(token.RefId), form_id = Hex(token.FormId),
                        lemma_id = Hex(token.LemmaId), upos_id = Hex(token.UposId), xpos_id = Hex(token.XposId),
                        head_ref_id = Hex(token.HeadRefId), deprel_id = Hex(token.DeprelId),
                        features = token.Features.Select(pair => new { relation_id = Hex(pair.RelationId), value_id = Hex(pair.ValueId) }),
                        enhanced = token.Enhanced.Select(pair => new { head_ref_id = Hex(pair.HeadRefId), relation_id = Hex(pair.RelationId) }),
                        misc = token.Misc.Select(pair => new { key_id = Hex(pair.KeyId), value_id = Hex(pair.ValueId) }),
                    }),
                    canonical_parse_verified = true, file_occurrence_context_verified = true,
                    source_trust_verified = true, completion_present = true, task_declaration_admitted = true,
                    shape_id = Hex(task.Shape.Id), shape_file_id = Hex(task.File),
                    shape_relative_path = task.RelativePath, predicate_id = Hex(task.Shape.PredicateId),
                    accepted_entity_type_id = Hex(Assert.Single(task.Shape.Slots).AcceptedTypeId),
                    binding_mode_id = Hex(Assert.Single(task.Shape.Slots).BindingModeId),
                    selected_files = 15, admitted_file_journals = 15, completion_markers = 15,
                    parse_observation_count = 1, parse_consensus_witness_count = 1,
                }, new JsonSerializerOptions { WriteIndented = true });
                Assert.InRange(receipt.Length, 1, 64 * 1024);
                await File.WriteAllBytesAsync(Path.Combine(receiptDirectory, "antonym-exemplar.json"), receipt);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task AuthoredAntonymTask_ExecutesNovelRequestThroughAdmittedWordBinding()
    {
        object[] executions =
        [
            await AssertAntonymExecution(inputIsStoredSubject: true),
            await AssertAntonymExecution(inputIsStoredSubject: false),
        ];
        string? receiptPath = Environment.GetEnvironmentVariable("LAPLACE_OPERATIONAL_EXEMPLAR_RECEIPT");
        if (!string.IsNullOrWhiteSpace(receiptPath))
        {
            string receiptDirectory = Path.GetDirectoryName(Path.GetFullPath(receiptPath))!;
            Directory.CreateDirectory(receiptDirectory);
            byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "laplace.operational-antonym-execution-proof/v2", disposition = "complete",
                candidate_sha = Environment.GetEnvironmentVariable("LAPLACE_PR_TARGET_SHA"),
                storage_orientations_verified = new[] { "input-subject", "input-object" }, executions,
            }, new JsonSerializerOptions { WriteIndented = true });
            Assert.InRange(receipt.Length, 1, 64 * 1024);
            await File.WriteAllBytesAsync(Path.Combine(receiptDirectory, "antonym-execution.json"), receipt);
        }
    }

    private async Task<object> AssertAntonymExecution(bool inputIsStoredSubject)
    {
        CodepointPerfcache.LoadDefault();
        LanguageReference.EnsureLoaded();
        var fixture = SelectAntonymFixture(inputIsStoredSubject);
        string scope = fixture.Scope;
        string directory = Path.Combine(Path.GetTempPath(), "laplace-antonym-execution-" + scope);
        string operand = fixture.Operand;
        string prompt = "The opposite of " + operand + " is";
        Hash128 source = fixture.Source;
        Hash128 context = fixture.Context;
        Hash128 answer = fixture.Answer;
        Hash128 changedAnswer = Hash128.OfCanonical("test/antonym-execution/changed-answer/" + scope);
        Hash128 alternativeAnswerA = Hash128.OfCanonical("test/antonym-execution/alternative-answer-a/" + scope);
        Hash128 alternativeAnswerB = Hash128.OfCanonical("test/antonym-execution/alternative-answer-b/" + scope);
        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource, persistEvidence: true);
        var runner = new IngestRunner(writer, new NpgsqlSubstrateReader(pg.DataSource),
            NullLoggerFactory.Instance, new NpgsqlIngestObservability(pg.DataSource, evidencePersisted: true));
        try
        {
            await CopyBundledSource(directory);
            DateTime startedAt;
            await using (var clock = pg.DataSource.CreateCommand("SELECT clock_timestamp()"))
                startedAt = (DateTime)(await clock.ExecuteScalarAsync())!;
            await IngestSource(runner, new OperationalDecomposer(), directory, 15, reobservePresent: true);
            var task = await ReadBundledAntonymTask(directory);
            Hash128 exemplarRoot = ContentTierSpine.ResolveRoot("The opposite of empty is")!.Value;
            const string exemplarRelative = "seeds/operational/exemplars/en_antonym.conllu";
            var exemplar = await ReadAuthoredExemplar(Path.Combine(directory, exemplarRelative),
                exemplarRelative, startedAt, exemplarRoot, task.Shape.ExemplarParseId);
            await AssertFullBundleReceipt(directory, exemplar.RunId, retainReceipt: false);
            await AssertPersistedContract(task.Shape, task.File);

            // Native text admission supplies the whole Word entity directly.
            // These are new fixture facts, not observations attributed to WordNet.
            var facts = new SubstrateChangeBuilder(source, "antonym-execution-content/" + scope);
            facts.AddEntity(source, EntityTier.Word, EntityTypeRegistry.SourceReference, source);
            facts.AddEntity(context, EntityTier.Word, EntityTypeRegistry.SourceReference, source);
            facts.AddEntity(answer, EntityTier.Word, EntityTypeRegistry.WordNetSynset, source);
            facts.AddEntity(changedAnswer, EntityTier.Word, EntityTypeRegistry.WordNetSynset, source);
            facts.AddEntity(alternativeAnswerA, EntityTier.Word, EntityTypeRegistry.WordNetSynset, source);
            facts.AddEntity(alternativeAnswerB, EntityTier.Word, EntityTypeRegistry.WordNetSynset, source);
            Assert.True(ContentTierSpine.TryStageIntoBuilder(facts, Encoding.UTF8.GetBytes(prompt), source, out Hash128 promptRoot));
            Hash128 input = fixture.Input;
            Assert.NotEqual(ContentTierSpine.ResolveRoot("empty"), input);
            Assert.True(ContentTierSpine.TryStageIntoBuilder(facts, Encoding.UTF8.GetBytes("próbulo" + scope), source, out Hash128 alternativeA));
            Assert.True(ContentTierSpine.TryStageIntoBuilder(facts, Encoding.UTF8.GetBytes("plúvulo" + scope), source, out Hash128 alternativeB));
            Hash128 isLemma = RelationTypeRegistry.Resolve("IS_LEMMA_OF").Id;
            AttestationRow[] alternatives =
            [
                NativeAttestation.CategoricalResolved(input, isLemma, alternativeA, source, context, SourceTrust.SubstrateMandate),
                NativeAttestation.CategoricalResolved(input, isLemma, alternativeB, source, context, SourceTrust.SubstrateMandate),
                NativeAttestation.CategoricalResolved(alternativeA, task.Shape.PredicateId, alternativeAnswerA, source, context, SourceTrust.SubstrateMandate),
                NativeAttestation.CategoricalResolved(alternativeB, task.Shape.PredicateId, alternativeAnswerB, source, context, SourceTrust.SubstrateMandate),
            ];
            foreach (AttestationRow witness in alternatives) facts.AddAttestation(witness);
            await Apply(facts.Build());
            await using (var query = pg.DataSource.CreateCommand("SELECT id,type_id,first_observed_by FROM laplace.entities WHERE id=ANY($1)"))
            {
                query.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                    new[] { input.ToBytes(), alternativeA.ToBytes(), alternativeB.ToBytes() });
                await using var row = await query.ExecuteReaderAsync();
                var seen = new HashSet<Hash128>();
                while (await row.ReadAsync())
                {
                    Assert.True(seen.Add(Hash128.FromBytes(row.GetFieldValue<byte[]>(0))));
                    Assert.Equal(Assert.Single(task.Shape.Slots).AcceptedTypeId.ToBytes(), row.GetFieldValue<byte[]>(1));
                    Assert.Equal(source.ToBytes(), row.GetFieldValue<byte[]>(2));
                }
                Assert.Equal(3, seen.Count);
            }
            foreach (AttestationRow witness in alternatives) await AssertWitness(witness);
            await AssertNoCurrentParseOrInvocation(promptRoot);
            // Both named Word alternatives have witnessed results already.
            // CURRENT_FORM must still leave the absent original fact unresolved.
            Receipt absent = await Forward(prompt, ordinaryDefaults: true);
            Assert.False(absent.Complete);
            Assert.Empty(absent.Emitted);

            AttestationRow originalWitness = await AdmitFact(answer, "antonym-execution-fact/" + scope);
            Assert.Equal(inputIsStoredSubject, originalWitness.SubjectId == input);
            Assert.Equal(!inputIsStoredSubject, originalWitness.ObjectId == input);
            Receipt original = await Forward(prompt, ordinaryDefaults: true);
            AssertComplete(original, answer);
            await AssertNoCurrentParseOrInvocation(promptRoot);

            // The native writer orients symmetric relations before persistence.
            // Withdraw that exact witnessed row and its oriented consensus cell,
            // independently of which endpoint the prompt names as its input.
            await using (var retract = pg.DataSource.CreateCommand(
                "DELETE FROM laplace.attestations WHERE id=$1 AND source_id=$2 AND context_id=$3"))
            {
                retract.Parameters.AddWithValue(originalWitness.Id.ToBytes());
                retract.Parameters.AddWithValue(originalWitness.SourceId.ToBytes());
                retract.Parameters.AddWithValue(originalWitness.ContextId!.Value.ToBytes());
                Assert.Equal(1, await retract.ExecuteNonQueryAsync());
            }
            await using (var retract = pg.DataSource.CreateCommand(
                "DELETE FROM laplace.consensus WHERE subject_id=$1 AND type_id=$2 AND object_id=$3"))
            {
                retract.Parameters.AddWithValue(originalWitness.SubjectId.ToBytes());
                retract.Parameters.AddWithValue(originalWitness.TypeId.ToBytes());
                retract.Parameters.AddWithValue(originalWitness.ObjectId!.Value.ToBytes());
                Assert.Equal(1, await retract.ExecuteNonQueryAsync());
            }
            AttestationRow changedWitness = await AdmitFact(changedAnswer, "antonym-execution-changed-fact/" + scope);
            Receipt changed = await Forward(prompt, ordinaryDefaults: true);
            AssertComplete(changed, changedAnswer);
            Assert.Equal(task.Bytes, await File.ReadAllBytesAsync(Path.Combine(directory, task.RelativePath)));
            Assert.DoesNotContain(input, task.Shape.Constituents);
            Assert.DoesNotContain(answer, task.Shape.Constituents);
            Assert.DoesNotContain(changedAnswer, task.Shape.Constituents);
            await AssertPersistedContract(task.Shape, task.File);
            await AssertNoCurrentParseOrInvocation(promptRoot);
            foreach (AttestationRow witness in alternatives) await AssertWitness(witness);

            return new
            {
                schema = "laplace.operational-antonym-execution-proof/v1", disposition = "complete",
                candidate_sha = Environment.GetEnvironmentVariable("LAPLACE_PR_TARGET_SHA"), run_id = exemplar.RunId,
                prompt, root_id = Hex(promptRoot), input_surface_id = Hex(input), input_id = Hex(input),
                accepted_entity_type_id = Hex(Assert.Single(task.Shape.Slots).AcceptedTypeId),
                binding_mode_id = Hex(Assert.Single(task.Shape.Slots).BindingModeId),
                predicate_id = Hex(task.Shape.PredicateId), fact_source_id = Hex(source), fact_context_id = Hex(context),
                fact_witness_id = Hex(originalWitness.Id), changed_fact_witness_id = Hex(changedWitness.Id),
                stored_subject_id = Hex(originalWitness.SubjectId), stored_object_id = Hex(originalWitness.ObjectId!.Value),
                input_storage_endpoint = inputIsStoredSubject ? "subject" : "object",
                retained_word_lemma_alternative_ids = new[] { Hex(alternativeA), Hex(alternativeB) },
                retained_alternative_witness_ids = alternatives.Select(witness => Hex(witness.Id)).ToArray(),
                exemplar_parse_id = Hex(task.Shape.ExemplarParseId), shape_id = Hex(task.Shape.Id),
                shape_file_id = Hex(task.File), bundled_shape_relative_path = task.RelativePath,
                bundled_shape_bytes = task.Bytes.Length,
                bundled_shape_sha256 = Convert.ToHexString(SHA256.HashData(task.Bytes)).ToLowerInvariant(),
                program_id = Hex(original.ProgramId), changed_fact_program_id = Hex(changed.ProgramId),
                emitted_id = Hex(Assert.Single(original.Emitted)), changed_fact_emitted_id = Hex(Assert.Single(changed.Emitted)),
                required_obligations = original.Required, satisfied_obligations = original.Satisfied,
                remaining_required = original.Remaining,
                execution_defaults = new { steps = 128, max_stride = 5, spread = 0.6, top_k = 10,
                    hops = 2, fanout = 8, seed_recipe = "hash128_lo(blake3(prompt UTF8))", prior_frontier = "NULL" },
                direct_word_binding_verified = true, missing_fact_rejected = true, changed_fact_read = true,
                competing_word_lemma_bindings_retained = true,
                task_bytes_unchanged = true, current_parse_or_invocation_manufactured = false,
                selected_files = 15, admitted_file_journals = 15, completion_markers = 15,
            };

            AttestationRow Fact(Hash128 result) => NativeAttestation.CategoricalResolved(input,
                task.Shape.PredicateId, result, source, context, SourceTrust.SubstrateMandate);

            async Task<AttestationRow> AdmitFact(Hash128 result, string label)
            {
                AttestationRow witness = Fact(result);
                await Apply(new SubstrateChangeBuilder(source, label).AddAttestation(witness).Build());
                await AssertWitness(witness);
                return witness;
            }

            async Task<Hash128> AssertWitness(AttestationRow witness)
            {
                await using var query = pg.DataSource.CreateCommand(
                    "SELECT a.id,a.outcome,a.observation_count,a.opponent_rating_fp1e9,a.opponent_rd_fp1e9,c.rating,c.witness_count "
                    + "FROM laplace.attestations a JOIN laplace.consensus c "
                    + "ON c.subject_id=a.subject_id AND c.type_id=a.type_id AND c.object_id=a.object_id "
                    + "WHERE a.subject_id=$1 AND a.type_id=$2 AND a.object_id=$3 AND a.source_id=$4 AND a.context_id=$5");
                query.Parameters.AddWithValue(witness.SubjectId.ToBytes());
                query.Parameters.AddWithValue(witness.TypeId.ToBytes());
                query.Parameters.AddWithValue(witness.ObjectId!.Value.ToBytes());
                query.Parameters.AddWithValue(witness.SourceId.ToBytes());
                query.Parameters.AddWithValue(witness.ContextId!.Value.ToBytes());
                await using var row = await query.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync());
                Hash128 actual = Hash128.FromBytes(row.GetFieldValue<byte[]>(0));
                Assert.Equal(witness.Id, actual);
                Assert.Equal(2, row.GetInt16(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal(witness.OpponentRatingFp1e9, row.GetInt64(3));
                Assert.Equal(witness.OpponentRdFp1e9, row.GetInt64(4));
                Assert.True(row.GetInt64(5) > Glicko2.DefaultRatingFp1e9);
                Assert.Equal(1, row.GetInt64(6));
                Assert.False(await row.ReadAsync());
                return actual;
            }

            void AssertComplete(Receipt receipt, Hash128 result)
            {
                Assert.True(receipt.Complete, receipt.Disposition);
                Assert.Equal(result, Assert.Single(receipt.Emitted));
                Assert.Equal(promptRoot, receipt.Root);
                Assert.True(receipt.Required > 0);
                Assert.Equal(receipt.Required, receipt.Satisfied);
                Assert.Equal(0, receipt.Remaining);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

        async Task Apply(SubstrateChange change)
        {
            try { await writer.ApplyAsync(change); }
            finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
        }
    }

    private sealed record AntonymFixture(string Scope, string Operand, Hash128 Input,
        Hash128 Source, Hash128 Context, Hash128 Answer);

    private static AntonymFixture SelectAntonymFixture(bool inputIsStoredSubject)
    {
        Hash128 predicate = RelationTypeRegistry.Resolve("IS_ANTONYM_OF").Id;
        // Ask the native orientation law to classify fresh fixture candidates.
        // Both cases must run; random identity ordering must not choose coverage.
        for (int attempt = 0; attempt < 128; attempt++)
        {
            string scope = Guid.NewGuid().ToString("N");
            string operand = "ñébulo" + scope;
            Hash128 input = ContentTierSpine.ResolveRoot(operand)!.Value;
            Hash128 source = Hash128.OfCanonical("test/antonym-execution/source/" + scope);
            Hash128 context = Hash128.OfCanonical("test/antonym-execution/context/" + scope);
            Hash128 answer = Hash128.OfCanonical("test/antonym-execution/answer/" + scope);
            if (input == answer) continue;
            AttestationRow witness = NativeAttestation.CategoricalResolved(input, predicate,
                answer, source, context, SourceTrust.SubstrateMandate);
            if ((witness.SubjectId == input) == inputIsStoredSubject)
                return new AntonymFixture(scope, operand, input, source, context, answer);
        }
        throw new InvalidOperationException("Native fixture selection did not produce the required symmetric storage orientation.");
    }

    private static async Task<(OperationalTaskShapeWitness.Definition Shape, Hash128 File,
        string RelativePath, byte[] Bytes)> ReadBundledAntonymTask(string root)
    {
        const string relative = "seeds/operational/tasks/en_antonym.json";
        string path = Path.Combine(root, relative);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.InRange(bytes.Length, 1, 64 * 1024);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(OperationalDecomposer.BundledPath, relative)), bytes);
        using var ast = GrammarDecomposer.Parse(bytes, "json");
        var declared = OperationalTaskShapeWitness.Read(ast, bytes);
        Assert.Equal(OperationalTaskShapeWitness.SchemaV2, declared.Schema);
        Assert.Equal(RelationTypeRegistry.Resolve("IS_ANTONYM_OF").Id, declared.PredicateId);
        var slot = Assert.Single(declared.Slots);
        Assert.Equal(UdParseStructure.TokenRefId("4"), slot.TokenRefId);
        Assert.Equal(EntityTypeRegistry.Word, slot.AcceptedTypeId);
        Assert.Equal(OperationalTaskShapeWitness.CurrentFormBindingId, slot.BindingModeId);
        using var composer = new GrammarRowComposer(bytes, ast, OperationalSource.SourceId,
            "json", GrammarCompositionMode.FullSource);
        FileIdentity file = FileEntity.Resolve(composer.RootComponent(),
            GrammarSourceFileSupport.MetadataFromPath(path, relative, "json"));
        return (declared, file.FileId, relative, bytes);
    }

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
            string authoredRoot = Path.Combine(directory, "authored");
            DateTime? authoredStartedAt = null;
            Hash128 parseSource;
            if (throughWordNetSense)
            {
                await CopyBundledSource(authoredRoot);
                udPath = Path.Combine(authoredRoot, authoredRelative);
                await using (var clock = pg.DataSource.CreateCommand("SELECT clock_timestamp()"))
                    authoredStartedAt = (DateTime)(await clock.ExecuteScalarAsync())!;
                // One real generic source run owns the entire distributed bundle;
                // parse/shape admission ordering is not arranged by the fixture.
                await Ingest(new OperationalDecomposer(), authoredRoot, expectedFiles: 15, reobservePresent: true);
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
            {
                Guid authoredRun = await RetainAuthoredExemplar(udPath, authoredRelative,
                    authoredStartedAt!.Value, exemplarRoot, parse);
                await AssertFullBundleReceipt(authoredRoot, authoredRun);
            }

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
                        bundled_shape_relative_path = first.RelativePath,
                        bundled_shape_bytes = first.Bytes.Length,
                        bundled_shape_sha256 = Convert.ToHexString(SHA256.HashData(first.Bytes)).ToLowerInvariant(),
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

            async Task<(OperationalTaskShapeWitness.Definition Shape, Hash128 File,
                string RelativePath, byte[] Bytes)> AdmitShape(Hash128 predicate)
            {
                string relativePath = "shape.json";
                string path = shapePath;
                string ecosystem = path;
                byte[] bytes;
                if (throughWordNetSense && predicate.Equals(firstPredicate))
                {
                    // Execute the exact distributed declaration. Its native
                    // reference identities must match this actual source admission.
                    relativePath = "seeds/operational/tasks/en_define.json";
                    bytes = await File.ReadAllBytesAsync(Path.Combine(OperationalDecomposer.BundledPath, relativePath));
                    Assert.InRange(bytes.Length, 1, 64 * 1024);
                    path = Path.Combine(authoredRoot, relativePath);
                    Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
                }
                else
                {
                    // The alternate declaration is an explicit source mutation,
                    // independently admitted with its own file identity.
                    string json = $$"""
                        {
                          "schema": "laplace/task-shape/relation-read/token-slots/v1",
                          "exemplar_parse_id": "{{Hex(parse)}}",
                          "predicate_id": "{{Hex(predicate)}}",
                          "slots": [{"exemplar_token_ref_id": "{{Hex(UdParseStructure.TokenRefId("2"))}}",
                                     "accepted_entity_type_id": "{{Hex(acceptedType)}}"}]
                        }
                        """;
                    bytes = Encoding.UTF8.GetBytes(json);
                }
                if (!(throughWordNetSense && predicate.Equals(firstPredicate)))
                {
                    await File.WriteAllBytesAsync(path, bytes);
                    await Ingest(new OperationalDecomposer(), ecosystem);
                }
                using var ast = GrammarDecomposer.Parse(bytes, "json");
                var declared = OperationalTaskShapeWitness.Read(ast, bytes);
                Assert.Equal(parse, declared.ExemplarParseId);
                Assert.Equal(predicate, declared.PredicateId);
                Assert.Equal(UdParseStructure.TokenRefId("2"), Assert.Single(declared.Slots).TokenRefId);
                Assert.Equal(acceptedType, Assert.Single(declared.Slots).AcceptedTypeId);
                using var composer = new GrammarRowComposer(bytes, ast, OperationalSource.SourceId,
                    "json", GrammarCompositionMode.FullSource);
                FileIdentity file = FileEntity.Resolve(composer.RootComponent(),
                    GrammarSourceFileSupport.MetadataFromPath(path, relativePath, "json"));
                return (declared, file.FileId, relativePath, bytes);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }

        AttestationRow Fact(Hash128 subject, Hash128 predicate, Hash128 result) =>
            NativeAttestation.CategoricalResolved(subject, predicate, result, source, context, SourceTrust.SubstrateMandate);

        Task Ingest(IDecomposer decomposer, string path, int expectedFiles = 1, bool reobservePresent = false) =>
            IngestSource(runner, decomposer, path, expectedFiles, reobservePresent);

        async Task Apply(SubstrateChange change)
        {
            try { await writer.ApplyAsync(change); }
            finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
        }
    }

    private static async Task CopyBundledSource(string root)
    {
        string[] files = Directory.GetFiles(OperationalDecomposer.BundledPath, "*", SearchOption.AllDirectories);
        Assert.Equal(15, files.Length);
        foreach (string path in files)
        {
            string destination = Path.Combine(root, Path.GetRelativePath(OperationalDecomposer.BundledPath, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            await File.WriteAllBytesAsync(destination, bytes);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        }
    }

    private static async Task IngestSource(IngestRunner runner, IDecomposer decomposer,
        string path, int expectedFiles = 1, bool reobservePresent = false)
    {
        // Shared-db Facts need fresh file journals in either test order. The
        // native writer still deduplicates the exact testimony: readback below
        // requires one observation and one consensus witness for each parse.
        IngestRunResult result = await runner.RunAsync(decomposer, IngestRunOptions.Default with
        {
            EcosystemPath = path,
            SkipLayerOrderingCheck = true,
            SkipSourceCompletion = true,
            DecomposerOptions = DecomposerOptions.Default with { ReObservePresent = reobservePresent },
        });
        Assert.Empty(result.Failures);
        Assert.Equal(0, result.UnitsFailed);
        Assert.Equal(expectedFiles, result.FilesDone);
        Assert.Equal(expectedFiles, result.InputUnitsDone);
    }

    private async Task<AuthoredExemplarEvidence> ReadAuthoredExemplar(
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
            "SELECT a.context_id,a.outcome,a.observation_count,a.opponent_rating_fp1e9,a.opponent_rd_fp1e9,c.rating,c.witness_count "
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
            Assert.Equal(1, row.GetInt64(6));
            Assert.False(await row.ReadAsync());
        }
        await using (var query = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.attestations WHERE subject_id=$1 AND type_id=$2 AND object_id=$3 "
            + "AND source_id=$4 AND context_id=$1 AND outcome=2 AND observation_count=1"))
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
        return new AuthoredExemplarEvidence(runId, expectedFile.FileId, occurrence, fingerprint, bytes);
    }

    private sealed record AuthoredExemplarEvidence(Guid RunId, Hash128 FileId,
        Hash128 Occurrence, Hash128 Fingerprint, byte[] Bytes);

    private async Task<Guid> RetainAuthoredExemplar(
        string path, string relativePath, DateTime startedAt, Hash128 root, Hash128 parse)
    {
        var evidence = await ReadAuthoredExemplar(path, relativePath, startedAt, root, parse);
        var (runId, fileId, occurrence, fingerprint, bytes) = evidence;
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
                file_id = Hex(fileId), occurrence_context_id = Hex(occurrence),
                resume_fingerprint = Hex(fingerprint),
                source_root_id = Hex(root), exemplar_parse_id = Hex(parse),
                exemplar_token_ref_id = Hex(UdParseStructure.TokenRefId("2")),
                accepted_entity_type_id = Hex(EntityTypeRegistry.WordNetSynset),
                predicate_id = Hex(RelationTypeRegistry.Resolve("HAS_DEFINITION").Id),
                file_occurrence_context_verified = true, source_trust_verified = true, completion_present = true,
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(receiptPath, json + "\n");
        }
        return runId;
    }

    private async Task AssertFullBundleReceipt(string root, Guid runId, bool retainReceipt = true)
    {
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Assert.Equal(15, files.Length);
        var expected = files.ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'),
            path => (Path: path, Bytes: File.ReadAllBytes(path), Fingerprint: IngestBatchPipeline.TryResolveFileIdentity(path)!.Value),
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var manifest = new List<object>();
        await using var query = pg.DataSource.CreateCommand(
            "SELECT f.relative_path,f.bytes,f.resume_fingerprint,f.file_id,f.status,f.disposition,"
            + "EXISTS (SELECT 1 FROM laplace.attestations a WHERE a.subject_id=f.resume_fingerprint "
            + "AND a.object_id=f.resume_fingerprint AND a.source_id=f.resume_fingerprint "
            + "AND a.type_id=$2 AND a.context_id=$3) "
            + "FROM laplace.ingest_file_journal f WHERE f.run_id=$1 ORDER BY f.relative_path");
        query.Parameters.AddWithValue(runId);
        query.Parameters.AddWithValue(LayerCompletion.RelationTypeId(2).ToBytes());
        query.Parameters.AddWithValue(OperationalSource.SourceId.ToBytes());
        await using var row = await query.ExecuteReaderAsync();
        while (await row.ReadAsync())
        {
            string relative = row.GetString(0);
            Assert.True(seen.Add(relative), "A bundled source artifact must have exactly one receipt in its run.");
            Assert.True(expected.TryGetValue(relative, out var file));
            Assert.Equal(file.Bytes.LongLength, row.GetInt64(1));
            Assert.Equal(file.Fingerprint.ToBytes(), row.GetFieldValue<byte[]>(2));
            Hash128 fileId = Hash128.FromBytes(row.GetFieldValue<byte[]>(3));
            Assert.NotEqual(default, fileId);
            Assert.Equal("ok", row.GetString(4));
            Assert.Equal("admitted", row.GetString(5));
            Assert.True(row.GetBoolean(6), "Every selected artifact must finish through its own source-scoped completion marker.");
            manifest.Add(new { relative_path = relative, bytes = file.Bytes.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant(),
                resume_fingerprint = Hex(file.Fingerprint), file_id = Hex(fileId), completion_present = true });
        }
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
        string? receiptPath = Environment.GetEnvironmentVariable("LAPLACE_OPERATIONAL_EXEMPLAR_RECEIPT");
        if (retainReceipt && !string.IsNullOrWhiteSpace(receiptPath))
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(receiptPath))!, "bundle.json"),
                JsonSerializer.Serialize(new { schema = "laplace.operational-bundle-proof/v1",
                    candidate_sha = Environment.GetEnvironmentVariable("LAPLACE_PR_TARGET_SHA"),
                    run_id = runId, source_id = Hex(OperationalSource.SourceId), files = manifest,
                    selected = 15, admitted = 15, completions = 15,
                }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
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
