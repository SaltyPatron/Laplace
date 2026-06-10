using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.SemLink.Tests;

public sealed class SemLinkDecomposerTests
{
    static SemLinkDecomposerTests() => CodepointPerfcache.Load(ResolvePerfcacheBlob());

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

    private const string PbVnJson =
        """{"give.01": {"13.1-1": {"ARG0": "agent", "ARG1": "theme"}}, "abdicate.01": {"10.11-2": {}}}""";

    private const string VnFnJson =
        """{"13.1-1-give": ["Giving", "Commerce_sell"], "21.1-1-chip": ["Cause_to_fragment"]}""";

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
        var rsId = Hash128.OfCanonical("propbank/roleset/give.01");
        var vnId = Hash128.OfCanonical("verbnet/class/13.1-1");
        Assert.Equal(rsId, SemLinkDecomposer.RolesetId("give.01"));
        Assert.Equal(vnId, SemLinkDecomposer.VnClassId("13.1-1"));
        Assert.Contains(atts, a =>
            (a.SubjectId == rsId && a.ObjectId == vnId) ||
            (a.SubjectId == vnId && a.ObjectId == rsId));
    }

    [Fact]
    public async Task PbVn_Role_Level_Maps_Arg_Content_To_Theta_Content_With_Class_Context()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(SemLinkDecomposer.Source, "fixture", null);
        var argId   = ContentEmitter.Emit(b, "ARG0", SemLinkDecomposer.Source);
        var thetaId = ContentEmitter.Emit(b, "agent", SemLinkDecomposer.Source);
        var vnId    = Hash128.OfCanonical("verbnet/class/13.1-1");
        Assert.NotNull(argId);
        Assert.NotNull(thetaId);
        Assert.Contains(atts, a =>
            a.ContextId == vnId
            && (a.SubjectId == argId!.Value || a.ObjectId == argId!.Value)
            && (a.SubjectId == thetaId!.Value || a.ObjectId == thetaId!.Value));
    }

    [Fact]
    public async Task VnFn_Maps_Class_To_FrameNet_Frame_With_Shared_Ids()
    {
        var atts = await CollectAttestationsAsync();
        var vnId = Hash128.OfCanonical("verbnet/class/13.1-1");
        var fnId = Hash128.OfCanonical("framenet/frame/Giving");
        Assert.Equal(fnId, SemLinkDecomposer.FrameId("Giving"));
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
    public async Task Referenced_Meta_Entities_Emitted_For_Standalone_Ingest()
    {
        var (ents, _) = await CollectAllAsync();
        Assert.Contains(ents, e => e.Id == Hash128.OfCanonical("propbank/roleset/give.01"));
        Assert.Contains(ents, e => e.Id == Hash128.OfCanonical("verbnet/class/13.1-1"));
        Assert.Contains(ents, e => e.Id == Hash128.OfCanonical("framenet/frame/Giving"));
    }

    [Fact]
    public async Task Bootstrap_Registers_Source_Types_And_RelationTypeEntities()
    {
        var dec = new SemLinkDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext(writer));

        Assert.Single(writer.Captured);
        var boot = writer.Captured[0];
        Assert.Contains(boot.Entities, e =>
            e.Id == SemLinkDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e => e.Id == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO"));
        Assert.Contains(boot.Attestations, a =>
            a.SubjectId == SemLinkDecomposer.Source
            && a.TypeId == BootstrapIntentBuilder.HasTrustClassTypeId
            && a.ObjectId == SemLinkDecomposer.TrustClass);
    }

    private static async Task<List<AttestationRow>> CollectAttestationsAsync()
    {
        var (_, atts) = await CollectAllAsync();
        return atts;
    }

    private static async Task<(List<EntityRow> Entities, List<AttestationRow> Attestations)> CollectAllAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "instances"));
        await File.WriteAllTextAsync(Path.Combine(dir, "instances", "pb-vn2.json"), PbVnJson);
        await File.WriteAllTextAsync(Path.Combine(dir, "instances", "vn-fn2.json"), VnFnJson);
        try
        {
            var dec = new SemLinkDecomposer();
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

    private sealed class FakeContext(ISubstrateWriter writer) : IDecomposerContext
    {
        public string EcosystemPath { get; init; } = "/vault/Data/SemLink";
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
