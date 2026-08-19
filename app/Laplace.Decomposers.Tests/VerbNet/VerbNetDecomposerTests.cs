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
  <MEMBER name="lend" verbnet_key="lend#1" wn="lend%2:40:00" grouping="lend.01 loan.01" fn_mapping="Commerce_buy" features=""/>
  <MEMBER name="give-back" verbnet_key="give-back#1" wn="" grouping="" fn_mapping="Locating Becoming_aware" features=""/>
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
   <SEMANTICS>
    <PRED value="cause"><ARGS>
     <ARG type="Event" value="E"/>
     <ARG type="ThemRole" value="?Agent"/>
     <ARG type="ThemRole" value="?Theme"/>
    </ARGS></PRED>
   </SEMANTICS>
  </FRAME>
 </FRAMES>
 <SUBCLASSES>
  <VNSUBCLASS ID="give-13.1-1">
   <MEMBERS>
    <MEMBER name="sell" verbnet_key="sell#1" wn="sell%2:40:00 sell%2:40:01" grouping="sell.01" fn_mapping="None" features=""/>
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

        var classId = AnchorAdmission.Id(
            SourceEntityIdConventions.NumericVerbNetClassId("give-13.1"),
            EntityTypeRegistry.VerbNetClass);
        Assert.NotNull(lendId);
        Assert.NotNull(classId);
        var memberId = LexicalMemberAnchor.Id(
            LexicalMemberIdentityKind.VerbNet, classId!.Value, "lend#1");
        Assert.NotNull(memberId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("MEMBER_OF_VERBNET_CLASS")
            && a.SubjectId == memberId!.Value && a.ObjectId == classId.Value);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_NAME_ALIAS")
            && a.SubjectId == memberId.Value && a.ObjectId == lendId!.Value);
        Assert.DoesNotContain(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("MEMBER_OF_VERBNET_CLASS")
            && a.SubjectId == lendId!.Value && a.ObjectId == classId!.Value);

        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("IS_TYPED_AS")
            && a.SubjectId == classId!.Value
            && a.ObjectId == EntityTypeRegistry.Id("VerbNet_Class"));

        var subId = AnchorAdmission.Id(
            SourceEntityIdConventions.NumericVerbNetClassId("give-13.1-1"),
            EntityTypeRegistry.VerbNetClass);
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
        Hash128 classId = AnchorAdmission.Id(
            SourceEntityIdConventions.NumericVerbNetClassId("give-13.1"),
            EntityTypeRegistry.VerbNetClass)!.Value;
        Hash128 memberId = LexicalMemberAnchor.Id(
            LexicalMemberIdentityKind.VerbNet, classId, "lend#1")!.Value;

        var senseId = SenseAnchor.Id("lend%2:40:00");
        Assert.NotNull(senseId);
        Assert.NotNull(lendId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO")
            && (a.SubjectId == memberId || a.ObjectId == memberId)
            && (a.SubjectId == senseId!.Value || a.ObjectId == senseId!.Value));
        Assert.DoesNotContain(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO")
            && (a.SubjectId == lendId!.Value || a.ObjectId == lendId.Value)
            && (a.SubjectId == senseId.Value || a.ObjectId == senseId.Value));
    }

    [Fact]
    public async Task Member_FnMapping_Emits_Each_Frame_And_Drops_None_Sentinel()
    {
        var atts = await CollectAttestationsAsync();
        Hash128 classId = AnchorAdmission.Id(
            SourceEntityIdConventions.NumericVerbNetClassId("give-13.1"),
            EntityTypeRegistry.VerbNetClass)!.Value;
        Hash128 lend = LexicalMemberAnchor.Id(
            LexicalMemberIdentityKind.VerbNet, classId, "lend#1")!.Value;
        Hash128 giveBack = LexicalMemberAnchor.Id(
            LexicalMemberIdentityKind.VerbNet, classId, "give-back#1")!.Value;
        Hash128 commerceBuy = ContentEmitter.RootId("Commerce_buy")!.Value;
        Hash128 locating = ContentEmitter.RootId("Locating")!.Value;
        Hash128 becomingAware = ContentEmitter.RootId("Becoming_aware")!.Value;
        Hash128 none = ContentEmitter.RootId("None")!.Value;
        Hash128 evokes = RelationTypeRegistry.RelationTypeId("EVOKES_FRAME");
        Hash128 lendSurface = ContentEmitter.RootId("lend")!.Value;

        Assert.Contains(atts, a =>
            a.SubjectId == lend && a.TypeId == evokes && a.ObjectId == commerceBuy);
        Assert.Contains(atts, a =>
            a.SubjectId == giveBack && a.TypeId == evokes && a.ObjectId == locating);
        Assert.Contains(atts, a =>
            a.SubjectId == giveBack && a.TypeId == evokes && a.ObjectId == becomingAware);
        Assert.DoesNotContain(atts, a =>
            a.SubjectId == lendSurface && a.TypeId == evokes);
        Assert.DoesNotContain(atts, a =>
            a.TypeId == evokes && a.ObjectId == none);
    }

    [Fact]
    public async Task Member_PropBankGrouping_Emits_Each_Roleset_From_Member_Grain()
    {
        var atts = await CollectAttestationsAsync();
        Hash128 classId = AnchorAdmission.Id(
            SourceEntityIdConventions.NumericVerbNetClassId("give-13.1"),
            EntityTypeRegistry.VerbNetClass)!.Value;
        Hash128 memberId = LexicalMemberAnchor.Id(
            LexicalMemberIdentityKind.VerbNet, classId, "lend#1")!.Value;
        Hash128 lendRoleset = AnchorAdmission.Id(
            "lend.01", EntityTypeRegistry.PropBankRoleset)!.Value;
        Hash128 loanRoleset = AnchorAdmission.Id(
            "loan.01", EntityTypeRegistry.PropBankRoleset)!.Value;
        Hash128 corresponds = RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO");

        Assert.Contains(atts, a => a.TypeId == corresponds
            && (a.SubjectId == memberId || a.ObjectId == memberId)
            && (a.SubjectId == lendRoleset || a.ObjectId == lendRoleset));
        Assert.Contains(atts, a => a.TypeId == corresponds
            && (a.SubjectId == memberId || a.ObjectId == memberId)
            && (a.SubjectId == loanRoleset || a.ObjectId == loanRoleset));
    }

    [Fact]
    public void MemberIdentity_Is_ClassBound_And_Does_Not_Use_Lemma_ContentIdentity()
    {
        Hash128 classA = AnchorAdmission.Id("13.1", EntityTypeRegistry.VerbNetClass)!.Value;
        Hash128 classB = AnchorAdmission.Id("13.2", EntityTypeRegistry.VerbNetClass)!.Value;
        Hash128 memberA = LexicalMemberAnchor.Id(
            LexicalMemberIdentityKind.VerbNet, classA, "lend#1")!.Value;
        Hash128 memberB = LexicalMemberAnchor.Id(
            LexicalMemberIdentityKind.VerbNet, classB, "lend#1")!.Value;

        Assert.NotEqual(memberA, memberB);
        Assert.NotEqual(ContentEmitter.RootId("lend")!.Value, memberA);
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
    public async Task ThematicRole_IsBoundToItsVerbNetClass_NotTheSharedLabel()
    {
        var atts = await CollectAttestationsAsync();
        Hash128 classId = AnchorAdmission.Id(
            SourceEntityIdConventions.NumericVerbNetClassId("give-13.1"),
            EntityTypeRegistry.VerbNetClass)!.Value;
        Hash128 agentRole = RoleAnchor.Id(
            RoleIdentityKind.VerbNet, classId, "Agent")!.Value;
        var b = new SubstrateChangeBuilder(VerbNetDecomposer.Source, "fixture", null);
        Hash128 agentLabel = ContentEmitter.Emit(b, "Agent", VerbNetDecomposer.Source)!.Value;

        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_THEMATIC_ROLE")
            && a.SubjectId == classId && a.ObjectId == agentRole && a.ContextId is null);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_NAME_ALIAS")
            && a.SubjectId == agentRole && a.ObjectId == agentLabel);
        Assert.DoesNotContain(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_THEMATIC_ROLE")
            && a.SubjectId == classId && a.ObjectId == agentLabel);
    }

    [Fact]
    public async Task SemanticPredicateOccurrence_OwnsItsClassBoundArguments()
    {
        var atts = await CollectAttestationsAsync();
        Hash128 classId = AnchorAdmission.Id(
            SourceEntityIdConventions.NumericVerbNetClassId("give-13.1"),
            EntityTypeRegistry.VerbNetClass)!.Value;
        Hash128 causeLabel = ContentEmitter.RootId("cause")!.Value;
        var arguments = new[]
        {
            new SemanticPredicateArgument("Event", "E"),
            new SemanticPredicateArgument("ThemRole", "?Agent"),
            new SemanticPredicateArgument("ThemRole", "?Theme"),
        };
        Hash128 predicate = SemanticPredicateAnchor.Id(
            SemanticPredicateIdentityKind.VerbNet, classId,
            frameOrdinal: 0, predicateOrdinal: 0, labelId: causeLabel, arguments: arguments);
        Assert.NotEqual(predicate, SemanticPredicateAnchor.Id(
            SemanticPredicateIdentityKind.VerbNet, classId,
            frameOrdinal: 1, predicateOrdinal: 0, labelId: causeLabel, arguments: arguments));
        Assert.NotEqual(predicate, SemanticPredicateAnchor.Id(
            SemanticPredicateIdentityKind.VerbNet, classId,
            frameOrdinal: 0, predicateOrdinal: 0, labelId: causeLabel,
            arguments: arguments[..^1]));
        Hash128 agentRole = RoleAnchor.Id(
            RoleIdentityKind.VerbNet, classId, "Agent")!.Value;

        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("ENTAILS")
            && a.SubjectId == classId && a.ObjectId == predicate);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_NAME_ALIAS")
            && a.SubjectId == predicate && a.ObjectId == causeLabel);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SEMANTIC_ROLE")
            && a.SubjectId == predicate && a.ObjectId == agentRole);
        Assert.DoesNotContain(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SEMANTIC_ROLE")
            && a.SubjectId == causeLabel);
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
        Assert.Contains(boot.Entities, e =>
            e.Id == EntityTypeRegistry.Id("VerbNet_Role")
            && e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == EntityTypeRegistry.Id("VerbNet_Member")
            && e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == EntityTypeRegistry.Id("VerbNet_Predicate")
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
            {
                if (change.Metadata.SourceContentUnitName.StartsWith(
                        IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
                    continue;
                atts.AddRange(change.Attestations.ToArray());
            }
            return atts;
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

}
