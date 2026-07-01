using System.Text;
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

    private const string PbWnJson =
        """{"give.01": "30-02244956-v", "speak.01": "30-00941990-v"}""";

    private const string PredicateMatrixHeader =
        "1_ID_LANG\t1_ID_POS\t2_ID_PRED\t3_ID_ROLE\t4_VN_CLASS\t5_VN_CLASS_NUMBER\t6_VN_SUBCLASS\t7_VN_SUBCLASS_NUMBER\t8_VN_LEMA\t9_VN_ROLE\t10_WN_SENSE\t11_MCR_iliOffset\t12_FN_FRAME\t13_FN_LE\t14_FN_FRAME_ELEMENT\t15_PB_ROLESET\t16_PB_ARG";

    private const string PredicateMatrixRow =
        "id:eng\tid:v\tid:give.01\tid:0\tvn:give\t13.1\t13.1-1\t1\tgive\tvn:Agent\twn:give%2:40:03\tili-30-02244956-v\tfn:Giving\tNULL\tNULL\tpb:give.01\tpb:0";

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
        var rsId = CategoryAnchor.Id("give.01")!.Value;
        var vnId = CategoryAnchor.Id("13.1-1")!.Value;
        Assert.Contains(atts, a =>
            (a.SubjectId == rsId && a.ObjectId == vnId) ||
            (a.SubjectId == vnId && a.ObjectId == rsId));
    }

    [Fact]
    public async Task PbVn_Role_Level_Maps_Arg_Content_To_Theta_Content_With_Class_Context()
    {
        var (_, allAtts) = await CollectAllAsync();
        var atts = allAtts.Where(
            a => a.TypeId == RelationTypeRegistry.RelationTypeId("ROLE_CORRESPONDS_TO")).ToList();
        var (argId, thetaId) = ComposedArgThetaIds();
        var vnId = CategoryAnchor.Id("13.1-1")!.Value;
        Assert.Contains(atts, a =>
            a.ContextId == vnId
            && (a.SubjectId == argId || a.ObjectId == argId)
            && (a.SubjectId == thetaId || a.ObjectId == thetaId));
    }

    [Fact]
    public async Task VnFn_Maps_Class_To_FrameNet_Frame_With_Shared_Ids()
    {
        var atts = await CollectAttestationsAsync();
        var vnId = CategoryAnchor.Id("13.1-1")!.Value;
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
    public async Task Referenced_Concepts_Are_Shared_Content_Anchors_Not_Blobs()
    {


        var atts = await CollectAttestationsAsync();
        var rs = CategoryAnchor.Id("give.01")!.Value;
        var vn = CategoryAnchor.Id("13.1-1")!.Value;
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

        Assert.Single(writer.Captured);
        var boot = writer.Captured[0];
        Assert.Contains(boot.Entities, e =>
            e.Id == SemLinkDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e => e.Id == RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO"));
        Assert.Contains(boot.Entities, e => e.Id == RelationTypeRegistry.RelationTypeId("ROLE_CORRESPONDS_TO"));
        Assert.Contains(boot.Attestations, a =>
            a.SubjectId == SemLinkDecomposer.Source
            && a.TypeId == BootstrapIntentBuilder.HasTrustClassTypeId
            && a.ObjectId == SemLinkDecomposer.TrustClass);
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
        string cili = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        if (!File.Exists(Path.Combine(cili, IliMap.MapFileName))) return;

        var atts = await CollectPredicateMatrixAttestationsAsync();
        var rsId = CategoryAnchor.Id("give.01")!.Value;
        var vnId = CategoryAnchor.Id("13.1-1")!.Value;
        var fnId = CategoryAnchor.Id("Giving")!.Value;
        Hash128? synId = ConceptAnchor.SynsetId(2244956, 'v');
        Assert.NotNull(synId);

        Assert.Contains(atts, a => a.SubjectId == rsId && a.ObjectId == synId);
        Assert.Contains(atts, a => a.SubjectId == vnId && a.ObjectId == synId);
        Assert.Contains(atts, a => a.SubjectId == fnId && a.ObjectId == synId);
    }

    [Fact]
    public async Task PbWn_Json_Links_Roleset_To_Synset_When_Cili_Present()
    {
        string cili = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        if (!File.Exists(Path.Combine(cili, IliMap.MapFileName))) return;

        var atts = await CollectPbWnAttestationsAsync();
        var rsId = CategoryAnchor.Id("give.01")!.Value;
        Hash128? synId = ConceptAnchor.SynsetId(2244956, 'v');
        Assert.NotNull(synId);
        Assert.Contains(atts, a => a.SubjectId == rsId && a.ObjectId == synId);
    }

    private static (Hash128 ArgId, Hash128 ThetaId) ComposedArgThetaIds()
    {

        const string singlePair =
            """{"give.01": {"13.1-1": {"ARG0": "agent", "ARG1": "theme"}}}""";
        byte[] utf8 = Encoding.UTF8.GetBytes(singlePair);
        var recipe = GrammarDecomposer.LookupById("json");
        using var ast = GrammarDecomposer.Parse(utf8, recipe);
        using var composer = new GrammarRowComposer(utf8, ast, SemLinkDecomposer.Source, "json");
        var (_, _, _, root) = composer.Materialize(1.0);
        var ctx = new GrammarComposeContext(utf8, ast, root, composer, JsonGrammarHelper.FindRootObjectNode(ast));
        int rootObj = JsonGrammarHelper.FindRootObjectNode(ast);
        foreach (var (_, vnObjNode) in JsonGrammarHelper.EnumerateObjectPairs(ast, rootObj))
        {
            foreach (var (_, rolesObjNode) in JsonGrammarHelper.EnumerateObjectPairs(ast, vnObjNode))
            {
                if (!JsonGrammarHelper.IsObjectNode(ast, rolesObjNode)) continue;
                foreach (var (argKeyNode, thetaNode) in JsonGrammarHelper.EnumerateObjectPairs(ast, rolesObjNode))
                {
                    if (JsonGrammarHelper.TryComposedNode(ctx, argKeyNode, out var argId)
                        && JsonGrammarHelper.TryComposedNode(ctx, thetaNode, out var thetaId))
                        return (argId, thetaId);
                }
            }
        }
        throw new InvalidOperationException("ARG0/agent composed spans not found in fixture JSON");
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

    private static async Task<(List<EntityRow> Entities, List<AttestationRow> Attestations)> CollectPredicateMatrixAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl-pm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "PredicateMatrix"));
        await File.WriteAllTextAsync(
            Path.Combine(dir, "PredicateMatrix", "PredicateMatrix.txt"),
            PredicateMatrixHeader + Environment.NewLine + PredicateMatrixRow + Environment.NewLine);
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

    private static async Task<(List<EntityRow> Entities, List<AttestationRow> Attestations)> CollectPbWnAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl-pbwn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "instances"));
        await File.WriteAllTextAsync(Path.Combine(dir, "instances", "pb-wn.json"), PbWnJson);
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
