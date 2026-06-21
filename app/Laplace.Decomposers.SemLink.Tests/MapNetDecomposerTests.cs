using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.SemLink.Tests;

public sealed class MapNetDecomposerTests
{
    static MapNetDecomposerTests() => CodepointPerfcache.Load(ResolvePerfcacheBlob());

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

    private const string FrameRow = "Accoutrements\tn#02814860";
    private const string LuRow = "Accoutrements\taccoutrement.n\tn#02814860";

    [Fact]
    public void ResolvePaths_Finds_VaultRoot_Versioned_MapNet()
    {
        string vault = Path.Combine(Path.GetTempPath(), "mn-vault-" + Guid.NewGuid().ToString("N"));
        string mapNetDir = Path.Combine(vault, "MapNet-0.1");
        Directory.CreateDirectory(mapNetDir);
        string frameFile = Path.Combine(mapNetDir, "mapping_frame_synsets.txt");
        File.WriteAllText(frameFile, FrameRow + Environment.NewLine);
        try
        {
            var paths = MapNetIngest.ResolvePaths(vault).ToList();
            Assert.Contains(frameFile, paths);
        }
        finally
        {
            try { Directory.Delete(vault, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MapNet_Links_Lu_To_Synset_When_Cili_Present()
    {
        string cili = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        if (!File.Exists(Path.Combine(cili, IliMap.MapFileName))) return;

        var atts = await CollectAttestationsAsync();
        var luId = CategoryAnchor.Id(SourceEntityIdConventions.FrameNetLuKey("Accoutrements", "accoutrement.n"))!.Value;
        Hash128? synId = ConceptAnchor.SynsetId(2814860, 'n');
        Assert.NotNull(synId);
        Assert.Contains(atts, a => a.SubjectId == luId && a.ObjectId == synId);
    }

    [Fact]
    public async Task MapNet_Links_Frame_To_Synset_When_Cili_Present()
    {
        string cili = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        if (!File.Exists(Path.Combine(cili, IliMap.MapFileName))) return;

        var atts = await CollectAttestationsAsync();
        var frameId = CategoryAnchor.Id("Accoutrements")!.Value;
        Hash128? synId = ConceptAnchor.SynsetId(2814860, 'n');
        Assert.NotNull(synId);
        Assert.Contains(atts, a => a.SubjectId == frameId && a.ObjectId == synId);
    }

    [Fact]
    public async Task Attestations_Are_CorrespondsTo_Only()
    {
        var atts = await CollectAttestationsAsync();
        var canonical = new HashSet<Hash128>(RelationTypeRegistry.AllCanonical().Select(k => k.Id));
        Assert.All(atts, a => Assert.Contains(a.TypeId, canonical));
        Assert.All(atts, a => Assert.Equal(RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO"), a.TypeId));
        Assert.NotEmpty(atts);
    }

    private static async Task<List<AttestationRow>> CollectAttestationsAsync()
    {
        var (_, atts) = await CollectAllAsync();
        var corr = RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO");
        return atts.Where(a => a.TypeId == corr).ToList();
    }

    private static async Task<(List<EntityRow> Entities, List<AttestationRow> Attestations)> CollectAllAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "mapping_frame_synsets.txt"), FrameRow + Environment.NewLine);
        await File.WriteAllTextAsync(Path.Combine(dir, "mapping_lus_synsets.txt"), LuRow + Environment.NewLine);
        try
        {
            var dec = new MapNetDecomposer();
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
        public string EcosystemPath { get; init; } = "/vault/Data/MapNet-0.1";
        public ISubstrateWriter Writer { get; } = writer;
        public ISubstrateReader Reader { get; } = new NullReader();
        public ILogger Logger { get; } = NullLogger.Instance;
        public string SubstrateVersion => "test";
    }

    private sealed class NullWriter : ISubstrateWriter
    {
        public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
            => Task.FromResult(new ApplyResult(0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, false));
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
