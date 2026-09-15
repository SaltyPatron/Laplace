using System.Reflection;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Operational;
using Laplace.Decomposers.UD;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Decomposers.Tests.Operational;

public sealed class OperationalDecomposerTests
{
    static OperationalDecomposerTests()
    {
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);
    }

    [Fact]
    public async Task BundledContracts_AreExactRepositoryArtifactsWithOriginalPaths()
    {
        string repo = typeof(OperationalDecomposerTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "LaplaceRepoRoot").Value!;
        var decomposer = new OperationalDecomposer();
        var graph = await decomposer.DescribeArtifactsAsync(
            OperationalDecomposer.BundledPath, DecomposerOptions.Default);
        Assert.NotNull(graph);
        Assert.Contains(graph.Selected, a => a.RelativePath == "docs/specs/37_Substrate_Operation_ISA.md");
        Assert.Contains(graph.Selected, a => a.RelativePath == "docs/INVENTION.md");
        Assert.Contains(graph.Selected, a => a.RelativePath == "seeds/operational/tasks/en_define.json");
        Assert.Equal(13, graph.Selected.Count);
        foreach (var artifact in graph.Selected)
        {
            byte[] original = await File.ReadAllBytesAsync(Path.Combine(repo, artifact.RelativePath));
            var record = await OperationalDecomposer.ReadContractAsync(artifact.Path, artifact.RelativePath);
            Assert.Equal(original, record.Utf8);
            Assert.Equal(artifact.RelativePath, record.FileMetadata!.Value.RelativePath);
            Assert.Null(record.ExampleSegments);
            Assert.Null(record.KeywordExamples);
            Assert.Null(record.ConceptAnchorKey);
            Assert.Null(record.ObservedPromptUtf8);
        }
    }

    [Fact]
    public async Task AuthoredConllu_RetainsBytesAndWitnessesOperationalFileOccurrence()
    {
        const string relative = "seeds/operational/exemplars/en_define.conllu";
        string path = Path.Combine(OperationalDecomposer.BundledPath, relative);
        byte[] original = await File.ReadAllBytesAsync(path);
        var record = await OperationalDecomposer.ReadContractAsync(path, relative);
        Assert.Equal(original, record.Utf8);
        var (file, change) = Compose(record);
        try
        {
            Hash128 hasParse = RelationTypeRegistry.Resolve("HAS_PARSE").Id;
            AttestationRow claim = Assert.Single(change.Attestations.Where(a => a.TypeId == hasParse));
            Assert.Equal(OperationalSource.SourceId, claim.SourceId);
            Assert.NotEqual(UDSource.SourceId, claim.SourceId);
            Assert.Equal(ContentTierSpine.ResolveRoot("define justice"), claim.SubjectId);
            Assert.NotNull(claim.ContextId);
            Assert.Contains(change.Attestations, a => a.SubjectId == file
                && a.TypeId == RelationTypeRegistry.Resolve("CONTAINS").Id
                && a.ObjectId == claim.ContextId && a.ContextId == file
                && a.SourceId == OperationalSource.SourceId);
            PhysicalityRow physicality = Assert.Single(change.Physicalities.Where(p =>
                p.EntityId == claim.ObjectId && p.Type == PhysicalityType.ParseStructure));
            Hash128[] flat = Trajectory.Constituents(physicality.TrajectoryXyzm!);
            Assert.True(UdParseStructure.TryDecode(flat, out var parsed));
            Assert.Equal(2, parsed!.Tokens.Count);
            Assert.Equal(UdParseStructure.TokenRefId("2"), parsed.Tokens[1].RefId);
            Assert.Equal(UdParseStructure.TokenRefId("1"), parsed.Tokens[1].HeadRefId);
            Assert.Equal(RelationTypeRegistry.ResolveDeprel("obj").Id, parsed.Tokens[1].DeprelId);
            Assert.Empty(parsed.Mwts);
            Assert.DoesNotContain(change.Attestations, a => a.SourceId == UDSource.SourceId);
        }
        finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
    }

    [Fact]
    public async Task CoNlluMemoryAndFileReadersPreserveTheSameAnnotation()
    {
        string path = Path.Combine(OperationalDecomposer.BundledPath,
            "seeds/operational/exemplars/en_define.conllu");
        var fromFile = new List<UdSentence>();
        await foreach (var sentence in UdConlluParser.ParseSentencesAsync(path)) fromFile.Add(sentence);
        using var stream = new MemoryStream(await File.ReadAllBytesAsync(path), writable: false);
        var fromMemory = new List<UdSentence>();
        await foreach (var sentence in UdConlluParser.ParseSentencesAsync(stream)) fromMemory.Add(sentence);
        Assert.Single(fromFile);
        Assert.Equal(JsonSerializer.Serialize(fromFile), JsonSerializer.Serialize(fromMemory));
    }

    [Fact]
    public async Task BundledDefinitionTaskReferencesTheActualAuthoredParseAndSemanticArgument()
    {
        const string exemplarRelative = "seeds/operational/exemplars/en_define.conllu";
        const string taskRelative = "seeds/operational/tasks/en_define.json";
        string exemplarPath = Path.Combine(OperationalDecomposer.BundledPath, exemplarRelative);
        string taskPath = Path.Combine(OperationalDecomposer.BundledPath, taskRelative);
        var exemplarRecord = await OperationalDecomposer.ReadContractAsync(exemplarPath, exemplarRelative);
        var taskRecord = await OperationalDecomposer.ReadContractAsync(taskPath, taskRelative);
        Assert.Equal(await File.ReadAllBytesAsync(exemplarPath), exemplarRecord.Utf8);
        Assert.Equal(await File.ReadAllBytesAsync(taskPath), taskRecord.Utf8);
        Assert.NotNull(exemplarRecord.StructureWitness);
        Assert.NotNull(taskRecord.StructureWitness);

        using var ast = GrammarDecomposer.Parse(taskRecord.Utf8, "json");
        var declared = OperationalTaskShapeWitness.Read(ast, taskRecord.Utf8);
        Assert.True(ast.NodeCount > 0);
        var changes = new List<SubstrateChange>();
        try
        {
            var exemplar = Compose(exemplarRecord);
            changes.Add(exemplar.Change);
            Hash128 hasParse = RelationTypeRegistry.Resolve("HAS_PARSE").Id;
            AttestationRow parseClaim = Assert.Single(exemplar.Change.Attestations.Where(a => a.TypeId == hasParse));
            Assert.Equal(OperationalSource.SourceId, parseClaim.SourceId);
            Assert.NotNull(parseClaim.ObjectId);
            Assert.NotNull(parseClaim.ContextId);
            PhysicalityRow parseStructure = Assert.Single(exemplar.Change.Physicalities.Where(p =>
                p.EntityId == parseClaim.ObjectId && p.Type == PhysicalityType.ParseStructure));
            Assert.NotNull(parseStructure.TrajectoryXyzm);
            Assert.True(UdParseStructure.TryDecode(Trajectory.Constituents(parseStructure.TrajectoryXyzm!), out var parsed));
            Assert.NotNull(parsed);
            Assert.Equal(2, parsed.Tokens.Count);
            Assert.Equal(parsed.Tokens[0].RefId, parsed.Tokens[1].HeadRefId);
            Assert.Equal(RelationTypeRegistry.ResolveDeprel("obj").Id, parsed.Tokens[1].DeprelId);
            Assert.Equal(parseClaim.ObjectId.Value, declared.ExemplarParseId);
            Assert.Equal(RelationTypeRegistry.Resolve("HAS_DEFINITION").Id, declared.PredicateId);
            var slot = Assert.Single(declared.Slots);
            Assert.Equal(parsed.Tokens[1].RefId, slot.TokenRefId);
            Assert.NotEqual(parsed.Tokens[0].RefId, slot.TokenRefId);
            Assert.Equal(EntityTypeRegistry.WordNetSynset, slot.AcceptedTypeId);
            Assert.Equal(new[] {
                OperationalTaskShapeWitness.SchemaId, declared.ExemplarParseId, declared.PredicateId,
                slot.Id, slot.TokenRefId, slot.AcceptedTypeId, OperationalTaskShapeWitness.SlotsEndId,
            }, declared.Constituents);
            Assert.Contains(exemplar.Change.Attestations, a => a.SubjectId == exemplar.FileId
                && a.TypeId == RelationTypeRegistry.Resolve("CONTAINS").Id
                && a.ObjectId == parseClaim.ContextId && a.ContextId == exemplar.FileId
                && a.SourceId == OperationalSource.SourceId);

            var task = Compose(taskRecord);
            changes.Add(task.Change);
            Assert.NotEqual(exemplar.FileId, task.FileId);
            PhysicalityRow shape = Assert.Single(task.Change.Physicalities.Where(p =>
                p.EntityId == declared.Id && p.Type == PhysicalityType.ParseStructure));
            Assert.Equal(PhysicalityId.Compute(declared.Id, PhysicalityType.ParseStructure), shape.Id);
            Assert.NotNull(shape.TrajectoryXyzm);
            Assert.Equal(declared.Constituents, Trajectory.Constituents(shape.TrajectoryXyzm!));
            Assert.DoesNotContain(task.Change.Physicalities,
                p => p.EntityId == declared.Id && p.Type == PhysicalityType.Content);
            Assert.Contains(task.Change.Entities, e => e.Id == slot.Id
                && e.TypeId == EntityTypeRegistry.CodeConcept && e.FirstObservedBy == OperationalSource.SourceId);

            var declarations = task.Change.Attestations.Where(a => a.SubjectId == declared.Id
                || (a.SubjectId == declared.ExemplarParseId && a.TypeId == OperationalSource.ExampleOfTypeId)).ToArray();
            Assert.Equal(3, declarations.Length);
            Assert.Contains(declarations, a => a.SubjectId == declared.ExemplarParseId
                && a.TypeId == OperationalSource.ExampleOfTypeId && a.ObjectId == declared.Id);
            Assert.Contains(declarations, a => a.SubjectId == declared.Id
                && a.TypeId == OperationalSource.CallsTypeId && a.ObjectId == declared.PredicateId);
            Assert.Contains(declarations, a => a.SubjectId == declared.Id
                && a.TypeId == OperationalSource.InputTypeId && a.ObjectId == slot.Id);
            Assert.All(declarations, a =>
            {
                Assert.Equal(OperationalSource.SourceId, a.SourceId);
                Assert.Equal(task.FileId, a.ContextId);
                var expected = NativeAttestation.CategoricalResolved(a.SubjectId, a.TypeId, a.ObjectId,
                    OperationalSource.SourceId, task.FileId, SourceTrust.SubstrateMandate);
                Assert.Equal(expected.Id, a.Id);
                Assert.Equal(expected.Outcome, a.Outcome);
                Assert.Equal(expected.OpponentRdFp1e9, a.OpponentRdFp1e9);
            });
        }
        finally
        {
            foreach (var change in changes)
                foreach (var stage in change.IntentStages) stage.Dispose();
        }
    }

    [Fact]
    public async Task MandateProvenance_AndNativeGrammarFileStructureAreAdmitted()
    {
        var writer = new CapturingWriter();
        var decomposer = new OperationalDecomposer();
        await decomposer.InitializeAsync(new FakeContext(OperationalDecomposer.BundledPath, writer));
        Assert.Contains(writer.Captured.SelectMany(c => c.Attestations), a =>
            a.SubjectId == OperationalSource.SourceId
            && a.TypeId == BootstrapIntentBuilder.HasTrustClassTypeId
            && a.ObjectId == SubstrateCanonicalIds.TrustClass("SubstrateMandate"));

        const string relative = "docs/specs/37_Substrate_Operation_ISA.md";
        var record = await OperationalDecomposer.ReadContractAsync(
            Path.Combine(OperationalDecomposer.BundledPath, relative), relative);
        var (fileId, change) = Compose(record);
        try
        {
            var entities = CopyTupleParser.ParseEntities(
                change.IntentStages.Select(s => s.TupleBuffer(IntentStageTable.Entities)).ToList());
            var physicalities = CopyTupleParser.ParsePhysicalities(
                change.IntentStages.Select(s => s.TupleBuffer(IntentStageTable.Physicalities)).ToList());
            Assert.Contains(fileId, entities.Ids);
            Assert.Contains(fileId, physicalities.EntityIds);
            Assert.Contains(ContentEmitter.RootId("COUPLE")!.Value, entities.Ids);
            var governed = RelationTypeRegistry.AllCanonical().Select(r => r.Id).ToHashSet();
            Assert.DoesNotContain(change.Attestations, a => governed.Contains(a.SubjectId)
                && a.TypeId == RelationTypeRegistry.Resolve("HAS_NAME_ALIAS").Id);
        }
        finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
    }

    [Fact]
    public async Task SourceEditChangesNativeIdentity_AndTouchDoesNot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "operational-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "contract.md");
            await File.WriteAllTextAsync(path, "# Contract\nCOUPLE before ORIENT.\n");
            var first = Compose(await OperationalDecomposer.ReadContractAsync(path, "docs/contract.md"));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            var replay = Compose(await OperationalDecomposer.ReadContractAsync(path, "docs/contract.md"));
            await File.WriteAllTextAsync(path, "# Contract\nORIENT before COUPLE.\n");
            var changed = Compose(await OperationalDecomposer.ReadContractAsync(path, "docs/contract.md"));
            try
            {
                Assert.Equal(first.FileId, replay.FileId);
                Assert.NotEqual(first.FileId, changed.FileId);
            }
            finally
            {
                foreach (var change in new[] { first.Change, replay.Change, changed.Change })
                    foreach (var stage in change.IntentStages) stage.Dispose();
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static (Hash128 FileId, SubstrateChange Change) Compose(GrammarComposeRecord record)
    {
        var handler = new GrammarComposeHandler(OperationalSource.SourceId, SourceTrust.SubstrateMandate, null);
        using var unit = handler.CreateDeferredUnit(record);
        var builder = new SubstrateChangeBuilder(OperationalSource.SourceId, "contract-file");
        Hash128 file = unit.DrainInto(builder, SourceTrust.SubstrateMandate, null);
        handler.WalkWitness(record, file, builder, unit);
        SubstrateChange change = builder.Build();
        Assert.Equal(file, change.Metadata.FileId);
        return (file, change);
    }
}
