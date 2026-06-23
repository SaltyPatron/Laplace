using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions.Tests;

// WithCiliDir mutates the process-global LAPLACE_CILI_DIR env var and resets the static IliMap cache.
// That mutation must not overlap any test that reads the real on-disk CILI map (IliMapTests,
// ConceptAnchorTests, CrossSourceLinkingTests) or it momentarily repoints them at a temp dir and they
// observe an empty/foreign map. Joining the GrammarPerfcache collection serializes this class against
// those readers (xUnit runs a collection's tests without inter-class parallelism); no assertion changes.
[Collection("GrammarPerfcache")]
public class SourceEntityIdConventionsTests
{
    private static string? _savedCiliDir;
    private static string? _savedDataRoot;

    private static void WithCiliDir(string dir, Action body)
    {
        _savedCiliDir = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR");
        _savedDataRoot = Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT");
        Environment.SetEnvironmentVariable("LAPLACE_CILI_DIR", dir);
        Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", null);
        SourceEntityIdConventions.ResetIliMapCacheForTests();
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_CILI_DIR", _savedCiliDir);
            Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", _savedDataRoot);
            SourceEntityIdConventions.ResetIliMapCacheForTests();
        }
    }

    private static string NewTempDir()
    {
        string d = Path.Combine(Path.GetTempPath(),
            "laplace-srcid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string WriteModel(string dir, byte[] weights, string config)
    {
        File.WriteAllBytes(Path.Combine(dir, "model.safetensors"), weights);
        File.WriteAllText(Path.Combine(dir, "config.json"), config);
        return dir;
    }

    [Fact]
    public void Model_IdenticalContent_DifferentDirName_SameId()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            var bytes = Encoding.ASCII.GetBytes(new string('W', 4096));
            WriteModel(a, bytes, "{\"hidden\":8}");
            WriteModel(b, bytes, "{\"hidden\":8}");

            Hash128? ida = SourceEntityIdConventions.ModelContentSourceId(a);
            Hash128? idb = SourceEntityIdConventions.ModelContentSourceId(b);

            Assert.NotNull(ida);
            Assert.Equal(ida, idb);
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void Model_OneByteDifferent_DistinctId()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            var bytes = Encoding.ASCII.GetBytes(new string('W', 4096));
            WriteModel(a, bytes, "{\"hidden\":8}");
            var flipped = (byte[])bytes.Clone();
            flipped[2048] ^= 0x01;
            WriteModel(b, flipped, "{\"hidden\":8}");

            Assert.NotEqual(SourceEntityIdConventions.ModelContentSourceId(a),
                            SourceEntityIdConventions.ModelContentSourceId(b));
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void Model_DifferentConfigSameWeights_DistinctId()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            var bytes = Encoding.ASCII.GetBytes(new string('W', 4096));
            WriteModel(a, bytes, "{\"rope_theta\":10000}");
            WriteModel(b, bytes, "{\"rope_theta\":500000}");

            Assert.NotEqual(SourceEntityIdConventions.ModelContentSourceId(a),
                            SourceEntityIdConventions.ModelContentSourceId(b));
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void Model_NoWeightFiles_ReturnsNull()
    {
        var d = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(d, "config.json"), "{\"hidden\":8}");
            Assert.Null(SourceEntityIdConventions.ModelContentSourceId(d));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Model_ChunkBoundary_DeterministicAcrossSizes()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            var bytes = new byte[70 * 1024 * 1024 + 12345];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + 7);
            WriteModel(a, bytes, "{\"hidden\":8}");
            WriteModel(b, bytes, "{\"hidden\":8}");
            Assert.Equal(SourceEntityIdConventions.ModelContentSourceId(a),
                         SourceEntityIdConventions.ModelContentSourceId(b));
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void Text_BomAndCrlfVariant_SameId()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(a, "corpus.txt"),
                Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n"));
            var withBomCrlf = new List<byte> { 0xEF, 0xBB, 0xBF };
            withBomCrlf.AddRange(Encoding.UTF8.GetBytes("alpha\r\nbeta\r\ngamma\r\n"));
            File.WriteAllBytes(Path.Combine(b, "corpus.txt"), withBomCrlf.ToArray());

            Hash128 ida = SourceEntityIdConventions.NormalizedTextSourceId(
                "substrate/source/test-text/v1", new[] { Path.Combine(a, "corpus.txt") });
            Hash128 idb = SourceEntityIdConventions.NormalizedTextSourceId(
                "substrate/source/test-text/v1", new[] { Path.Combine(b, "corpus.txt") });

            Assert.Equal(ida, idb);
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void Text_DifferentContent_DistinctId()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(a, "corpus.txt"), "alpha\nbeta\n");
            File.WriteAllText(Path.Combine(b, "corpus.txt"), "alpha\ndelta\n");
            Assert.NotEqual(
                SourceEntityIdConventions.NormalizedTextSourceId(
                    "substrate/source/test-text/v1", new[] { Path.Combine(a, "corpus.txt") }),
                SourceEntityIdConventions.NormalizedTextSourceId(
                    "substrate/source/test-text/v1", new[] { Path.Combine(b, "corpus.txt") }));
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void NumericVerbNetClassId_Strips_Lemma_Prefix()
    {
        Assert.Equal("13.1", SourceEntityIdConventions.NumericVerbNetClassId("give-13.1"));
        Assert.Equal("13.1-1", SourceEntityIdConventions.NumericVerbNetClassId("give-13.1-1"));
        Assert.Equal("13.1", SourceEntityIdConventions.NumericVerbNetClassId("13.1"));
    }

    [Fact]
    public void VerbNetClassFromSemLinkKey_Splits_Off_Member_Lemma()
    {
        Assert.Equal("26.5", SourceEntityIdConventions.VerbNetClassFromSemLinkKey("26.5-shake"));
        Assert.Equal("13.1-1", SourceEntityIdConventions.VerbNetClassFromSemLinkKey("13.1-1-give"));
    }

    [Fact]
    public void NormalizeSenseKey_Canonicalizes_To_ThreeFields()
    {
        Assert.Equal("give%2:40:03", SourceEntityIdConventions.NormalizeSenseKey("give%2:40:03::"));
        Assert.Equal("ache%2:37:06", SourceEntityIdConventions.NormalizeSenseKey("?ache%2:37:06"));
        Assert.Null(SourceEntityIdConventions.NormalizeSenseKey("notasensekey"));
    }

    [Fact]
    public void FrameNetLuKey_Normalizes_Frame_And_LuName()
    {
        Assert.Equal("Giving/give.v", SourceEntityIdConventions.FrameNetLuKey("Giving", "give.v"));
        Assert.Equal("Accoutrements/accoutrement.n",
            SourceEntityIdConventions.FrameNetLuKey(" Accoutrements ", " accoutrement.n "));
    }

    [Fact]
    public void ParseMapNetSynsetKey_Parses_PosHashOffset()
    {
        Assert.Equal((57580L, 'a'), SourceEntityIdConventions.ParseMapNetSynsetKey("a#00057580"));
        Assert.Equal((1142646L, 'v'), SourceEntityIdConventions.ParseMapNetSynsetKey("v#01142646"));
        Assert.Equal((20977L, 'n'), SourceEntityIdConventions.ParseMapNetSynsetKey("n#00020977"));
        Assert.Null(SourceEntityIdConventions.ParseMapNetSynsetKey("NULL"));
    }

    [Fact]
    public void ParseMcrSynsetKey_Parses_PredicateMatrix_Ili_Tokens()
    {
        Assert.Equal((2244956L, 'v'), SourceEntityIdConventions.ParseMcrSynsetKey("ili-30-02244956-v"));
        Assert.Equal((941990L, 'v'), SourceEntityIdConventions.ParseMcrSynsetKey("30-00941990-v"));
        Assert.Null(SourceEntityIdConventions.ParseMcrSynsetKey("NULL"));
    }

    [Fact]
    public void ResolveSynsetAnchor_Parses_Mcr_WnRdf_And_SenseKeys()
    {
        string cili = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        if (File.Exists(Path.Combine(cili, IliMap.MapFileName)))
        {
            CodepointPerfcache.LoadDefault();
            Hash128? ili = SourceEntityIdConventions.ResolveSynsetAnchor("30-02244956-v");
            Assert.NotNull(ili);
            Assert.Equal(ConceptAnchor.SynsetId(2244956, 'v'), ili);
            Assert.Equal(
                ConceptAnchor.SynsetId(2244956, 'v'),
                SourceEntityIdConventions.ResolveSynsetAnchor(
                    "http://wordnet-rdf.princeton.edu/wn31/02244956-v"));
        }

        Hash128? sense = SourceEntityIdConventions.ResolveSynsetAnchor("give%2:40:03::");
        Assert.NotNull(sense);
        Assert.Equal(SenseAnchor.Id("give%2:40:03"), sense);

        Assert.Null(SourceEntityIdConventions.ResolveSynsetAnchor("communication"));
    }

    [Fact]
    public void StripPredicateMatrixNamespace_Removes_Type_Prefix()
    {
        Assert.Equal("eng", SourceEntityIdConventions.StripPredicateMatrixNamespace("id:eng"));
        Assert.Equal("ili-30-02244956-v", SourceEntityIdConventions.StripPredicateMatrixNamespace("ili-30-02244956-v"));
    }

    [Fact]
    public void EnsureCiliMapForIngest_Throws_When_Map_Missing()
    {
        WithCiliDir(NewTempDir(), () =>
        {
            var ex = Assert.Throws<CiliMapMissingException>(() =>
                SourceEntityIdConventions.EnsureCiliMapForIngest(NullLogger.Instance, "OMWDecomposer"));
            Assert.Equal(SourceEntityIdConventions.CiliMapPath(), ex.ExpectedPath);
            Assert.Contains("OMWDecomposer", ex.Message);
            Assert.Contains(IliMap.MapFileName, ex.Message);
        });
    }

    [Fact]
    public void EnsureCiliMapForIngest_Throws_When_Map_Empty()
    {
        WithCiliDir(NewTempDir(), () =>
        {
            File.WriteAllText(SourceEntityIdConventions.CiliMapPath(), "");
            Assert.Throws<CiliMapMissingException>(() =>
                SourceEntityIdConventions.EnsureCiliMapForIngest(NullLogger.Instance, "SemLinkDecomposer"));
        });
    }

    [Fact]
    public void EnsureCiliMapForIngest_Succeeds_When_Map_Has_Entries()
    {
        WithCiliDir(NewTempDir(), () =>
        {
            File.WriteAllText(SourceEntityIdConventions.CiliMapPath(),
                "i46531\t10676319-n\n");
            SourceEntityIdConventions.EnsureCiliMapForIngest(NullLogger.Instance, "OMWDecomposer");
            Assert.NotNull(SourceEntityIdConventions.WordNetIli(10676319, 'n'));
        });
    }

    [Fact]
    public void WarnIfCiliMapMissing_Does_Not_Throw_When_Map_Missing()
    {
        WithCiliDir(NewTempDir(), () =>
            SourceEntityIdConventions.WarnIfCiliMapMissing(NullLogger.Instance, "WordNetDecomposer"));
    }

    [Fact]
    public void CiliMapPath_Defaults_To_DataRoot_Cili()
    {
        _savedCiliDir = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR");
        _savedDataRoot = Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_CILI_DIR", null);
            Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", @"D:\Data\Ingest");
            SourceEntityIdConventions.ResetIliMapCacheForTests();
            Assert.Equal(
                Path.Combine(@"D:\Data\Ingest", "CILI", IliMap.MapFileName),
                SourceEntityIdConventions.CiliMapPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_CILI_DIR", _savedCiliDir);
            Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", _savedDataRoot);
            SourceEntityIdConventions.ResetIliMapCacheForTests();
        }
    }
}
