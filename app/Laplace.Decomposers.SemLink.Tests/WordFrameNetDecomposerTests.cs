using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.SemLink.Tests;

public sealed class WordFrameNetDecomposerTests
{
    static WordFrameNetDecomposerTests() => CodepointPerfcache.Load(ResolvePerfcacheBlob());

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

    private const string MapStyleRow = "Giving\tgive.v\t30-02244956-v";
    private const string FourColRow = "Giving\tgive\tv\t02244956-v";

    [Fact]
    public void TryParseRow_Accepts_MapStyle_And_FourColumn_Rows()
    {
        Assert.True(FnLuSynsetBridgeIngest.TryParseRow(MapStyleRow, out var f1, out var lu1, out var s1));
        Assert.Equal("Giving", f1);
        Assert.Equal("give.v", lu1);
        Assert.Equal("30-02244956-v", s1);

        Assert.True(FnLuSynsetBridgeIngest.TryParseRow(FourColRow, out var f2, out var lu2, out var s2));
        Assert.Equal("Giving", f2);
        Assert.Equal("give.v", lu2);
        Assert.Equal("02244956-v", s2);
    }

    [Fact]
    public void TryParseWfnNative_Parses_FrameHeader_And_DataLines()
    {
        Assert.True(FnLuSynsetBridgeIngest.TryParseWfnNativeFrameHeader(
            "Frame: Abounding_with", out var frame));
        Assert.Equal("Abounding_with", frame);

        Assert.True(FnLuSynsetBridgeIngest.TryParseWfnNativeDataLine(
            "brushed a 01105125-a having soft nap produced by brushing",
            out var lemma, out var pos, out var syn));
        Assert.Equal("brushed", lemma);
        Assert.Equal("a", pos);
        Assert.Equal("01105125-a", syn);

        Assert.True(FnLuSynsetBridgeIngest.TryParseWfnNativeDataLine(
            "fleecy a 01105125-a", out lemma, out pos, out syn));
        Assert.Equal("fleecy", lemma);
        Assert.Equal("01105125-a", syn);

        // REAL data also carries a pipe-joined "<lemma>|<pos>" layout with a trailing numeric + gloss —
        // the old regex dropped every one of these silently.
        Assert.True(FnLuSynsetBridgeIngest.TryParseWfnNativeDataLine(
            "abusive|a 01114176-a 0 by physical or psychological maltreatment",
            out lemma, out pos, out syn));
        Assert.Equal("abusive", lemma);
        Assert.Equal("a", pos);
        Assert.Equal("01114176-a", syn);

        // Multi-word lemmas (common for FrameNet LUs) — the old "(\S+)" lemma group dropped these.
        Assert.True(FnLuSynsetBridgeIngest.TryParseWfnNativeDataLine(
            "back and forth r 00114809-r to and fro", out lemma, out pos, out syn));
        Assert.Equal("back_and_forth", lemma);
        Assert.Equal("r", pos);
        Assert.Equal("00114809-r", syn);

        // Satellite-adjective ss-type 's' — the old regex's [avnr] class excluded it outright.
        Assert.True(FnLuSynsetBridgeIngest.TryParseWfnNativeDataLine(
            "prepared|a 01771525-s 0 ready beforehand", out lemma, out pos, out syn));
        Assert.Equal("01771525-s", syn);
    }

    [Fact]
    public void ResolvePaths_Finds_Extensionless_WfnTarLayout()
    {
        string vault = Path.Combine(Path.GetTempPath(), "wfn-vault-" + Guid.NewGuid().ToString("N"));
        string wfnFile = Path.Combine(vault, "WordFrameNet", "WFN", "WordFrameNet");
        Directory.CreateDirectory(Path.GetDirectoryName(wfnFile)!);
        File.WriteAllText(wfnFile,
            "Frame: Giving" + Environment.NewLine +
            "give v 02244956-v to transfer possession" + Environment.NewLine);
        try
        {
            var paths = WordFrameNetIngest.ResolvePaths(Path.Combine(vault, "WordFrameNet")).ToList();
            Assert.Contains(wfnFile, paths);
        }
        finally
        {
            try { Directory.Delete(vault, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolvePaths_Finds_VaultRoot_WordFrameNet()
    {
        string vault = Path.Combine(Path.GetTempPath(), "wfn-vault-" + Guid.NewGuid().ToString("N"));
        string wfnDir = Path.Combine(vault, "WordFrameNet");
        Directory.CreateDirectory(wfnDir);
        string mapFile = Path.Combine(wfnDir, "lu_synset.map");
        File.WriteAllText(mapFile, MapStyleRow + Environment.NewLine);
        try
        {
            var paths = WordFrameNetIngest.ResolvePaths(vault).ToList();
            Assert.Contains(mapFile, paths);
        }
        finally
        {
            try { Directory.Delete(vault, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WordFrameNet_Links_Lu_To_Synset_When_Cili_Present()
    {
        string cili = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        if (!File.Exists(Path.Combine(cili, IliMap.MapFileName))) return;

        var atts = await CollectAttestationsAsync();
        var luId = CategoryAnchor.Id(SourceEntityIdConventions.FrameNetLuKey("Giving", "give.v"))!.Value;
        Hash128? synId = ConceptAnchor.SynsetId(2244956, 'v');
        Assert.NotNull(synId);
        Assert.Contains(atts, a => a.SubjectId == luId && a.ObjectId == synId);
    }

    [Fact]
    public async Task Attestations_Are_CorrespondsTo_Only()
    {
        var atts = await CollectAttestationsAsync();
        var corr = RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO");
        Assert.All(atts, a => Assert.Equal(corr, a.TypeId));
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
        string dir = Path.Combine(Path.GetTempPath(), "wfn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "WordFrameNet"),
            "Frame: Giving" + Environment.NewLine +
            "give v 02244956-v to transfer possession" + Environment.NewLine);
        try
        {
            var dec = new WordFrameNetDecomposer();
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
        public string EcosystemPath { get; init; } = "/vault/Data/WordFrameNet";
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
