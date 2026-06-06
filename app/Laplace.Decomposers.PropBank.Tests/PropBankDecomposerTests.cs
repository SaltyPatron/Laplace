using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.PropBank.Tests;

/// <summary>
/// Verifies PropBankDecomposer against a tiny inline frameset XML fixture (one predicate,
/// one roleset, numbered roles with a VerbNet rolelink, an example): registry-routed kinds,
/// the shared meta id conventions (roleset / VN class), the arg-number ordinal context, and
/// no raw rows. ContentEmitter routes text through the perf-cache (host precondition).
/// </summary>
public sealed class PropBankDecomposerTests
{
    static PropBankDecomposerTests() => CodepointPerfcache.Load(ResolvePerfcacheBlob());

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

    // A faithful give.xml fixture: predicate "give", roleset give.01 "transfer", three
    // numbered roles (giver/0, thing given/1, entity given to/2) each with a VerbNet rolelink
    // (class give-13.1-1, theta role), and one example.
    private const string FramesetXml = """
<frameset>
  <predicate lemma="give">
    <roleset id="give.01" name="transfer">
      <roles>
        <role descr="giver" f="PAG" n="0">
          <rolelinks>
            <rolelink class="give-13.1-1" resource="VerbNet" version="verbnet3.4">agent</rolelink>
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

    [Fact]
    public async Task Attestations_Use_RegistryRouted_Canonical_Kind_Ids()
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
        var rsId = PropBankDecomposer.RolesetId("give.01");
        Assert.Equal(Hash128.OfCanonical("propbank/roleset/give.01"), rsId);
        Assert.NotNull(giveId);

        // lemma —HAS_SENSE→ roleset (the WordNet lemma→sense arena + direction).
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SENSE")
            && a.SubjectId == giveId!.Value && a.ObjectId == rsId);
    }

    [Fact]
    public async Task SemanticRole_Carries_ArgNumber_As_Ordinal_Context()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(PropBankDecomposer.Source, "fixture", null);
        var giverId = ContentEmitter.Emit(b, "giver", PropBankDecomposer.Source);
        var rsId = PropBankDecomposer.RolesetId("give.01");

        // roleset —HAS_SEMANTIC_ROLE→ "giver" with arg 0's ordinal entity as context.
        var ord0 = PropBankDecomposer.OrdinalId("0");
        Assert.Equal(Hash128.OfCanonical("ordinal/0/v1"), ord0);
        Assert.NotNull(giverId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SEMANTIC_ROLE")
            && a.SubjectId == rsId && a.ObjectId == giverId!.Value && a.ContextId == ord0);
    }

    [Fact]
    public async Task RoleLink_Corresponds_To_VerbNet_Class_And_ThetaRole()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(PropBankDecomposer.Source, "fixture", null);
        var rsId = PropBankDecomposer.RolesetId("give.01");

        // roleset ↔ VN class (bare-numeric 13.1-1 — collides with VerbNet/SemLink).
        var vnId = PropBankDecomposer.VnClassId("give-13.1-1");
        Assert.Equal(Hash128.OfCanonical("verbnet/class/13.1-1"), vnId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO")
            && (a.SubjectId == rsId || a.ObjectId == rsId)
            && (a.SubjectId == vnId || a.ObjectId == vnId));

        // PB role "giver" ↔ VN theta "agent" (both content), VN class as context.
        var giverId = ContentEmitter.Emit(b, "giver", PropBankDecomposer.Source);
        var agentId = ContentEmitter.Emit(b, "agent", PropBankDecomposer.Source);
        Assert.NotNull(giverId);
        Assert.NotNull(agentId);
        Assert.Contains(atts, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO")
            && a.ContextId == vnId
            && (a.SubjectId == giverId!.Value || a.ObjectId == giverId!.Value)
            && (a.SubjectId == agentId!.Value || a.ObjectId == agentId!.Value));
    }

    [Fact]
    public void NumericClassId_Strips_Lemma_Prefix()
    {
        Assert.Equal("13.1-1", PropBankDecomposer.NumericClassId("give-13.1-1"));
        Assert.Equal("10.11-2", PropBankDecomposer.NumericClassId("resign-10.11-2"));
        Assert.Equal("13.1-1", PropBankDecomposer.NumericClassId("13.1-1"));
    }

    [Fact]
    public async Task Bootstrap_Registers_Source_Types_And_KindEntities()
    {
        var dec = new PropBankDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext(writer));

        Assert.Single(writer.Captured);
        var boot = writer.Captured[0];
        Assert.Contains(boot.Entities, e =>
            e.Id == PropBankDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == Hash128.OfCanonical("substrate/type/PropBank_Roleset/v1"));
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
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    // === fakes ===

    private sealed class FakeContext(ISubstrateWriter writer) : IDecomposerContext
    {
        public string EcosystemPath { get; init; } = "/vault/Data/PropBank";
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
