using System.Xml.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.FrameNet.Tests;

public sealed class FrameNetDecomposerTests
{
    static FrameNetDecomposerTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolvePerfcacheBlob());
    }

    private static string ResolvePerfcacheBlob() => TestInstall.ResolvePerfcacheOrThrow();

    private const string FrameXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<frame name="Giving" ID="139" xmlns="http://framenet.icsi.berkeley.edu">
    <definition>&lt;def-root&gt;A &lt;fex name="Donor"&gt;Donor&lt;/fex&gt; transfers a &lt;fen&gt;Theme&lt;/fen&gt; to a Recipient. &lt;ex&gt;&lt;fex name="Donor"&gt;She&lt;/fex&gt; &lt;t&gt;gave&lt;/t&gt; him a book.&lt;/ex&gt;&lt;/def-root&gt;</definition>
    <FE coreType="Core" name="Donor" ID="1">
        <definition>&lt;def-root&gt;The person that gives the &lt;fen&gt;Theme&lt;/fen&gt;.&lt;/def-root&gt;</definition>
        <requiresFE name="Place" ID="2"/>
    </FE>
    <FE coreType="Peripheral" name="Place" ID="2">
        <definition>&lt;def-root&gt;Where the giving happens.&lt;/def-root&gt;</definition>
        <excludesFE name="Donor" ID="1"/>
    </FE>
    <lexUnit status="Finished_Initial" POS="V" name="give.v" ID="4344"/>
    <lexUnit status="Finished_Initial" POS="N" name="donation.n" ID="5345"/>
    <lexUnit status="Finished_Initial" POS="IDIO" name="give away.idio" ID="9999"/>
    <frameRelation type="Inherits from">
        <relatedFrame ID="206">Transfer</relatedFrame>
    </frameRelation>
    <frameRelation type="Uses">
        <relatedFrame ID="198">Intentionally_act</relatedFrame>
    </frameRelation>
    <frameRelation type="Subframe of">
        <relatedFrame ID="300">Commerce_scenario</relatedFrame>
    </frameRelation>
    <frameRelation type="Is Inherited by">
        <relatedFrame ID="999">Donating</relatedFrame>
    </frameRelation>
</frame>
""";

    private static FrameNetDecomposer.Frame ParseFixture() =>
        FrameNetDecomposer.ParseFrame(XDocument.Parse(FrameXml))
        ?? throw new InvalidOperationException("fixture failed to parse");

    [Fact]
    public void ParseFrame_Extracts_Frame_FEs_LUs_And_CanonicalDirectionRelations_Only()
    {
        var f = ParseFixture();

        Assert.Equal("Giving", f.Name);
        Assert.Contains("transfers a Theme to a Recipient", f.Definition);
        Assert.DoesNotContain("gave him a book", f.Definition);
        Assert.Contains(f.Examples, e => e.Contains("gave him a book"));

        Assert.Equal(2, f.Elements.Count);
        Assert.Contains(f.Elements, fe => fe.Name == "Donor" && fe.CoreType == "Core");
        Assert.Contains(f.Elements, fe => fe.Name == "Place" && fe.CoreType == "Peripheral");
        Assert.Contains(f.Elements, fe => fe.Name == "Donor" && fe.Definition.Contains("person that gives"));

        Assert.Equal(3, f.LexUnits.Count);
        Assert.Contains(f.LexUnits, lu => lu.Lemma == "give" && lu.Pos == "V");
        Assert.Contains(f.LexUnits, lu => lu.Lemma == "donation" && lu.Pos == "N");
        Assert.Contains(f.LexUnits, lu => lu.Lemma == "give away" && lu.Pos == "IDIO");

        Assert.Equal(3, f.Relations.Count);
        Assert.Contains(f.Relations, r => r.Type == "Inherits from" && r.TargetFrame == "Transfer");
        Assert.Contains(f.Relations, r => r.Type == "Uses" && r.TargetFrame == "Intentionally_act");
        Assert.Contains(f.Relations, r => r.Type == "Subframe of" && r.TargetFrame == "Commerce_scenario");
        Assert.DoesNotContain(f.Relations, r => r.Type == "Is Inherited by");
    }

    [Fact]
    public void ParseLu_Extracts_Definition_Valence_And_AnnotatedSentence()
    {
        const string luXml = """
<?xml version="1.0" encoding="UTF-8"?>
<lexUnit status="FN1_Sent" POS="V" name="copy.v" ID="10" frame="Duplication" xmlns="http://framenet.icsi.berkeley.edu">
  <definition>COD: make a copy of.</definition>
  <lexeme POS="V" name="copy"/>
  <valences>
    <FERealization total="1">
      <FE name="Creator"/>
      <pattern total="1">
        <valenceUnit GF="Ext" PT="NP" FE="Creator"/>
      </pattern>
    </FERealization>
  </valences>
  <subCorpus name="V-test">
    <sentence ID="1">
      <text>She copied the file.</text>
      <annotationSet status="MANUAL" ID="99">
        <layer rank="1" name="Target">
          <label name="Target" start="4" end="10"/>
        </layer>
      </annotationSet>
    </sentence>
  </subCorpus>
</lexUnit>
""";

        var lu = FrameNetLuIngest.ParseLu(System.Xml.Linq.XDocument.Parse(luXml));
        Assert.NotNull(lu);
        Assert.Equal(10, lu!.Id);
        Assert.Equal("Duplication", lu.FrameName);
        Assert.Equal("copy", lu.Lemma);
        Assert.Contains("make a copy", lu.Definition);
        Assert.Contains(lu.ValencePatterns, p => p.Pattern.Contains("Creator"));
        // FrameNet states the annotated-instance count on every <pattern> as total="N"
        // (192,241 of them in framenet_v17) and it was never read: observationCount was
        // however many times the pattern STRING repeated in the XML, a structural artifact
        // of the file layout. Every collected pattern now carries a positive count.
        Assert.All(lu.ValencePatterns, p => Assert.True(p.Total >= 1));
        Assert.Single(lu.Sentences);
        Assert.Equal("copied", lu.Sentences[0].TargetText);
    }

    [Fact]
    public async Task Attestations_Use_RegistryRouted_Canonical_Type_Ids()
    {
        // Content attestations only — period-boundary/HasLayerCompleted markers
        // (GH #898 per-file resume) are ops metadata typed outside the highway
        // registry on purpose (LayerCompletion.RelationTypeId).
        var atts = await CollectAttestationsAsync();

        var canonical = new HashSet<Hash128>(RelationTypeRegistry.AllCanonical().Select(k => k.Id));
        Assert.All(atts, a => Assert.Contains(a.TypeId, canonical));

        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("EVOKES_FRAME"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_FRAME_ELEMENT"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_DEFINITION"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_POS"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_EXAMPLE"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("FRAME_USES"));

        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("IS_TYPED_AS"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SUBEVENT"));
    }

    [Fact]
    public async Task FeToFe_Requires_And_Excludes_Are_Emitted()
    {
        var atts = await CollectAttestationsAsync();
        var frameId = CategoryAnchor.Id("Giving")!.Value;
        var donorId = RoleAnchor.Id(RoleIdentityKind.FrameNet, frameId, "Donor");
        var placeId = RoleAnchor.Id(RoleIdentityKind.FrameNet, frameId, "Place");
        Assert.NotNull(donorId);
        Assert.NotNull(placeId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("REQUIRES")
            && a.SubjectId == donorId!.Value && a.ObjectId == placeId!.Value);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("EXCLUDES")
            && a.SubjectId == placeId!.Value && a.ObjectId == donorId!.Value);
    }

    [Fact]
    public async Task EvokesFrame_Targets_Frame_AndCorenessBelongsToBoundFrameElement()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(FrameNetDecomposer.Source, "fixture", null);
        var giveId = ContentEmitter.Emit(b, "give", FrameNetDecomposer.Source);
        var frameId = CategoryAnchor.Id("Giving");
        Assert.NotNull(giveId);
        Assert.NotNull(frameId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("EVOKES_FRAME")
            && a.SubjectId == giveId!.Value
            && a.ObjectId == frameId!.Value);

        var coreCtx = Hash128.OfCanonical("framenet/coreness/Core");
        var donorRole = RoleAnchor.Id(RoleIdentityKind.FrameNet, frameId.Value, "Donor")!.Value;
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_FRAME_ELEMENT")
            && a.SubjectId == frameId.Value && a.ObjectId == donorRole && a.ContextId is null);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_FEATURE")
            && a.SubjectId == donorRole && a.ObjectId == coreCtx && a.ContextId is null);
    }

    [Fact]
    public async Task Fulltext_Targets_Preserve_Exact_Span_And_Occurrence()
    {
        const string fulltextXml = """
<?xml version="1.0" encoding="UTF-8"?>
<fullTextAnnotation xmlns="http://framenet.icsi.berkeley.edu">
  <sentence ID="77">
    <text>bank bank</text>
    <annotationSet ID="100" frameName="Commerce">
      <layer name="Target"><label name="Target" start="0" end="3"/></layer>
    </annotationSet>
    <annotationSet ID="101" frameName="Natural_features">
      <layer name="Target"><label name="Target" start="5" end="8"/></layer>
    </annotationSet>
  </sentence>
</fullTextAnnotation>
""";

        string dir = Path.Combine(Path.GetTempPath(), "fn-span-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "fulltext"));
        string path = Path.Combine(dir, "fulltext", "sample.xml");
        await File.WriteAllTextAsync(path, fulltextXml);
        try
        {
            var parsed = new List<FrameNetDecomposer.FulltextAnno>();
            await foreach (var ann in FrameNetDecomposer.ParseFulltextAsync(
                               path, "framenet/fulltext/sample.xml", CancellationToken.None))
                parsed.Add(ann);

            Assert.Collection(parsed,
                a =>
                {
                    Assert.Equal((0, 3), (a.TargetStart, a.TargetEnd));
                    Assert.Equal("77", a.SentenceReference);
                    Assert.Equal("100", a.AnnotationReference);
                },
                a =>
                {
                    Assert.Equal((5, 8), (a.TargetStart, a.TargetEnd));
                    Assert.Equal("101", a.AnnotationReference);
                });

            var dec = new FrameNetDecomposer();
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var changes = new List<SubstrateChange>();
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                changes.Add(change);

            var evokes = changes.SelectMany(c => c.Attestations)
                .Where(a => a.TypeId == RelationTypeRegistry.RelationTypeId("EVOKES_FRAME"))
                .ToList();
            Assert.Equal(2, evokes.Count);
            Assert.Equal(2, evokes.Select(a => a.SubjectId).Distinct().Count());
            Assert.Equal(2, evokes.Select(a => a.ContextId).Distinct().Count());
            Assert.DoesNotContain(evokes, a => a.SubjectId == ContentEmitter.RootId("bank"));

            Hash128 sentenceId = ContentEmitter.RootId("bank bank")!.Value;
            Hash128 targetId = ContentEmitter.RootId("bank")!.Value;
            Hash128 firstSingleSpanId = Hash128.Merkle(EntityTier.Document,
            [
                FrameNetDecomposer.AnnotationSchemaId,
                sentenceId,
                FrameNetDecomposer.OffsetId(0),
                FrameNetDecomposer.OffsetId(3),
                targetId,
            ]);
            Assert.Contains(evokes, a => a.SubjectId == firstSingleSpanId);

            var annotationIds = changes.SelectMany(c => c.Entities)
                .Where(e => e.TypeId == EntityTypeRegistry.FrameNetAnnotation)
                .Select(e => e.Id)
                .ToHashSet();
            Assert.Equal(2, annotationIds.Count);
            Assert.All(evokes, a => Assert.Contains(a.SubjectId, annotationIds));

            var annotationPhysicalities = changes.SelectMany(c => c.Physicalities)
                .Where(p => annotationIds.Contains(p.EntityId))
                .ToList();
            Assert.Equal(2, annotationPhysicalities.Count);
            Assert.Contains(annotationPhysicalities, p =>
                Trajectory.Constituents(p.TrajectoryXyzm!).Contains(FrameNetDecomposer.OffsetId(0))
                && Trajectory.Constituents(p.TrajectoryXyzm!).Contains(FrameNetDecomposer.OffsetId(3)));
            Assert.Contains(annotationPhysicalities, p =>
                Trajectory.Constituents(p.TrajectoryXyzm!).Contains(FrameNetDecomposer.OffsetId(5))
                && Trajectory.Constituents(p.TrajectoryXyzm!).Contains(FrameNetDecomposer.OffsetId(8)));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Fulltext_Discontinuous_Target_Preserves_Every_Ordered_Span()
    {
        const string fulltextXml = """
<?xml version="1.0" encoding="UTF-8"?>
<fullTextAnnotation xmlns="http://framenet.icsi.berkeley.edu">
  <sentence ID="88">
    <text>take the box apart</text>
    <annotationSet ID="200" frameName="Separating">
      <layer name="Target">
        <label name="Target" start="0" end="3"/>
        <label name="Target" start="13" end="17"/>
      </layer>
    </annotationSet>
  </sentence>
</fullTextAnnotation>
""";

        string dir = Path.Combine(Path.GetTempPath(), "fn-multispan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "fulltext"));
        string path = Path.Combine(dir, "fulltext", "sample.xml");
        await File.WriteAllTextAsync(path, fulltextXml);
        try
        {
            var parsed = new List<FrameNetDecomposer.FulltextAnno>();
            await foreach (var ann in FrameNetDecomposer.ParseFulltextAsync(
                               path, "framenet/fulltext/sample.xml", CancellationToken.None))
                parsed.Add(ann);

            var annotation = Assert.Single(parsed);
            Assert.Equal("take apart", annotation.TargetText);
            Assert.Equal((0, 3), (annotation.TargetStart, annotation.TargetEnd));
            Assert.Equal(
                [new FrameNetDecomposer.TargetSpan(0, 3), new FrameNetDecomposer.TargetSpan(13, 17)],
                annotation.TargetSpans);

            var dec = new FrameNetDecomposer();
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var changes = new List<SubstrateChange>();
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                changes.Add(change);

            var evokes = Assert.Single(
                changes.SelectMany(c => c.Attestations),
                a => a.TypeId == RelationTypeRegistry.RelationTypeId("EVOKES_FRAME"));
            var annotationPhysicality = Assert.Single(
                changes.SelectMany(c => c.Physicalities),
                p => p.EntityId == evokes.SubjectId);
            Assert.Equal(
            [
                FrameNetDecomposer.AnnotationSchemaId,
                ContentEmitter.RootId("take the box apart")!.Value,
                FrameNetDecomposer.OffsetId(0),
                FrameNetDecomposer.OffsetId(3),
                FrameNetDecomposer.OffsetId(13),
                FrameNetDecomposer.OffsetId(17),
                ContentEmitter.RootId("take apart")!.Value,
            ], Trajectory.Constituents(annotationPhysicality.TrajectoryXyzm!));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Bootstrap_Registers_Source_Types_And_RelationTypeEntities()
    {
        var dec = new FrameNetDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext(writer));

        Assert.Equal(2, writer.Captured.Count);
        var boot = writer.Captured[0];

        Assert.Contains(boot.Entities, e =>
            e.Id == FrameNetDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == EntityTypeRegistry.Id("FrameNet_Frame")
            && e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId);
        Assert.Contains(boot.Entities, e => e.Id == RelationTypeRegistry.RelationTypeId("EVOKES_FRAME"));
        Assert.Contains(boot.Attestations, a =>
            a.SubjectId == FrameNetDecomposer.Source
            && a.TypeId == BootstrapIntentBuilder.HasTrustClassTypeId
            && a.ObjectId == FrameNetDecomposer.TrustClass);

        Assert.Contains(writer.Captured[1].Entities, e =>
            e.Id == Hash128.OfCanonical("framenet/coreness/Core"));
    }

    [Fact]
    public async Task NonphysicalManagedEntities_AreGovernedProbationaryPos()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fn-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "frame"));
        await File.WriteAllTextAsync(Path.Combine(dir, "frame", "Giving.xml"), FrameXml);
        try
        {
            var dec = new FrameNetDecomposer();
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var nonphysical = new Dictionary<Hash128, Hash128>();
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
            {
                if (change.Metadata.SourceContentUnitName.StartsWith(
                        IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
                    continue;
                var placed = change.Physicalities.Select(p => p.EntityId).ToHashSet();
                foreach (var entity in change.Entities)
                    if (!placed.Contains(entity.Id))
                        nonphysical[entity.Id] = entity.TypeId;
            }

            Assert.Equal(3, nonphysical.Count);
            Assert.Equal(2, nonphysical.Values.Count(t => t == EntityTypeRegistry.FrameNetFe));
            Assert.Contains(nonphysical, entry =>
                entry.Key == SubstrateCanonicalIds.PosProbationary("framenet", "IDIO")
                && entry.Value == EntityTypeRegistry.Pos);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Inventory_SeparatesUnknownRecordTotalFromExactFileTotal()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fn-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "frame"));
        await File.WriteAllTextAsync(Path.Combine(dir, "frame", "Giving.xml"), FrameXml);
        try
        {
            var dec = new FrameNetDecomposer();
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var inventory = await dec.DescribeInputAsync(ctx, DecomposerOptions.Default);

            Assert.NotNull(inventory);
            Assert.Equal("records", inventory!.UnitType);
            Assert.Equal(0, inventory.TotalInputUnits);
            Assert.Equal(1, inventory.FileCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task SubframeOf_Emits_Parent_HasSubevent_Child()
    {
        var atts = await CollectAttestationsAsync();
        var givingId = CategoryAnchor.Id("Giving")!.Value;
        var parentId = CategoryAnchor.Id("Commerce_scenario")!.Value;
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SUBEVENT")
            && a.SubjectId == parentId
            && a.ObjectId == givingId);
    }

    [Fact]
    public async Task Relation_Targets_Are_Shared_Content_Anchors()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "frame"));
        await File.WriteAllTextAsync(Path.Combine(dir, "frame", "Giving.xml"), FrameXml);
        try
        {
            var dec = new FrameNetDecomposer();
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var referenced = new HashSet<Hash128>();
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                foreach (var a in change.Attestations)
                {
                    referenced.Add(a.SubjectId);
                    if (a.ObjectId is { } o) referenced.Add(o);
                }



            Assert.Contains(CategoryAnchor.Id("Giving")!.Value, referenced);
            foreach (var target in new[] { "Transfer", "Intentionally_act", "Commerce_scenario" })
                Assert.Contains(CategoryAnchor.Id(target)!.Value, referenced);



            var idioPos = SubstrateCanonicalIds.PosProbationary("framenet", "IDIO");
            Assert.Contains(idioPos, referenced);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static async Task<List<AttestationRow>> CollectAttestationsAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "frame"));
        await File.WriteAllTextAsync(Path.Combine(dir, "frame", "Giving.xml"), FrameXml);
        try
        {
            var dec = new FrameNetDecomposer();
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var atts = new List<AttestationRow>();
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
            {
                // Skip file-progress markers (period-boundary/ / file-failed/) — not
                // FrameNet testimony; their TypeId is HasLayerCompleted, not a highway
                // relation (GH #898).
                var unit = change.Metadata.SourceContentUnitName;
                if (unit.StartsWith(IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal)
                    || unit.StartsWith(IngestBatchPipeline.FileFailedUnitPrefix, StringComparison.Ordinal))
                    continue;
                atts.AddRange(change.Attestations.ToArray());
            }
            return atts;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }


    // The count FrameNet states must arrive verbatim, not be recomputed from how many
    // times a pattern string happens to appear in the file.
    [Fact]
    public void ValencePattern_CarriesTheCorpusStatedTotal()
    {
        const string xml = """
<?xml version="1.0" encoding="UTF-8"?>
<lexUnit status="FN1_Sent" POS="V" name="copy.v" ID="11" frame="Duplication" xmlns="http://framenet.icsi.berkeley.edu">
  <definition>COD: make a copy of.</definition>
  <lexeme POS="V" name="copy"/>
  <valences>
    <FERealization total="26">
      <FE name="Creator"/>
      <pattern total="24">
        <valenceUnit GF="Ext" PT="NP" FE="Creator"/>
        <valenceUnit GF="Obj" PT="NP" FE="Original"/>
      </pattern>
      <pattern total="2">
        <valenceUnit GF="Dep" PT="PP" FE="Creator"/>
        <valenceUnit GF="Obj" PT="NP" FE="Original"/>
      </pattern>
    </FERealization>
  </valences>
  <subCorpus name="V-test">
    <sentence ID="1">
      <text>She copied the file.</text>
      <annotationSet status="MANUAL" ID="99">
        <layer rank="1" name="Target">
          <label name="Target" start="4" end="10"/>
        </layer>
      </annotationSet>
    </sentence>
  </subCorpus>
</lexUnit>
""";
        var lu = FrameNetLuIngest.ParseLu(System.Xml.Linq.XDocument.Parse(xml));
        Assert.NotNull(lu);
        var totals = lu!.ValencePatterns
            .Where(p => p.Pattern.Contains(" + "))
            .Select(p => p.Total)
            .OrderByDescending(t => t)
            .ToList();
        Assert.Equal(new long[] { 24, 2 }, totals);
    }
}
