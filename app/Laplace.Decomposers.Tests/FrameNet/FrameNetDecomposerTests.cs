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
        CodepointPerfcache.Load(ResolvePerfcacheBlob());
    }

    private static string ResolvePerfcacheBlob()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory.EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin",
                                                   SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        throw new InvalidOperationException("perf-cache blob not found; build the engine or set LAPLACE_PERFCACHE_BIN.");
    }

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
        Assert.Contains(lu.ValencePatterns, p => p.Contains("Creator"));
        Assert.Single(lu.Sentences);
        Assert.Equal("copied", lu.Sentences[0].TargetText);
    }

    [Fact]
    public async Task Attestations_Use_RegistryRouted_Canonical_Type_Ids()
    {
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
        var donorId = CategoryAnchor.Id("Donor");
        var placeId = CategoryAnchor.Id("Place");
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
    public async Task EvokesFrame_Targets_Frame_Meta_And_CoreType_Rides_Context()
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
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_FRAME_ELEMENT") && a.ContextId == coreCtx);
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



            var idioPos = Hash128.OfCanonical("substrate/pos/probationary/framenet/IDIO/v1");
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
                atts.AddRange(change.Attestations.ToArray());
            return atts;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

}
