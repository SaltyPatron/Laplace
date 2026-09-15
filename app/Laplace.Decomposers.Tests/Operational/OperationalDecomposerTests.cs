using System.Reflection;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Operational;
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
        Assert.Equal(11, graph.Selected.Count);
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
        return (file, builder.Build());
    }
}
