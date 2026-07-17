using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.VerbNet.Tests;

public sealed class VerbNetDecomposerTests
{
    static VerbNetDecomposerTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolvePerfcacheBlob());
    }

    private static string ResolvePerfcacheBlob() => TestInstall.ResolvePerfcacheOrThrow();

    private const string ClassXml = """
<VNCLASS ID="give-13.1">
 <MEMBERS>
  <MEMBER name="lend" verbnet_key="lend#1" wn="lend%2:40:00" features=""/>
  <MEMBER name="give-back" verbnet_key="give-back#1" wn="" features=""/>
 </MEMBERS>
 <THEMROLES>
  <THEMROLE type="Agent"><SELRESTRS logic="or"><SELRESTR Value="+" type="animate"/></SELRESTRS></THEMROLE>
  <THEMROLE type="Theme"><SELRESTRS/></THEMROLE>
 </THEMROLES>
 <FRAMES>
  <FRAME>
   <DESCRIPTION descriptionNumber="0.2" primary="NP V NP PP.recipient" secondary="NP-PP" xtag=""/>
   <EXAMPLES><EXAMPLE>They lent a bicycle to me.</EXAMPLE></EXAMPLES>
   <SYNTAX><NP value="Agent"/></SYNTAX>
  </FRAME>
 </FRAMES>
 <SUBCLASSES>
  <VNSUBCLASS ID="give-13.1-1">
   <MEMBERS>
    <MEMBER name="sell" verbnet_key="sell#1" wn="sell%2:40:00 sell%2:40:01" features=""/>
   </MEMBERS>
   <THEMROLES>
    <THEMROLE type="Asset"><SELRESTRS/></THEMROLE>
   </THEMROLES>
   <FRAMES/>
   <SUBCLASSES/>
  </VNSUBCLASS>
 </SUBCLASSES>
</VNCLASS>
""";

    [Fact]
    public async Task Attestations_Use_RegistryRouted_Canonical_Type_Ids()
    {
        var atts = await CollectAttestationsAsync();

        var canonical = new HashSet<Hash128>(RelationTypeRegistry.AllCanonical().Select(k => k.Id));
        Assert.All(atts, a => Assert.Contains(a.TypeId, canonical));

        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("IS_A"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("MEMBER_OF_VERBNET_CLASS"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_THEMATIC_ROLE"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_VERB_FRAME"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_EXAMPLE"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO"));
    }

    [Fact]
    public async Task Member_MemberOfVerbNetClass_And_Subclass_IsA_ParentClass()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(VerbNetDecomposer.Source, "fixture", null);

        var lendId = ContentEmitter.Emit(b, "lend", VerbNetDecomposer.Source);

        var classId = CategoryAnchor.Id(SourceEntityIdConventions.NumericVerbNetClassId("give-13.1"));
        Assert.NotNull(lendId);
        Assert.NotNull(classId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("MEMBER_OF_VERBNET_CLASS")
            && a.SubjectId == lendId!.Value && a.ObjectId == classId!.Value);
        Assert.DoesNotContain(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("IS_A")
            && a.SubjectId == lendId!.Value && a.ObjectId == classId!.Value);

        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("IS_TYPED_AS")
            && a.SubjectId == classId!.Value
            && a.ObjectId == EntityTypeRegistry.Id("VerbNet_Class"));

        var subId = CategoryAnchor.Id(SourceEntityIdConventions.NumericVerbNetClassId("give-13.1-1"));
        Assert.NotNull(subId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("IS_A")
            && a.SubjectId == subId!.Value && a.ObjectId == classId!.Value);
    }

    [Fact]
    public async Task Member_WnSenseKeys_Correspond_To_WordNet_Sense_Entities()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(VerbNetDecomposer.Source, "fixture", null);
        var lendId = ContentEmitter.Emit(b, "lend", VerbNetDecomposer.Source);



        var senseId = CategoryAnchor.Id("lend%2:40:00");
        Assert.NotNull(senseId);
        Assert.NotNull(lendId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO")
            && (a.SubjectId == lendId!.Value || a.ObjectId == lendId!.Value)
            && (a.SubjectId == senseId!.Value || a.ObjectId == senseId!.Value));
    }

    [Fact]
    public void NormalizeSenseKey_Canonicalizes_To_ThreeFields_And_Strips_Markers()
    {


        Assert.Equal("give%2:40:03", SourceEntityIdConventions.NormalizeSenseKey("give%2:40:03"));
        Assert.Equal("give%2:40:03", SourceEntityIdConventions.NormalizeSenseKey("give%2:40:03::"));
        Assert.Equal("ache%2:37:06", SourceEntityIdConventions.NormalizeSenseKey("?ache%2:37:06"));
        Assert.Null(SourceEntityIdConventions.NormalizeSenseKey("notasensekey"));
    }

    [Fact]
    public void NumericClassId_Strips_Lemma_Prefix()
    {
        // Canonical helper (wrappers deleted); cross-source law lives in
        // SourceEntityIdConventionsTests — these cases guard VerbNet-specific shapes.
        Assert.Equal("13.1", SourceEntityIdConventions.NumericVerbNetClassId("give-13.1"));
        Assert.Equal("13.1-1", SourceEntityIdConventions.NumericVerbNetClassId("give-13.1-1"));
        Assert.Equal("10.11-2", SourceEntityIdConventions.NumericVerbNetClassId("resign-10.11-2"));
        Assert.Equal("13.1", SourceEntityIdConventions.NumericVerbNetClassId("13.1"));
        Assert.Equal("45.8", SourceEntityIdConventions.NumericVerbNetClassId("break_down-45.8"));
    }

    [Fact]
    public async Task Bootstrap_Registers_Source_Types_And_RelationTypeEntities()
    {
        var dec = new VerbNetDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext(writer));

        Assert.Single(writer.Captured);
        var boot = writer.Captured[0];
        Assert.Contains(boot.Entities, e =>
            e.Id == VerbNetDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == EntityTypeRegistry.Id("VerbNet_Class")
            && e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId);
        Assert.Contains(boot.Entities, e => e.Id == RelationTypeRegistry.RelationTypeId("HAS_THEMATIC_ROLE"));
        Assert.Contains(boot.Entities, e => e.Id == RelationTypeRegistry.RelationTypeId("MEMBER_OF_VERBNET_CLASS"));
        Assert.Contains(boot.Attestations, a =>
            a.SubjectId == VerbNetDecomposer.Source
            && a.TypeId == BootstrapIntentBuilder.HasTrustClassTypeId
            && a.ObjectId == VerbNetDecomposer.TrustClass);
    }

    private static async Task<List<AttestationRow>> CollectAttestationsAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "verbnet3.4"));
        await File.WriteAllTextAsync(Path.Combine(dir, "verbnet3.4", "give-13.1.xml"), ClassXml);
        try
        {
            var dec = new VerbNetDecomposer();
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var atts = new List<AttestationRow>();
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                atts.AddRange(change.Attestations.ToArray());
            return atts;
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

}
