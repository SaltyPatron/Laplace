using Xunit;

namespace Laplace.Decomposers.Model.Tests;






public class RealModelManifestTests
{
    private static string HubRoot =>
        TestInstall.ResolveModelHubOrFallback();

    public sealed record Expect(
        string HubDir, string ModelType, int Vocab, int Hidden, int Layers, int Heads, int KvHeads,
        int Interm, bool Tied, bool Moe, bool Mla);

    public static IEnumerable<object[]> Models() => new[]
    {
        new object[] { new Expect("models--TinyLlama--TinyLlama-1.1B-Chat-v1.0", "llama", 32000, 2048, 22, 32, 4, 5632, false, false, false) },
        new object[] { new Expect("models--Qwen--Qwen2.5-Coder-3B-Instruct", "qwen2", 151936, 2048, 36, 16, 2, 11008, true, false, false) },
        new object[] { new Expect("models--Qwen--Qwen3-Embedding-0.6B", "qwen3", 151669, 1024, 28, 16, 8, 3072, true, false, false) },
        new object[] { new Expect("models--deepseek-ai--DeepSeek-Coder-V2-Lite-Instruct", "deepseek_v2", 102400, 2048, 27, 16, 16, 10944, false, true, true) },
        new object[] { new Expect("models--microsoft--phi-2", "phi", 51200, 2560, 32, 32, 32, 10240, false, false, false) },
    };

    private static bool HasModel(string dir) =>
        File.Exists(Path.Combine(dir, "config.json")) && Directory.GetFiles(dir, "*.safetensors").Length > 0;

    private static string? ResolveSnapshot(string hubDir)
    {
        string root = Path.Combine(HubRoot, hubDir);
        if (!Directory.Exists(root)) return null;
        if (HasModel(root)) return root;
        string snaps = Path.Combine(root, "snapshots");
        if (Directory.Exists(snaps))
            foreach (var d in Directory.GetDirectories(snaps))
                if (HasModel(d)) return d;
        return null;
    }

    private static ModelManifest Parse(string dir, string name)
    {
        var cfg = ModelConfigReader.Read(Path.Combine(dir, "config.json"));
        var headers = SafetensorsContainerParser.ParseModel(dir);
        return TensorRoleClassifier.Build(headers, cfg, name);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Manifest_MatchesRealModel(Expect e)
    {
        string? dir = ResolveSnapshot(e.HubDir);
        if (dir is null) return;

        var cfgResult = ModelConfigReader.Read(Path.Combine(dir, "config.json"));
        var headers = SafetensorsContainerParser.ParseModel(dir);
        var m = TensorRoleClassifier.Build(headers, cfgResult, e.HubDir);
        var c = m.Config;


        Assert.Equal(e.ModelType, c.ModelType);
        Assert.Equal(e.Vocab, c.VocabSize);
        Assert.Equal(e.Hidden, c.HiddenSize);
        Assert.Equal(e.Layers, c.NumLayers);
        Assert.Equal(e.Heads, c.NumHeads);
        Assert.Equal(e.KvHeads, c.NumKvHeads);
        Assert.Equal(e.Interm, c.IntermediateSize);
        Assert.Equal(e.Tied, c.TieWordEmbeddings);
        Assert.Equal(e.Moe, c.IsMoe);
        Assert.Equal(e.Mla, c.IsMla);


        Assert.Equal(Modality.Text, m.Modality);
        Assert.Equal(Coverage.Full, m.Coverage);
        Assert.NotNull(m.Embedding);
        Assert.NotNull(m.LmHead);
        if (e.Tied)
            Assert.Equal(m.Embedding, m.LmHead);
        else
            Assert.NotEqual(m.Embedding, m.LmHead);


        Assert.NotNull(m.InputNorm(0));
        if (!e.Mla)
            Assert.NotNull(m.PostAttnNorm(0));


        if (e.ModelType == "qwen3")
        {
            Assert.NotNull(m.QNorm(0));
            Assert.NotNull(m.KNorm(0));
        }
        else
        {
            Assert.Null(m.QNorm(0));
            Assert.Null(m.KNorm(0));
        }


        if (e.Mla)
            Assert.NotNull(m.KvaLatentNorm(0));


        if (!e.Mla)
        {
            Assert.NotNull(m.Single(0, TensorRoleKind.AttnQ));
            Assert.NotNull(m.Single(0, TensorRoleKind.AttnK));
            Assert.NotNull(m.Single(0, TensorRoleKind.AttnV));
            Assert.NotNull(m.Single(0, TensorRoleKind.AttnO));
        }

        Assert.NotNull(m.Single(0, TensorRoleKind.MlpDown));
    }



    [Theory]
    [InlineData("DETR-ResNet-101")]
    [InlineData("RT-DETR-v1-R101")]
    [InlineData("Conditional-DETR-R50")]
    [InlineData("Grounding-DINO-Base")]
    public void VisionDetection_NeverRunsTextPlanes(string hubDir)
    {
        string? dir = ResolveSnapshot(hubDir);
        if (dir is null) return;
        var m = Parse(dir, hubDir);
        Assert.False(m.TextPlanesRunnable,
            $"{hubDir} (type={m.Config.ModelType}, modality={m.Modality}, coverage={m.Coverage}) must not run text planes");
    }



    [Theory]
    [InlineData("models--sentence-transformers--all-MiniLM-L6-v2")]
    [InlineData("Florence-2-base")]
    [InlineData("models--Qwen--Qwen3-VL-Embedding-2B")]
    [InlineData("models--Qwen--Qwen3-Reranker-0.6B")]
    [InlineData("models--jinaai--jina-code-embeddings-1.5b")]
    [InlineData("models--nvidia--canary-qwen-2.5b")]
    public void AnyFormat_ParsesWithoutCrashing(string hubDir)
    {
        string? dir = ResolveSnapshot(hubDir);
        if (dir is null) return;
        var ex = Record.Exception(() =>
        {
            var m = Parse(dir, hubDir);
            Assert.True(Enum.IsDefined(m.Coverage));
        });
        Assert.True(ex is null, $"{hubDir} parse threw: {ex?.GetType().Name}: {ex?.Message}");
    }
}
