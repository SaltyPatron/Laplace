using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.SemLink.Tests;

public sealed class MapNetDecomposerTests
{
    static MapNetDecomposerTests() => SemLinkTestPerfcache.Load();

    private const string FrameRow = "Accoutrements\tn#02814860";
    private const string LuRow = "Accoutrements\thelmet.n\tn#02814860";

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
        string cili = TestInstall.ResolveCiliOrFallback();
        if (!TestInstall.HasFullCiliMap(cili)) return;

        var atts = await CollectAttestationsAsync();
        var luId = CategoryAnchor.Id(SourceEntityIdConventions.FrameNetLuKey("Accoutrements", "helmet.n"))!.Value;
        Hash128? synId = ConceptAnchor.SynsetId(2814860, 'n', SourceEntityIdConventions.MultiWordNetWnVersion);
        Assert.NotNull(synId);
        CorrespondsToAssert.Contains(atts, luId, synId.Value);
    }

    [Fact]
    public async Task MapNet_Links_Frame_To_Synset_When_Cili_Present()
    {
        string cili = TestInstall.ResolveCiliOrFallback();
        if (!TestInstall.HasFullCiliMap(cili)) return;

        var atts = await CollectAttestationsAsync();
        var frameId = CategoryAnchor.Id("Accoutrements")!.Value;
        Hash128? synId = ConceptAnchor.SynsetId(2814860, 'n', SourceEntityIdConventions.MultiWordNetWnVersion);
        Assert.NotNull(synId);
        CorrespondsToAssert.Contains(atts, frameId, synId.Value);
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

}
