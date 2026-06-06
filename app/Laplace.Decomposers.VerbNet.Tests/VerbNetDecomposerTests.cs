using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.VerbNet.Tests;

/// <summary>
/// Verifies VerbNetDecomposer against a tiny inline class XML fixture (one class + one
/// subclass): the right registry-routed attestation kinds land, the meta/content/sense
/// entities use the shared id conventions, and no raw rows are minted. ContentEmitter routes
/// lemmas/roles/frames/examples through the T0 perf-cache, so the static ctor loads it (the
/// host precondition every content-bearing decomposer has).
/// </summary>
public sealed class VerbNetDecomposerTests
{
    static VerbNetDecomposerTests() => CodepointPerfcache.Load(ResolvePerfcacheBlob());

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

    // A faithful give-13.1 fixture: one top class with members (one with wn= sense keys, one
    // without), two thematic roles, one frame with a primary description + example; one nested
    // subclass give-13.1-1 with its own member + role.
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
    public async Task Attestations_Use_RegistryRouted_Canonical_Kind_Ids()
    {
        var atts = await CollectAttestationsAsync();

        // Every kind id is a canonical-registry kind id (registry-routed, never a raw row).
        var canonical = new HashSet<Hash128>(RelationTypeRegistry.AllCanonical().Select(k => k.Id));
        Assert.All(atts, a => Assert.Contains(a.TypeId, canonical));

        // The load-bearing arenas are present.
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("IS_A"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_THEMATIC_ROLE"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_VERB_FRAME"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_EXAMPLE"));
        Assert.Contains(atts, a => a.TypeId == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO"));
    }

    [Fact]
    public async Task Member_IsA_Class_And_Class_IsA_ParentClass()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(VerbNetDecomposer.Source, "fixture", null);

        // "lend" wordform —IS_A→ give-13.1 class meta (bare-numeric id 13.1).
        var lendId = ContentEmitter.Emit(b, "lend", VerbNetDecomposer.Source);
        var classId = VerbNetDecomposer.ClassId("give-13.1");
        Assert.Equal(Hash128.OfCanonical("verbnet/class/13.1"), classId);
        Assert.NotNull(lendId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("IS_A")
            && a.SubjectId == lendId!.Value && a.ObjectId == classId);

        // subclass give-13.1-1 (bare 13.1-1) —IS_A→ give-13.1 (bare 13.1).
        var subId = VerbNetDecomposer.ClassId("give-13.1-1");
        Assert.Equal(Hash128.OfCanonical("verbnet/class/13.1-1"), subId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("IS_A")
            && a.SubjectId == subId && a.ObjectId == classId);
    }

    [Fact]
    public async Task Member_WnSenseKeys_Correspond_To_WordNet_Sense_Entities()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(VerbNetDecomposer.Source, "fixture", null);
        var lendId = ContentEmitter.Emit(b, "lend", VerbNetDecomposer.Source);

        // wn="lend%2:40:00" normalizes to the index.sense key "lend%2:40:00::" — the
        // EXACT id WordNetDecomposer mints (wordnet/sense/<key>).
        var senseId = Hash128.OfCanonical("wordnet/sense/lend%2:40:00::");
        Assert.Equal(senseId, VerbNetDecomposer.SenseId("lend%2:40:00::"));
        Assert.NotNull(lendId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO")
            && (a.SubjectId == lendId!.Value || a.ObjectId == lendId!.Value)
            && (a.SubjectId == senseId || a.ObjectId == senseId));
    }

    [Fact]
    public void NormalizeSenseKey_Appends_DoubleColon_And_Strips_Uncertainty_Marker()
    {
        Assert.Equal("give%2:40:03::", VerbNetDecomposer.NormalizeSenseKey("give%2:40:03"));
        Assert.Equal("give%2:40:03::", VerbNetDecomposer.NormalizeSenseKey("give%2:40:03::"));
        Assert.Equal("ache%2:37:06::", VerbNetDecomposer.NormalizeSenseKey("?ache%2:37:06"));
        Assert.Null(VerbNetDecomposer.NormalizeSenseKey("notasensekey"));
    }

    [Fact]
    public void NumericClassId_Strips_Lemma_Prefix()
    {
        Assert.Equal("13.1", VerbNetDecomposer.NumericClassId("give-13.1"));
        Assert.Equal("13.1-1", VerbNetDecomposer.NumericClassId("give-13.1-1"));
        Assert.Equal("10.11-2", VerbNetDecomposer.NumericClassId("resign-10.11-2"));
        Assert.Equal("13.1", VerbNetDecomposer.NumericClassId("13.1"));   // already bare
        Assert.Equal("45.8", VerbNetDecomposer.NumericClassId("break_down-45.8"));
    }

    [Fact]
    public async Task Bootstrap_Registers_Source_Types_And_KindEntities()
    {
        var dec = new VerbNetDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext(writer));

        Assert.Single(writer.Captured);
        var boot = writer.Captured[0];
        Assert.Contains(boot.Entities, e =>
            e.Id == VerbNetDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == Hash128.OfCanonical("substrate/type/VerbNet_Class/v1")
            && e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId);
        Assert.Contains(boot.Entities, e => e.Id == RelationTypeRegistry.RelationTypeId("HAS_THEMATIC_ROLE"));
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
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    // === fakes ===

    private sealed class FakeContext(ISubstrateWriter writer) : IDecomposerContext
    {
        public string EcosystemPath { get; init; } = "/vault/Data/VerbNet";
        public ISubstrateWriter Writer { get; } = writer;
        public ISubstrateReader Reader { get; } = new NullReader();
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public string SubstrateVersion => "test";
    }

    private sealed class NullWriter : ISubstrateWriter
    {
        public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
            => Task.FromResult(new ApplyResult(0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, false));
    }

    private sealed class CapturingWriter : ISubstrateWriter
    {
        public List<SubstrateChange> Captured { get; } = new();
        public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        {
            Captured.Add(change);
            return Task.FromResult(new ApplyResult(
                change.Entities.Length, change.Entities.Length,
                change.Physicalities.Length, change.Physicalities.Length,
                change.Attestations.Length, change.Attestations.Length, 4, TimeSpan.Zero, false));
        }
    }

    private sealed class NullReader : ISubstrateReader
    {
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
            => Task.FromResult(new byte[(candidates.Count + 7) / 8]);
    }
}
