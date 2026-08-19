using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.SemLink.Tests;

public sealed class SemLinkDecomposerTests
{
    static SemLinkDecomposerTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolvePerfcacheBlob());
    }

    private static string ResolvePerfcacheBlob() => TestInstall.ResolvePerfcacheOrThrow();

    private const string PbVnJson =
        """{"give.01": {"13.1-1": {"ARG0": "agent", "ARG1": "theme"}}, "abdicate.01": {"10.11-2": {}}}""";

    private const string VnFnJson =
        """{"13.1-1-give": ["Giving", "Commerce_sell"], "21.1-1-chip": ["Cause_to_fragment"]}""";

    private const string PbWnJson =
        """{"give.01": "30-02244956-v", "speak.01": "30-00941990-v"}""";

    private const string PredicateMatrixHeader =
        "1_ID_LANG\t1_ID_POS\t2_ID_PRED\t3_ID_ROLE\t4_VN_CLASS\t5_VN_CLASS_NUMBER\t6_VN_SUBCLASS\t7_VN_SUBCLASS_NUMBER\t8_VN_LEMA\t9_VN_ROLE\t10_WN_SENSE\t11_MCR_iliOffset\t12_FN_FRAME\t13_FN_LE\t14_FN_FRAME_ELEMENT\t15_PB_ROLESET\t16_PB_ARG\t18_MCR_BC\t19_MCR_DOMAIN\t20_MCR_SUMO\t21_MCR_TO\t22_MCR_LEXNAME\t23_MCR_BLC\t24_WN_SENSEFREC\t25_WN_SYNSET_REL_NUM\t26_ESO_CLASS\t27_ESO_ROLE";

    private const string PredicateMatrixRow =
        "id:eng\tid:v\tid:give.01\tid:0\tvn:give\t13.1\t13.1-1\t1\tgive\tvn:Agent\twn:give%2:40:03\tili-30-02244956-v\tfn:Giving\tfn:give.v\tfn:Donor\tpb:give.01\tpb:0\tmcr:1\tmcr:factotum\tmcr:Motion\tmcr:Dynamic;Location\tmcr:motion\tmcr:ili-30-01835496-v\twn:21\twn:007\teso:Transfer\teso:source";

    [Fact]
    public async Task Attestations_Are_Only_RegistryRouted_CorrespondsTo()
    {
        var atts = await CollectAttestationsAsync();
        var canonical = new HashSet<Hash128>(RelationTypeRegistry.AllCanonical().Select(k => k.Id));
        Assert.All(atts, a => Assert.Contains(a.TypeId, canonical));
        Assert.All(atts, a => Assert.Equal(RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO"), a.TypeId));
        Assert.NotEmpty(atts);
    }

    [Fact]
    public async Task PbVn_Maps_Roleset_To_VerbNet_Class_With_Shared_Ids()
    {
        var atts = await CollectAttestationsAsync();
        var rsId = AnchorAdmission.Id("give.01", EntityTypeRegistry.PropBankRoleset)!.Value;
        var vnId = AnchorAdmission.Id("13.1-1", EntityTypeRegistry.VerbNetClass)!.Value;
        Assert.Contains(atts, a =>
            (a.SubjectId == rsId && a.ObjectId == vnId) ||
            (a.SubjectId == vnId && a.ObjectId == rsId));
    }

    [Fact]
    public async Task PbVn_Role_Level_Maps_ParentBoundRoles_WithoutSemanticContext()
    {
        var (_, allAtts) = await CollectAllAsync();
        var atts = allAtts.Where(
            a => a.TypeId == RelationTypeRegistry.RelationTypeId("ROLE_CORRESPONDS_TO")).ToList();
        var rsId = AnchorAdmission.Id("give.01", EntityTypeRegistry.PropBankRoleset)!.Value;
        var vnId = AnchorAdmission.Id("13.1-1", EntityTypeRegistry.VerbNetClass)!.Value;
        var argId = RoleAnchor.Id(RoleIdentityKind.PropBank, rsId, "ARG0")!.Value;
        var thetaId = RoleAnchor.Id(RoleIdentityKind.VerbNet, vnId, "agent")!.Value;
        Assert.Contains(atts, a =>
            a.ContextId is null
            && (a.SubjectId == argId || a.ObjectId == argId)
            && (a.SubjectId == thetaId || a.ObjectId == thetaId));
    }

    [Fact]
    public async Task PredicateMatrix_RoleMapping_BindsBothClassAndFrameIntoEndpoints()
    {
        var (_, atts) = await CollectPredicateMatrixAsync();
        Hash128 vnClass = AnchorAdmission.Id("13.1-1", EntityTypeRegistry.VerbNetClass)!.Value;
        Hash128 frame = AnchorAdmission.Id("Giving", EntityTypeRegistry.FrameNetFrame)!.Value;
        Hash128 vnRole = RoleAnchor.Id(RoleIdentityKind.VerbNet, vnClass, "Agent")!.Value;
        Hash128 frameRole = RoleAnchor.Id(RoleIdentityKind.FrameNet, frame, "Donor")!.Value;

        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("ROLE_CORRESPONDS_TO")
            && a.ContextId is null
            && (a.SubjectId == vnRole || a.ObjectId == vnRole)
            && (a.SubjectId == frameRole || a.ObjectId == frameRole));
    }

    [Fact]
    public async Task PredicateMatrix_PreservesMultilingualPredicateAndRoleIdentity()
    {
        var (entities, atts) = await CollectPredicateMatrixAsync();
        Hash128 predicate = PredicateMatrixIngest.PredicateId("eng", "v", "give.01")!.Value;
        Hash128 matrixRole = PredicateMatrixIngest.PredicateRoleId("eng", "v", "give.01", "0")!.Value;
        Hash128 roleset = AnchorAdmission.Id("give.01", EntityTypeRegistry.PropBankRoleset)!.Value;
        Hash128 propBankRole = RoleAnchor.Id(RoleIdentityKind.PropBank, roleset, "ARG0")!.Value;

        Assert.Contains(entities, e =>
            e.Id == predicate && e.TypeId == EntityTypeRegistry.PredicateMatrixPredicate);
        Assert.Contains(entities, e =>
            e.Id == matrixRole && e.TypeId == EntityTypeRegistry.PredicateMatrixRole);
        Assert.Contains(atts, a =>
            a.SubjectId == predicate
            && a.TypeId == PredicateMatrixSource.HasLanguageTypeId
            && a.ObjectId == LanguageReference.Resolve("eng"));
        Assert.Contains(atts, a =>
            a.TypeId == PredicateMatrixSource.RoleCorrespondsToTypeId
            && (a.SubjectId == matrixRole || a.ObjectId == matrixRole)
            && (a.SubjectId == propBankRole || a.ObjectId == propBankRole));
        Assert.NotEqual(
            predicate,
            PredicateMatrixIngest.PredicateId("spa", "v", "give.01")!.Value);
    }

    [Fact]
    public async Task PredicateMatrix_PreservesMcrAndEsoNativeFieldsWithoutTextAdmission()
    {
        var (entities, atts) = await CollectPredicateMatrixAsync();
        Assert.Contains(entities, e => e.TypeId == EntityTypeRegistry.McrDomain);
        Assert.Contains(entities, e => e.TypeId == EntityTypeRegistry.McrSumo);
        Assert.Contains(entities, e => e.TypeId == EntityTypeRegistry.McrTopOntology);
        Assert.Contains(entities, e => e.TypeId == EntityTypeRegistry.McrLexname);
        Assert.Contains(entities, e => e.TypeId == EntityTypeRegistry.EsoClass);
        Assert.Contains(entities, e => e.TypeId == EntityTypeRegistry.EsoRole);
        Assert.Contains(entities, e => e.TypeId == EntityTypeRegistry.PredicateMatrixAnnotationValue);

        Assert.Contains(atts, a => a.TypeId == PredicateMatrixSource.HasDomainTopicTypeId);
        Assert.Contains(atts, a => a.TypeId == PredicateMatrixSource.HasLexCategoryTypeId);
        Assert.Contains(atts, a => a.TypeId == PredicateMatrixSource.HasBaseConceptStatusTypeId);
        Assert.Contains(atts, a => a.TypeId == PredicateMatrixSource.HasSenseFrequencyTypeId);
        Assert.Contains(atts, a => a.TypeId == PredicateMatrixSource.HasSynsetRelationCountTypeId);
    }

    [Fact]
    public async Task PredicateMatrix_OneSourceRow_IsOnePipelineRecord_AndOneInputUnit()
    {
        string path = Path.Combine(Path.GetTempPath(), "pm-row-grain-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(
            path,
            PredicateMatrixHeader + Environment.NewLine
            + PredicateMatrixRow + Environment.NewLine
            + PredicateMatrixRow.Replace("id:give.01", "id:send.01", StringComparison.Ordinal) + Environment.NewLine);
        try
        {
            var records = new List<PredicateMatrixIngest.PredicateMatrixRecord>();
            await foreach (var record in PredicateMatrixIngest.EnumerateRecordsAsync(path, null, 1, default))
                records.Add(record);

            var only = Assert.Single(records);
            Assert.Equal(7, only.Edges.Length);
            Assert.Equal(2L, await PredicateMatrixIngest.EstimateRecordCountAsync(path, null, default));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task PredicateMatrix_Inventory_UsesTheSameAdmissionGrainAsExtraction()
    {
        string path = Path.Combine(Path.GetTempPath(), "pm-inventory-" + Guid.NewGuid().ToString("N") + ".txt");
        string spanish = PredicateMatrixRow.Replace("id:eng", "id:spa", StringComparison.Ordinal);
        string noun = PredicateMatrixRow.Replace("id:v", "id:n", StringComparison.Ordinal);
        string noSynset = PredicateMatrixRow.Replace("ili-30-02244956-v", "NULL", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            path,
            string.Join(Environment.NewLine, PredicateMatrixHeader, PredicateMatrixRow, spanish, noun, noSynset, ""));
        try
        {
            Assert.Equal(4L, await PredicateMatrixIngest.EstimateRecordCountAsync(path, null, default));
            Assert.Equal(1L, await PredicateMatrixIngest.EstimateRecordCountAsync(
                path, LanguageFilter.FromSpec("es"), default));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task SemLinkJson_Inventory_CountsTopLevelSourceRecords()
    {
        string path = Path.Combine(Path.GetTempPath(), "sl-json-inventory-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, PbVnJson);
        try
        {
            Assert.Equal(2L, await SemLinkJsonPairStream.CountRecordsAsync(path, default));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task SemLinkRoleInventory_CountsAdmittedMappings_NotPhysicalLines()
    {
        const string xml = """
<mappings>
  <vncls class="9.1" fnframe="Placing">
    <roles>
      <role fnrole="Goal" vnrole="Destination"/>
      <role fnrole="" vnrole="Theme"/>
    </roles>
  </vncls>
  <vncls class="" fnframe="Broken"><roles><role fnrole="X" vnrole="Y"/></roles></vncls>
</mappings>
""";
        string path = Path.Combine(Path.GetTempPath(), "sl-role-inventory-" + Guid.NewGuid().ToString("N") + ".xml");
        await File.WriteAllTextAsync(path, xml);
        try
        {
            Assert.Equal(1L, await SemLinkRoleMappingIngest.EstimateUnitCountAsync(path, default));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task PredicateMatrix_PackagingRepetition_DoesNotAmplifyWitnessCount()
    {
        var (_, atts) = await CollectPredicateMatrixAsync(PredicateMatrixRow, PredicateMatrixRow);
        Assert.NotEmpty(atts);
        Assert.All(atts, a => Assert.Equal(1, a.ObservationCount));
    }

    [Fact]
    public async Task VnFnRoleMapping_PreservesTheFrameOwnerFromTheSourceRow()
    {
        const string xml = """
<mappings>
  <vncls class="9.1" fnframe="Placing">
    <roles><role fnrole="Goal" vnrole="Destination"/></roles>
  </vncls>
</mappings>
""";
        string path = Path.Combine(Path.GetTempPath(), "vn-fn-role-" + Guid.NewGuid().ToString("N") + ".xml");
        await File.WriteAllTextAsync(path, xml);
        try
        {
            var records = new List<RoleCorrespondenceRecord>();
            await foreach (var record in SemLinkRoleMappingIngest.EnumerateRecordsAsync(path, default))
                records.Add(record);

            RoleCorrespondenceRecord mapping = Assert.Single(records);
            Assert.Equal("9.1", mapping.SubjectParentKey);
            Assert.Equal(EntityTypeRegistry.VerbNetClass, mapping.SubjectParentTypeId);
            Assert.Equal("Destination", mapping.SubjectRoleKey);
            Assert.Equal("Placing", mapping.ObjectParentKey);
            Assert.Equal(EntityTypeRegistry.FrameNetFrame, mapping.ObjectParentTypeId);
            Assert.Equal("Goal", mapping.ObjectRoleKey);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task VnFn_Maps_Class_To_FrameNet_Frame_With_Shared_Ids()
    {
        var atts = await CollectAttestationsAsync();
        var vnId = AnchorAdmission.Id("13.1-1", EntityTypeRegistry.VerbNetClass)!.Value;
        var fnId = CategoryAnchor.Id("Giving")!.Value;
        Assert.Contains(atts, a =>
            (a.SubjectId == vnId && a.ObjectId == fnId) ||
            (a.SubjectId == fnId && a.ObjectId == vnId));
    }

    [Fact]
    public void VnClassFromKey_Splits_Off_Member_Lemma()
    {
        Assert.Equal("26.5", SemLinkDecomposer.VnClassFromKey("26.5-shake"));
        Assert.Equal("21.1-1", SemLinkDecomposer.VnClassFromKey("21.1-1-chip"));
        Assert.Equal("13.1-1", SemLinkDecomposer.VnClassFromKey("13.1-1-give"));
        Assert.Equal("51.3.2-2", SemLinkDecomposer.VnClassFromKey("51.3.2-2-sneak"));
    }

    [Fact]
    public async Task Referenced_Concepts_ConvergeAcrossSourceSpecificAdmissionPaths()
    {


        var atts = await CollectAttestationsAsync();
        var rs = AnchorAdmission.Id("give.01", EntityTypeRegistry.PropBankRoleset)!.Value;
        var vn = AnchorAdmission.Id("13.1-1", EntityTypeRegistry.VerbNetClass)!.Value;
        var fn = CategoryAnchor.Id("Giving")!.Value;
        Assert.Contains(atts, a => a.SubjectId == rs || a.ObjectId == rs);
        Assert.Contains(atts, a => a.SubjectId == vn || a.ObjectId == vn);
        Assert.Contains(atts, a => a.SubjectId == fn || a.ObjectId == fn);
    }

    [Fact]
    public async Task Bootstrap_Registers_Source_Types_And_RelationTypeEntities()
    {
        var dec = new SemLinkDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext(writer));

        Assert.Equal(3, writer.Captured.Count);
        var boot = writer.Captured.First(c =>
            c.Metadata.SourceContentUnitName == "bootstrap/SemLinkDecomposer");
        Assert.Contains(boot.Entities, e =>
            e.Id == SemLinkDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e => e.Id == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO"));
        Assert.Contains(boot.Entities, e => e.Id == RelationTypeRegistry.RelationTypeId("ROLE_CORRESPONDS_TO"));
        Assert.Contains(boot.Attestations, a =>
            a.SubjectId == SemLinkDecomposer.Source
            && a.TypeId == BootstrapIntentBuilder.HasTrustClassTypeId
            && a.ObjectId == SemLinkDecomposer.TrustClass);

        var pmBoot = writer.Captured.First(c =>
            c.Metadata.SourceContentUnitName == "bootstrap/PredicateMatrixDecomposer");
        Assert.Contains(pmBoot.Entities, e =>
            e.Id == PredicateMatrixIngest.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(pmBoot.Entities, e => e.Id == EntityTypeRegistry.PredicateMatrixPredicate);
        Assert.Contains(pmBoot.Entities, e => e.Id == EntityTypeRegistry.PredicateMatrixRole);
        Assert.Contains(writer.Captured, c =>
            c.Metadata.SourceContentUnitName == "bootstrap/license/PredicateMatrixDecomposer");
        Assert.Equal("CC-BY-3.0", PredicateMatrixSource.License.Spdx);
        Assert.Equal("1.3", PredicateMatrixSource.License.Version);
        Assert.Contains("substrate/source/PredicateMatrixDecomposer/v1", dec.CanonicalNamesForReadback);
    }

    [Fact]
    public void ResolvePaths_Finds_VaultRoot_Versioned_PredicateMatrix()
    {
        string vault = Path.Combine(Path.GetTempPath(), "sl-vault-" + Guid.NewGuid().ToString("N"));
        string semlink = Path.Combine(vault, "SemLink");
        string pmDir = Path.Combine(vault, "PredicateMatrix.v1.3");
        Directory.CreateDirectory(Path.Combine(semlink, "semlink-master", "instances"));
        Directory.CreateDirectory(pmDir);
        File.WriteAllText(Path.Combine(semlink, "semlink-master", "instances", "pb-vn2.json"), "{}");
        string pmFile = Path.Combine(pmDir, "PredicateMatrix.v1.3.txt");
        File.WriteAllText(pmFile, PredicateMatrixHeader + Environment.NewLine);
        try
        {
            var paths = PredicateMatrixIngest.ResolvePaths(semlink).ToList();
            Assert.Contains(pmFile, paths);
        }
        finally
        {
            try { Directory.Delete(vault, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PredicateMatrix_Links_Roleset_VnClass_And_Frame_To_Synset_When_Cili_Present()
    {
        string cili = TestInstall.ResolveCiliOrFallback();
        if (!TestInstall.HasFullCiliMap(cili)) return;

        var atts = await CollectPredicateMatrixAttestationsAsync();
        var rsId = AnchorAdmission.Id("give.01", EntityTypeRegistry.PropBankRoleset)!.Value;
        var vnId = AnchorAdmission.Id("13.1-1", EntityTypeRegistry.VerbNetClass)!.Value;
        var fnId = CategoryAnchor.Id("Giving")!.Value;
        Hash128? synId = ConceptAnchor.SynsetId(2244956, 'v');
        Assert.NotNull(synId);

        CorrespondsToAssert.Contains(atts, rsId, synId.Value);
        CorrespondsToAssert.Contains(atts, vnId, synId.Value);
        CorrespondsToAssert.Contains(atts, fnId, synId.Value);
    }

    [Fact]
    public async Task PbWn_Json_Links_Roleset_To_Synset_When_Cili_Present()
    {
        string cili = TestInstall.ResolveCiliOrFallback();
        if (!TestInstall.HasFullCiliMap(cili)) return;

        var atts = await CollectPbWnAttestationsAsync();
        var rsId = AnchorAdmission.Id("give.01", EntityTypeRegistry.PropBankRoleset)!.Value;
        Hash128? synId = ConceptAnchor.SynsetId(2244956, 'v');
        Assert.NotNull(synId);
        CorrespondsToAssert.Contains(atts, rsId, synId.Value);
    }

    private static async Task<List<AttestationRow>> CollectAttestationsAsync()
    {
        var (_, atts) = await CollectAllAsync();
        var corr = RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO");
        return atts.Where(a => a.TypeId == corr).ToList();
    }

    private static async Task<List<AttestationRow>> CollectPredicateMatrixAttestationsAsync()
    {
        var (_, atts) = await CollectPredicateMatrixAsync();
        var corr = RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO");
        return atts.Where(a => a.TypeId == corr).ToList();
    }

    private static async Task<List<AttestationRow>> CollectPbWnAttestationsAsync()
    {
        var (_, atts) = await CollectPbWnAsync();
        var corr = RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO");
        return atts.Where(a => a.TypeId == corr).ToList();
    }

    private static async Task<(List<EntityRow> Entities, List<AttestationRow> Attestations)> CollectAllAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "instances"));
        string pbVnPath = Path.Combine(dir, "instances", "pb-vn2.json");
        string vnFnPath = Path.Combine(dir, "instances", "vn-fn2.json");
        await File.WriteAllTextAsync(pbVnPath, PbVnJson);
        await File.WriteAllTextAsync(vnFnPath, VnFnJson);
        try
        {
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var ents = new List<EntityRow>();
            var atts = new List<AttestationRow>();
            IDecomposer[] phases =
            [
                new SemLinkJsonDocumentPhase(pbVnPath, SemLinkDocumentKind.PbVn, "pb-vn2"),
                new SemLinkJsonDocumentPhase(vnFnPath, SemLinkDocumentKind.VnFn, "vn-fn2"),
            ];
            foreach (var phase in phases)
            {
                await foreach (var change in phase.DecomposeAsync(ctx, DecomposerOptions.Default))
                {
                    ents.AddRange(change.Entities.ToArray());
                    atts.AddRange(change.Attestations.ToArray());
                }
            }
            return (ents, atts);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static async Task<(List<EntityRow> Entities, List<AttestationRow> Attestations)> CollectPredicateMatrixAsync(
        params string[] rows)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl-pm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "PredicateMatrix"));
        if (rows.Length == 0) rows = [PredicateMatrixRow];
        string path = Path.Combine(dir, "PredicateMatrix", "PredicateMatrix.txt");
        await File.WriteAllTextAsync(
            path,
            PredicateMatrixHeader + Environment.NewLine
            + string.Join(Environment.NewLine, rows) + Environment.NewLine);
        try
        {
            var dec = new PredicateMatrixPhase(path, null);
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var ents = new List<EntityRow>();
            var atts = new List<AttestationRow>();
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
            {
                ents.AddRange(change.Entities.ToArray());
                atts.AddRange(change.Attestations.ToArray());
            }
            return (ents, atts);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static async Task<(List<EntityRow> Entities, List<AttestationRow> Attestations)> CollectPbWnAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl-pbwn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "instances"));
        string path = Path.Combine(dir, "instances", "pb-wn.json");
        await File.WriteAllTextAsync(path, PbWnJson);
        try
        {
            var dec = new SemLinkJsonDocumentPhase(path, SemLinkDocumentKind.PbWn, "pb-wn");
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var ents = new List<EntityRow>();
            var atts = new List<AttestationRow>();
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
            {
                ents.AddRange(change.Entities.ToArray());
                atts.AddRange(change.Attestations.ToArray());
            }
            return (ents, atts);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

}
