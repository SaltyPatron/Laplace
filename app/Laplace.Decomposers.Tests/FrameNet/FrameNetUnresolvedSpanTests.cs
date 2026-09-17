using System.Text;
using System.Xml.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.FrameNet.Tests;

public sealed class FrameNetUnresolvedSpanTests
{
    static FrameNetUnresolvedSpanTests()
    {
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
    }

    // Exact retained sentence/label slices, not corrected or synthesized coordinates.
    // Observer35189626306/job105099130098, artifact10483998647:
    // SHA256 a624b2223d51c971120d31c21337d3f34e8acad29ebfcd4f3f8b7b3c2a1cb514.
    // lu/lu2188.xml: 0c64dba19ca3ef0741953bb90912e8ec8ef3e0ea30bd0c48c05c034851bdaae3
    // lu/lu9055.xml: a671a4d3bda562152a78534f2a4192a3b3568560a39328f96c2798651e166281
    // lu/lu15925.xml: c432fe4fee015efe8eea3adf8cb73e655e584b25aea425595cd36f64954a04f6
    // lu/lu13531.xml: 5eff411fcf610ce12197f61878a1d7bce871c60bdac7e236983f1cee8d69413b
    // lu/lu15581.xml: b6a99dc4d3d18146f3655072a57c5010ca48b0bf6b93edc5b433a4e89382da43
    // lu/lu15353.xml: 87c1948f6ecba296dd1872e2033bb675d82686e595b6a300af5d8f5ee9b19af8
    // lu/lu10365.xml: 7979f53ca3e7751e5b6aada43726697046ca56def75552b055ee720ebe861c21
    public static IEnumerable<object[]> RetainedFailures()
    {
        yield return new object[] { "lu/lu2188.xml", "Dimension", "Measuring 51 cm ( 20 in ) in diameter , the table is made from 12 mm (  ) chipboard and stands about 61 cm ( 24 \" ) high . ", "218151", "82882", "UNANN", "BNC", "1", "UNC", 71, 70, "Reversed" };
        yield return new object[] { "lu/lu9055.xml", "Killing", "Bishop Farquhar accused Mr Edwards ' killers of hiding ` in the exchanges of bitterness and hostility spread over previous generations \"  . ", "185307", "26298", "UNANN", "BNC", "1", "PUQ", 137, 136, "Reversed" };
        yield return new object[] { "lu/lu9055.xml", "Killing", "` She wore no pants , but there was no evidence of sexual interference , whatever her killer 's intention may have been , \"  he said . ", "185313", "26310", "UNANN", "BNC", "1", "PUQ", 124, 123, "Reversed" };
        yield return new object[] { "lu/lu9055.xml", "Killing", "The daughter of murdered Glasgow pensioner Agnes Law has described the killer as \"  a monster who must be caught . \"  ", "185381", "26446", "UNANN", "BNC", "1", "PUQ", 83, 82, "Reversed" };
        yield return new object[] { "lu/lu9055.xml", "Killing", "The daughter of murdered Glasgow pensioner Agnes Law has described the killer as \"  a monster who must be caught . \"  ", "185381", "26446", "UNANN", "BNC", "1", "PUQ", 117, 116, "Reversed" };
        yield return new object[] { "lu/lu9055.xml", "Killing", "` No , if Michael was set up then it 's obvious the killer wants him alive . \"  ", "185394", "26472", "UNANN", "BNC", "1", "PUQ", 79, 78, "Reversed" };
        yield return new object[] { "lu/lu15925.xml", "Trying_out", "Trying out new materials depends on what the artist wants to do and how the medium can be used to advantage ; at the same time it would be foolish to try to fit a new tool exactly into your pattern of work and thus limit it .", "1581564", "2598089", "UNANN", "PENN", "1", "sent", 225, 225, "OutOfRange" };
        yield return new object[] { "lu/lu13531.xml", "Cause_change_of_strength", "Oxford United are set to strengthen their squad by signing striker Nicky Cusack from Darlington …", "1458794", "2365318", "UNANN", "PENN", "1", "nns", 96, 98, "OutOfRange" };
        yield return new object[] { "lu/lu15581.xml", "Dunking", "Dip each piece into cooked fruit juice .", "1550211", "2543180", "UNANN", "PENN", "1", "sent", 40, 40, "OutOfRange" };
        yield return new object[] { "lu/lu15353.xml", "Locale_by_use", "Here 's what I have with me up at work today :", "1532434", "2507698", "UNANN", "PENN", "1", ":", 45, 46, "OutOfRange" };
        yield return new object[] { "lu/lu10365.xml", "Evoking", "Nothing that rang a bell .", "1254495", "1955981", "AUTO_EDITED", "Target", "1", "Target", 29, 32, "OutOfRange" };
        yield return new object[] { "lu/lu10365.xml", "Evoking", "It rang a bell .", "1254500", "1955991", "AUTO_EDITED", "Target", "1", "Target", 19, 22, "OutOfRange" };
        yield return new object[] { "lu/lu10365.xml", "Evoking", "` Does any of it ring a bell ?", "1254503", "1955997", "AUTO_EDITED", "Target", "1", "Target", 33, 36, "OutOfRange" };
        yield return new object[] { "lu/lu10365.xml", "Evoking", "Does it ring a bell ? \"", "1254504", "1955999", "AUTO_EDITED", "Target", "1", "Target", 24, 27, "OutOfRange" };
        yield return new object[] { "lu/lu10365.xml", "Evoking", "To readers of this column does the name Rosenstein ring a bell ?", "1254531", "1956053", "AUTO_EDITED", "Target", "1", "Target", 67, 70, "OutOfRange" };
    }

    private static FrameNetDecomposer.FulltextAnno Parse(
        string sentence, string frame, string layers, string file = "framenet/lu/fixture.xml",
        string sentenceReference = "9", string annotationReference = "11", string status = "AUTO_EDITED")
    {
        XNamespace ns = "http://framenet.icsi.berkeley.edu";
        var annotation = new XElement(ns + "annotationSet",
            new XAttribute("ID", annotationReference), new XAttribute("status", status));
        foreach (var layer in XElement.Parse("<layers>" + layers + "</layers>").Elements())
        {
            foreach (var element in layer.DescendantsAndSelf())
                element.Name = ns + element.Name.LocalName;
            annotation.Add(layer);
        }
        var document = new XDocument(new XElement(ns + "lexUnit",
            new XAttribute("ID", "1"), new XAttribute("name", "fixture.v"),
            new XAttribute("POS", "V"), new XAttribute("frame", frame),
            new XElement(ns + "subCorpus", new XElement(ns + "sentence",
                new XAttribute("ID", sentenceReference), new XElement(ns + "text", sentence), annotation))));
        return Assert.Single(Assert.Single(FrameNetLuIngest.ParseLu(document, file)!.Sentences).Annotations);
    }

    private static SubstrateChange Compose(FrameNetDecomposer.FulltextAnno annotation)
    {
        using var builder = new SubstrateChangeBuilder(FrameNetDecomposer.Source, "retained-span-fixture", null);
        FrameNetDecomposer.ComposeFulltextAnno(annotation, builder);
        return builder.Build();
    }

    private static void Release(SubstrateChange change)
    {
        foreach (var stage in change.IntentStages.Distinct()) stage.Dispose();
    }

    [Theory]
    [MemberData(nameof(RetainedFailures))]
    public void RetainedInvalidLabelIsRecordedAsAnUnresolvedSourceClaim(
        string file, string frame, string sentence, string sentenceReference, string annotationReference,
        string status, string layer, string rank, string name, int start, int end, string expectedResolution)
    {
        var labelXml = new XElement("label", new XAttribute("name", name),
            new XAttribute("start", start), new XAttribute("end", end));
        string layerXml = new XElement("layer", new XAttribute("name", layer),
            new XAttribute("rank", rank), labelXml).ToString(SaveOptions.DisableFormatting);
        var annotation = Parse(sentence, frame, layerXml, "framenet/" + file,
            sentenceReference, annotationReference, status);
        Assert.Equal(sentence, annotation.Sentence);
        Assert.Equal(status, annotation.Status);
        Assert.Equal(sentenceReference, annotation.SentenceReference);
        Assert.Equal(annotationReference, annotation.AnnotationReference);
        var sourceLayer = Assert.Single(annotation.Layers);
        Assert.Equal((layer, rank), (sourceLayer.Name, sourceLayer.Rank));
        var label = Assert.Single(sourceLayer.Labels);
        Assert.Equal(name, label.Name);
        Assert.Equal<int?>(start, label.Start);
        Assert.Equal<int?>(end, label.End);
        var resolution = FrameNetDecomposer.ClassifySpan(label, sentence.EnumerateRunes().Count());
        Assert.Equal(expectedResolution, resolution.ToString());
        Assert.Throws<FormatException>(() => FrameNetDecomposer.ReadResolvedSpan(sentence, label));
        Assert.True(annotation.HasUnresolvedSpans);
        Assert.False(annotation.HasResolvedTarget);
        Assert.Null(annotation.TargetText);
        Assert.Empty(annotation.TargetSpans);
        Assert.Throws<InvalidOperationException>(() => annotation.TargetStart);
        Assert.Throws<InvalidOperationException>(() => annotation.TargetEnd);

        var change = Compose(annotation);
        try
        {
            var physicality = Assert.Single(change.Physicalities, row => row.Type == PhysicalityType.ParseStructure);
            Hash128 none = FrameNetDecomposer.AnnotationNoneId;
            Hash128[] expected =
            [
                FrameNetDecomposer.UnresolvedAnnotationSchemaId,
                ContentEmitter.RootId(sentence)!.Value, CategoryAnchor.Id(frame)!.Value, none,
                ContentEmitter.RootId(status)!.Value,
                FrameNetDecomposer.AnnotationLayerId, ContentEmitter.RootId(layer)!.Value,
                ContentEmitter.RootId(rank)!.Value, FrameNetDecomposer.AnnotationLabelId,
                ContentEmitter.RootId(name)!.Value,
                Hash128.OfCanonical($"framenet/character-offset/{start}/v1"),
                Hash128.OfCanonical($"framenet/character-offset/{end}/v1"),
                none, none, FrameNetDecomposer.SpanResolutionId(resolution),
                FrameNetDecomposer.AnnotationLayerEndId, FrameNetDecomposer.AnnotationLayersEndId,
            ];
            Assert.Equal(expected, Trajectory.Constituents(physicality.TrajectoryXyzm!));
            Assert.Equal(Hash128.Merkle(EntityTier.Document, expected), physicality.EntityId);
            Assert.Equal(PhysicalityId.Compute(physicality.EntityId, PhysicalityType.ParseStructure), physicality.Id);
            var parse = Assert.Single(change.Attestations,
                a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_PARSE"));
            Assert.Equal(FrameNetDecomposer.Source, parse.SourceId);
            Assert.Equal(physicality.EntityId, parse.ObjectId);
            Assert.NotNull(parse.ContextId);
            Assert.DoesNotContain(change.Attestations,
                a => a.TypeId == RelationTypeRegistry.RelationTypeId("EVOKES_FRAME"));
            Assert.DoesNotContain(change.Physicalities,
                row => row.EntityId == physicality.EntityId && row.Type == PhysicalityType.Content);
        }
        finally { Release(change); }
    }

    private const string RetainedTargetLayers = """
<layer name="Target" rank="1">
  <label cBy="EGp" end="22" name="Target" start="19"/>
  <label cBy="MJE" end="6" name="Target" start="3"/>
  <label cBy="MJE" end="8" name="Target" start="8"/>
  <label cBy="MJE" end="13" name="Target" start="10"/>
</layer>
<layer name="FE" rank="1">
  <label cBy="JKR" end="1" feID="6003" name="Phenomenon" start="0"/>
  <label cBy="MJE" feID="6002" itype="INI" name="Cognizer"/>
</layer>
<layer name="GF" rank="1">
  <label end="1" name="Ext" start="0"/>
</layer>
<layer name="PT" rank="1">
  <label end="1" name="NP" start="0"/>
</layer>
<layer name="Other" rank="1">

</layer>
<layer name="Sent" rank="1">

</layer>
<layer name="Verb" rank="1">

</layer>
""";

    [Fact]
    public void StaleTargetDoesNotSilentlyChooseTheRemainingValidSegments()
    {
        // lu10365.xml sentence1254500/annotation1955991 has these exact four
        // targets. The stale19..22 segment is retained alongside the valid three.
        var annotation = Parse("It rang a bell .", "Evoking", RetainedTargetLayers,
            "framenet/lu/lu10365.xml", "1254500", "1955991");
        var labels = annotation.Layers[0].Labels;
        Assert.Equal(4, labels.Count);
        Assert.Equal<int?>(19, labels[0].Start);
        Assert.Equal<int?>(22, labels[0].End);
        Assert.Equal(new[] { "rang", "a", "bell" },
            labels.Skip(1).Select(label => FrameNetDecomposer.ReadResolvedSpan(annotation.Sentence, label)));
        Assert.Throws<FormatException>(() => FrameNetDecomposer.ReadResolvedSpan(annotation.Sentence, labels[0]));
        Assert.Null(annotation.TargetText);
        Assert.Empty(annotation.TargetSpans);
        var change = Compose(annotation);
        try
        {
            var physicality = Assert.Single(change.Physicalities, row => row.Type == PhysicalityType.ParseStructure);
            var ids = Trajectory.Constituents(physicality.TrajectoryXyzm!);
            Assert.Equal(90, ids.Length);
            Assert.Equal(FrameNetDecomposer.AnnotationNoneId, ids[3]);
            Assert.Equal(1, ids.Count(id => id == FrameNetDecomposer.SpanResolutionId(FrameNetDecomposer.SpanResolution.OutOfRange)));
            Assert.Equal(6, ids.Count(id => id == FrameNetDecomposer.SpanResolutionId(FrameNetDecomposer.SpanResolution.Resolved)));
            Assert.Equal(1, ids.Count(id => id == FrameNetDecomposer.SpanResolutionId(FrameNetDecomposer.SpanResolution.NoSpan)));
            Assert.DoesNotContain(change.Attestations,
                a => a.TypeId == RelationTypeRegistry.RelationTypeId("EVOKES_FRAME"));

            // This is an explicitly different source annotation, not a repair
            // performed by the parser. Its valid v2 identity stays distinct.
            var editedLayers = XElement.Parse("<layers>" + RetainedTargetLayers + "</layers>");
            editedLayers.Element("layer")!.Elements("label").First().Remove();
            var corrected = Parse(annotation.Sentence, "Evoking", string.Concat(editedLayers.Elements()));
            Assert.Equal("rang a bell", corrected.TargetText);
            Assert.False(corrected.HasUnresolvedSpans);
            var correctedChange = Compose(corrected);
            try
            {
                var correctedBody = Assert.Single(correctedChange.Physicalities, row => row.Type == PhysicalityType.ParseStructure);
                Assert.Equal(FrameNetDecomposer.AnnotationSchemaId, Trajectory.Constituents(correctedBody.TrajectoryXyzm!)[0]);
                Assert.NotEqual(physicality.EntityId, correctedBody.EntityId);
                Assert.Single(correctedChange.Attestations,
                    a => a.TypeId == RelationTypeRegistry.RelationTypeId("EVOKES_FRAME"));
            }
            finally { Release(correctedChange); }
        }
        finally { Release(change); }
    }

    [Fact]
    public async Task UnresolvedLuAndFulltextShareTheSameTypedStructure()
    {
        var lu = Parse("It rang a bell .", "Evoking", RetainedTargetLayers,
            "framenet/lu/lu10365.xml", "1254500", "1955991");
        string xml = "<fullTextAnnotation><sentence ID=\"1254500\"><text>It rang a bell .</text>"
            + "<annotationSet ID=\"1955991\" frameName=\"Evoking\" status=\"AUTO_EDITED\">"
            + RetainedTargetLayers + "</annotationSet></sentence></fullTextAnnotation>";
        string path = Path.Combine(Path.GetTempPath(), "fn-retained-" + Guid.NewGuid().ToString("N") + ".xml");
        await File.WriteAllTextAsync(path, xml);
        try
        {
            var parsed = new List<FrameNetDecomposer.FulltextAnno>();
            await foreach (var value in FrameNetDecomposer.ParseFulltextAsync(path, "framenet/lu/lu10365.xml", CancellationToken.None))
                parsed.Add(value);
            var fulltext = Assert.Single(parsed);
            Assert.Null(fulltext.TargetText);
            var left = Compose(lu);
            var right = Compose(fulltext);
            try
            {
                var leftBody = Assert.Single(left.Physicalities, row => row.Type == PhysicalityType.ParseStructure);
                var rightBody = Assert.Single(right.Physicalities, row => row.Type == PhysicalityType.ParseStructure);
                Assert.Equal(leftBody.EntityId, rightBody.EntityId);
                Assert.Equal(leftBody.TrajectoryXyzm, rightBody.TrajectoryXyzm);
                Assert.Equal(left.Attestations.Select(a => a.Id), right.Attestations.Select(a => a.Id));
            }
            finally { Release(left); Release(right); }
        }
        finally { File.Delete(path); }
    }
}
