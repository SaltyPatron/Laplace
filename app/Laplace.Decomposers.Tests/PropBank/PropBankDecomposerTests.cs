using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.PropBank.Tests;

public sealed class PropBankDecomposerTests
{
    static PropBankDecomposerTests() => CodepointPerfcache.Load(ResolvePerfcacheBlob());

    private static string ResolvePerfcacheBlob() => TestInstall.ResolvePerfcacheOrThrow();

    private const string FramesetXml = """
<frameset>
  <predicate lemma="give">
    <roleset id="give.01" name="transfer">
      <roles>
        <role descr="giver" f="PAG" n="0">
          <rolelinks>
            <rolelink class="give-13.1-1" resource="VerbNet" version="verbnet3.4">agent</rolelink>
            <rolelink class="Giving" resource="FrameNet" version="1.7">Donor</rolelink>
          </rolelinks>
        </role>
        <role descr="thing given" f="PPT" n="1">
          <rolelinks>
            <rolelink class="give-13.1-1" resource="VerbNet" version="verbnet3.4">theme</rolelink>
          </rolelinks>
        </role>
        <role descr="entity given to" f="GOL" n="2"/>
      </roles>
      <example name="give-v: double object" src="">
        <text>The executives gave the chefs a standing ovation .</text>
      </example>
    </roleset>
  </predicate>
</frameset>
""";



    private const string TopLevelFramesetXml = """
<frameset>
  <predicate lemma="be-destined-for">
    <roleset id="be-destined-for.91" name="91-rolesets for AMR/UMR-specific roles">
      <roles>
        <role descr="thing destined" f="PPT" n="1"/>
        <role descr="destination" f="GOL" n="2"/>
      </roles>
      <example name="be-destined-for.91: AMR reification" src="">
        <text>The package is destined for the warehouse .</text>
      </example>
    </roleset>
  </predicate>
</frameset>
""";

    [Fact]
    public async Task Attestations_Use_RegistryRouted_Canonical_Type_Ids()
    {
        var atts = await CollectAttestationsAsync();
        var canonical = new HashSet<Hash128>(RelationTypeRegistry.AllCanonical().Select(k => k.Id));
        Assert.All(atts, a => Assert.Contains(a.TypeId, canonical));

        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SENSE"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_DEFINITION"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SEMANTIC_ROLE"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_EXAMPLE"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO"));
    }

    [Fact]
    public async Task Predicate_HasSense_Roleset_Same_Arena_As_WordNet()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(PropBankDecomposer.Source, "fixture", null);
        var giveId = ContentEmitter.Emit(b, "give", PropBankDecomposer.Source);
        var rsId = CategoryAnchor.Id("give.01");
        Assert.NotNull(rsId);
        Assert.NotNull(giveId);

        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SENSE")
            && a.SubjectId == giveId!.Value && a.ObjectId == rsId!.Value);
    }

    [Fact]
    public async Task SemanticRole_Carries_ArgNumber_As_Ordinal_Context()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(PropBankDecomposer.Source, "fixture", null);
        var giverId = ContentEmitter.Emit(b, "giver", PropBankDecomposer.Source);
        var rsId = CategoryAnchor.Id("give.01");

        var ord0 = PropBankDecomposer.OrdinalId("0");
        Assert.Equal(Hash128.OfCanonical("ordinal/0/v1"), ord0);
        Assert.NotNull(rsId);
        Assert.NotNull(giverId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SEMANTIC_ROLE")
            && a.SubjectId == rsId!.Value && a.ObjectId == giverId!.Value && a.ContextId == ord0);
    }

    [Fact]
    public async Task RoleLink_Corresponds_To_VerbNet_Class_And_ThetaRole()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(PropBankDecomposer.Source, "fixture", null);
        var rsId = CategoryAnchor.Id("give.01");


        var vnId = CategoryAnchor.Id(SourceEntityIdConventions.NumericVerbNetClassId("give-13.1-1"));
        Assert.NotNull(rsId);
        Assert.NotNull(vnId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO")
            && (a.SubjectId == rsId!.Value || a.ObjectId == rsId!.Value)
            && (a.SubjectId == vnId!.Value || a.ObjectId == vnId!.Value));

        var giverId = ContentEmitter.Emit(b, "giver", PropBankDecomposer.Source);
        var agentId = ContentEmitter.Emit(b, "agent", PropBankDecomposer.Source);
        Assert.NotNull(giverId);
        Assert.NotNull(agentId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("ROLE_CORRESPONDS_TO")
            && a.ContextId == vnId!.Value
            && (a.SubjectId == giverId!.Value || a.ObjectId == giverId!.Value)
            && (a.SubjectId == agentId!.Value || a.ObjectId == agentId!.Value));
    }

    [Fact]
    public async Task RoleLink_Corresponds_To_FrameNet_Frame_And_FrameElement()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(PropBankDecomposer.Source, "fixture", null);
        var rsId = CategoryAnchor.Id("give.01");



        var fnId = CategoryAnchor.Id("Giving");
        Assert.NotNull(rsId);
        Assert.NotNull(fnId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO")
            && (a.SubjectId == rsId!.Value || a.ObjectId == rsId!.Value)
            && (a.SubjectId == fnId!.Value || a.ObjectId == fnId!.Value));

        var giverId = ContentEmitter.Emit(b, "giver", PropBankDecomposer.Source);
        var donorId = ContentEmitter.Emit(b, "Donor", PropBankDecomposer.Source);
        Assert.NotNull(giverId);
        Assert.NotNull(donorId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("ROLE_CORRESPONDS_TO")
            && a.ContextId == fnId!.Value
            && (a.SubjectId == giverId!.Value || a.ObjectId == giverId!.Value)
            && (a.SubjectId == donorId!.Value || a.ObjectId == donorId!.Value));
    }

    [Fact]
    public async Task Role_Carries_FunctionTag_As_Feature()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(PropBankDecomposer.Source, "fixture", null);
        var giverId = ContentEmitter.Emit(b, "giver", PropBankDecomposer.Source);
        var pagId = ContentEmitter.Emit(b, "PAG", PropBankDecomposer.Source);
        Assert.NotNull(giverId);
        Assert.NotNull(pagId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_FEATURE")
            && a.SubjectId == giverId!.Value && a.ObjectId == pagId!.Value);
    }

    [Fact]
    public async Task DecomposeAsync_Includes_TopLevel_Frameset_File_Alongside_FramesDir()
    {



        string root = Path.Combine(Path.GetTempPath(), "pb-test-" + Guid.NewGuid().ToString("N"));
        string dist = Path.Combine(root, "propbank-frames-main");
        Directory.CreateDirectory(Path.Combine(dist, "frames"));
        await File.WriteAllTextAsync(Path.Combine(dist, "frames", "give.xml"), FramesetXml);
        await File.WriteAllTextAsync(Path.Combine(dist, "AMR-UMR-91-rolesets.xml"), TopLevelFramesetXml);

        await File.WriteAllTextAsync(Path.Combine(dist, "README.md"), "not xml");
        try
        {
            var dec = new PropBankDecomposer();
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = root };
            var atts = new List<AttestationRow>();
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                atts.AddRange(change.Attestations.ToArray());

            var giveId = ContentEmitter.Emit(
                new SubstrateChangeBuilder(PropBankDecomposer.Source, "fixture", null),
                "give", PropBankDecomposer.Source);
            var beDestinedForId = CategoryAnchor.Id("be-destined-for.91");
            Assert.NotNull(giveId);
            Assert.NotNull(beDestinedForId);


            Assert.Contains(atts, a =>
                a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SENSE")
                && a.SubjectId == giveId!.Value && a.ObjectId == CategoryAnchor.Id("give.01")!.Value);



            var beDestinedForLemmaId = ContentEmitter.Emit(
                new SubstrateChangeBuilder(PropBankDecomposer.Source, "fixture", null),
                "be-destined-for", PropBankDecomposer.Source);
            Assert.NotNull(beDestinedForLemmaId);
            Assert.Contains(atts, a =>
                a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SENSE")
                && a.SubjectId == beDestinedForLemmaId!.Value && a.ObjectId == beDestinedForId!.Value);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Bootstrap_Registers_Source_Types_And_RelationTypeEntities()
    {
        var dec = new PropBankDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext(writer));

        Assert.Single(writer.Captured);
        var boot = writer.Captured[0];
        Assert.Contains(boot.Entities, e =>
            e.Id == PropBankDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == EntityTypeRegistry.Id("PropBank_Roleset"));
        Assert.Contains(boot.Entities, e => e.Id == RelationTypeRegistry.RelationTypeId("HAS_SEMANTIC_ROLE"));
        Assert.Contains(boot.Attestations, a =>
            a.SubjectId == PropBankDecomposer.Source
            && a.TypeId == BootstrapIntentBuilder.HasTrustClassTypeId
            && a.ObjectId == PropBankDecomposer.TrustClass);
    }

    private static async Task<List<AttestationRow>> CollectAttestationsAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "frames"));
        await File.WriteAllTextAsync(Path.Combine(dir, "frames", "give.xml"), FramesetXml);
        try
        {
            var dec = new PropBankDecomposer();
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var atts = new List<AttestationRow>();
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                atts.AddRange(change.Attestations.ToArray());
            return atts;
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

}
