using System.Xml.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.FrameNet.Tests;

public sealed class FrameNetAnnotationStructureTests
{
    static FrameNetAnnotationStructureTests()
    {
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
    }

    private const string Sentence = "🧪  水 gave eau";
    private const string Layers = """
<layer name="Target" rank="1"><label name="Target" start="5" end="8"/></layer>
<layer name="FE" rank="1">
  <label name="Donor" start="3" end="3"/>
  <label name="Theme" start="10" end="12"/>
  <label name="Recipient" itype="DNI"/>
</layer>
<layer name="FE" rank="2"><label name="Theme" start="3" end="3"/></layer>
<layer name="GF" rank="1"><label name="Ext" start="3" end="3"/><label name="Obj" start="10" end="12"/></layer>
<layer name="PT" rank="1"><label name="NP" start="3" end="3"/><label name="NP" start="10" end="12"/></layer>
""";

    private static FrameNetLuIngest.LuDocument ParseLu(string layers = Layers) =>
        FrameNetLuIngest.ParseLu(XDocument.Parse($"""
<lexUnit xmlns="http://framenet.icsi.berkeley.edu" ID="1" name="give.v" POS="V" frame="Giving">
  <subCorpus><sentence ID="9"><text>{Sentence}</text><annotationSet ID="11" status="MANUAL">{layers}</annotationSet></sentence></subCorpus>
</lexUnit>
"""), "framenet/lu/fixture.xml")!;

    private static SubstrateChange Compose(FrameNetDecomposer.FulltextAnno annotation)
    {
        var builder = new SubstrateChangeBuilder(FrameNetDecomposer.Source, "fixture", null);
        FrameNetDecomposer.ComposeFulltextAnno(annotation, builder);
        return builder.Build();
    }

    private static PhysicalityRow AnnotationPhysicality(SubstrateChange change) =>
        Assert.Single(change.Physicalities, p => p.Type == PhysicalityType.ParseStructure);

    [Fact]
    public void LuOffsetsAddressUnicodeCharactersInTheUnchangedSentence()
    {
        var sentence = Assert.Single(ParseLu().Sentences);
        Assert.Equal(Sentence, sentence.Text);
        Assert.Equal("gave", sentence.TargetText);
        var annotation = Assert.Single(sentence.Annotations);
        Assert.Equal((5, 8), (annotation.TargetStart, annotation.TargetEnd));
        Assert.Equal("framenet/lu/fixture.xml", annotation.FileLabel);
        Assert.Equal("9", annotation.SentenceReference);
        Assert.Equal("11", annotation.AnnotationReference);
        Assert.Equal("MANUAL", annotation.Status);
        Assert.Collection(annotation.Layers,
            layer => Assert.Equal(("Target", "1"), (layer.Name, layer.Rank)),
            layer =>
            {
                Assert.Equal(("FE", "1"), (layer.Name, layer.Rank));
                var absent = Assert.Single(layer.Labels, label => label.Name == "Recipient");
                Assert.Null(absent.Start);
                Assert.Null(absent.End);
                Assert.Equal("DNI", absent.InstantiationType);
            },
            layer => Assert.Equal(("FE", "2"), (layer.Name, layer.Rank)),
            layer => Assert.Equal(("GF", "1"), (layer.Name, layer.Rank)),
            layer => Assert.Equal(("PT", "1"), (layer.Name, layer.Rank)));
    }

    [Fact]
    public void StoredTrajectoryRetainsRoleBindingsNullInstantiationAndLayerRank()
    {
        var annotation = Assert.Single(Assert.Single(ParseLu().Sentences).Annotations);
        var change = Compose(annotation);
        var physicality = AnnotationPhysicality(change);
        Hash128[] ids = Trajectory.Constituents(physicality.TrajectoryXyzm!);
        Assert.Equal(FrameNetDecomposer.AnnotationSchemaId, ids[0]);
        Assert.Equal(ContentEmitter.RootId(Sentence)!.Value, ids[1]);
        Hash128 frameId = CategoryAnchor.Id("Giving")!.Value;
        Assert.Equal(frameId, ids[2]);
        Assert.Equal(ContentEmitter.RootId("gave")!.Value, ids[3]);
        Assert.Equal(ContentEmitter.RootId("MANUAL")!.Value, ids[4]);

        int cursor = 5;
        foreach (var layer in annotation.Layers)
        {
            Assert.Equal(FrameNetDecomposer.AnnotationLayerId, ids[cursor++]);
            Assert.Equal(ContentEmitter.RootId(layer.Name)!.Value, ids[cursor++]);
            Assert.Equal(ContentEmitter.RootId(layer.Rank!)!.Value, ids[cursor++]);
            foreach (var label in layer.Labels)
            {
                Assert.Equal(FrameNetDecomposer.AnnotationLabelId, ids[cursor++]);
                Assert.Equal(ContentEmitter.RootId(label.Name)!.Value, ids[cursor++]);
                Assert.Equal(label.Start is { } first ? FrameNetDecomposer.OffsetId(first)
                    : FrameNetDecomposer.AnnotationNoneId, ids[cursor++]);
                Assert.Equal(label.End is { } last ? FrameNetDecomposer.OffsetId(last)
                    : FrameNetDecomposer.AnnotationNoneId, ids[cursor++]);
                Assert.Equal(label.InstantiationType is { } ni ? ContentEmitter.RootId(ni)!.Value
                    : FrameNetDecomposer.AnnotationNoneId, ids[cursor++]);
                Assert.Equal(layer.Name == "FE"
                    ? RoleAnchor.Id(RoleIdentityKind.FrameNet, frameId, label.Name)!.Value
                    : FrameNetDecomposer.AnnotationNoneId, ids[cursor++]);
            }
            Assert.Equal(FrameNetDecomposer.AnnotationLayerEndId, ids[cursor++]);
        }
        Assert.Equal(FrameNetDecomposer.AnnotationLayersEndId, ids[cursor++]);
        Assert.Equal(ids.Length, cursor);
        var parse = Assert.Single(change.Attestations,
            a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_PARSE"));
        var evokes = Assert.Single(change.Attestations,
            a => a.TypeId == RelationTypeRegistry.RelationTypeId("EVOKES_FRAME"));
        Assert.Equal(ContentEmitter.RootId(Sentence)!.Value, parse.SubjectId);
        Assert.Equal(physicality.EntityId, parse.ObjectId);
        Assert.Equal(physicality.EntityId, evokes.SubjectId);
        Assert.Equal(frameId, evokes.ObjectId);
        Assert.Equal(FrameNetDecomposer.Source, parse.SourceId);
        Assert.NotNull(parse.ContextId);
        Assert.Equal(parse.ContextId, evokes.ContextId);
    }

    [Fact]
    public void ChangingSourceRoleRankOrNullInstantiationChangesStructureIdentity()
    {
        var original = Assert.Single(Assert.Single(ParseLu().Sentences).Annotations);
        var originalChange = Compose(original);
        Hash128 originalId = AnnotationPhysicality(originalChange).EntityId;
        foreach (string changedLayers in new[]
        {
            Layers.Replace("name=\"Donor\"", "name=\"Recipient\""),
            Layers.Replace("rank=\"2\"", "rank=\"3\""),
            Layers.Replace("itype=\"DNI\"", "itype=\"INI\""),
            Layers.Replace("start=\"10\" end=\"12\"", "start=\"3\" end=\"3\""),
        })
        {
            var changed = Compose(Assert.Single(Assert.Single(ParseLu(changedLayers).Sentences).Annotations));
            Assert.NotEqual(originalId, AnnotationPhysicality(changed).EntityId);
        }
        var repeated = Compose(original with { AnnotationReference = "12" });
        Assert.Equal(originalId, AnnotationPhysicality(repeated).EntityId);
        Assert.NotEqual(
            Assert.Single(originalChange.Attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_PARSE")).ContextId,
            Assert.Single(repeated.Attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_PARSE")).ContextId);
    }

    [Fact]
    public async Task FulltextAndLuUseTheSameAnnotationStructureWithoutXmlWhitespaceDependence()
    {
        string xml = $"<fullTextAnnotation><sentence ID=\"9\"><text>{Sentence}</text>"
            + $"<annotationSet ID=\"11\" status=\"MANUAL\" frameName=\"Giving\">{Layers}</annotationSet></sentence></fullTextAnnotation>";
        string path = Path.Combine(Path.GetTempPath(), "fn-unicode-" + Guid.NewGuid().ToString("N") + ".xml");
        await File.WriteAllTextAsync(path, xml);
        try
        {
            var annotations = new List<FrameNetDecomposer.FulltextAnno>();
            await foreach (var annotation in FrameNetDecomposer.ParseFulltextAsync(
                    path, "framenet/fulltext/fixture.xml", CancellationToken.None))
                annotations.Add(annotation);
            var fulltext = Assert.Single(annotations);
            Assert.Equal("gave", fulltext.TargetText);
            var fromFulltext = Compose(fulltext);
            var fromLu = Compose(Assert.Single(Assert.Single(ParseLu().Sentences).Annotations));
            Assert.Equal(AnnotationPhysicality(fromLu).EntityId, AnnotationPhysicality(fromFulltext).EntityId);
            Assert.NotEqual(
                Assert.Single(fromLu.Attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_PARSE")).ContextId,
                Assert.Single(fromFulltext.Attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_PARSE")).ContextId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InvalidRoleSpanIsRejectedInsteadOfSilentlyDiscardingTheRole()
    {
        Assert.Throws<FormatException>(() => ParseLu(Layers.Replace("end=\"12\"", "end=\"99\"")));
        Assert.Throws<FormatException>(() => FrameNetDecomposer.ReadAnnotationLabel("Theme", "3", null, null));
    }
}
