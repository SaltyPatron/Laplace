using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.FrameNet.Tests;

public sealed class FrameNetPhysicalityContractTests
{
    static FrameNetPhysicalityContractTests()
    {
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
    }

    [Fact]
    public async Task FulltextAnnotationIsStructuralWhileItsTextBranchesRemainContentDags()
    {
        const string xml = """
<?xml version="1.0" encoding="UTF-8"?>
<fullTextAnnotation xmlns="http://framenet.icsi.berkeley.edu">
  <sentence ID="77">
    <text>fucking:antitrust</text>
    <annotationSet ID="100" frameName="Statement">
      <layer name="Target"><label name="Target" start="8" end="16"/></layer>
    </annotationSet>
  </sentence>
</fullTextAnnotation>
""";

        string dir = Path.Combine(Path.GetTempPath(), "fn-physicality-law-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "fulltext"));
        await File.WriteAllTextAsync(Path.Combine(dir, "fulltext", "sample.xml"), xml);
        try
        {
            var changes = new List<SubstrateChange>();
            var dec = new FrameNetDecomposer();
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                changes.Add(change);

            var annotationIds = changes.SelectMany(c => c.Entities)
                .Where(e => e.TypeId == EntityTypeRegistry.FrameNetAnnotation)
                .Select(e => e.Id)
                .ToHashSet();
            Hash128 annotationId = Assert.Single(annotationIds);

            var annotation = Assert.Single(changes.SelectMany(c => c.Physicalities),
                p => p.EntityId == annotationId);
            Assert.Equal(PhysicalityType.ParseStructure, annotation.Type);
            Assert.Equal(
                PhysicalityId.Compute(annotationId, PhysicalityType.ParseStructure),
                annotation.Id);
            Assert.DoesNotContain(changes.SelectMany(c => c.Physicalities),
                p => p.EntityId == annotationId && p.Type == PhysicalityType.Content);

            Hash128 sentence = ContentEmitter.RootId("fucking:antitrust")!.Value;
            Hash128 target = ContentEmitter.RootId("antitrust")!.Value;
            int nativeContentPhysicalities = changes
                .SelectMany(c => c.IntentStages)
                .Sum(stage => stage.PhysicalityCount);
            Assert.True(nativeContentPhysicalities >= 2,
                "sentence and target content DAGs must be staged through the native content lane");

            Hash128[] members = Trajectory.Constituents(annotation.TrajectoryXyzm!);
            Assert.Equal(FrameNetDecomposer.AnnotationSchemaId, members[0]);
            Assert.Equal(sentence, members[1]);
            Assert.Equal(target, members[^1]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
