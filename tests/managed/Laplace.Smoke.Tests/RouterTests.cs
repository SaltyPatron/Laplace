namespace Laplace.Smoke.Tests;

using System.IO;
using System.Text;

using Laplace.Pipeline;

using Xunit;

/// <summary>
/// F3 router tests — IModalityRouter (file extension + magic-byte
/// dispatch) and IModelArchitectureRouter (config.json architectures
/// dispatch). Both routers return canonical concept-name strings that
/// match substrate concept entities; no English-string enum types.
/// </summary>
public class RouterTests
{
    [Theory]
    [InlineData("foo.txt",         "text")]
    [InlineData("notes.md",        "text")]
    [InlineData("data.json",       "structured")]
    [InlineData("config.yaml",     "structured")]
    [InlineData("script.py",       "code")]
    [InlineData("Program.cs",      "code")]
    [InlineData("photo.png",       "image")]
    [InlineData("song.flac",       "audio")]
    [InlineData("clip.mp4",        "video")]
    [InlineData("doc.tex",         "math")]
    [InlineData("page.html",       "web")]
    [InlineData("model.safetensors","model")]
    [InlineData("region.geojson",  "geo")]
    [InlineData("part.step",       "cad")]
    [InlineData("flow.pcap",       "network")]
    [InlineData("score.midi",      "music")]
    [InlineData("genome.fasta",    "bio")]
    [InlineData("archive.zip",     "compressed")]
    public void ModalityRouter_DispatchByExtension(string artifactPath, string expectedModality)
    {
        var router = new ModalityRouter();
        using var emptyStream = new MemoryStream();
        var modality = router.Route(artifactPath, emptyStream);
        Assert.Equal(expectedModality, modality);
    }

    [Fact]
    public void ModalityRouter_UnknownExtension_FallsBackToOpaque()
    {
        var router = new ModalityRouter();
        using var emptyStream = new MemoryStream();
        var modality = router.Route("mystery.xyz", emptyStream);
        Assert.Equal(ModalityRouter.UnknownModality, modality);
    }

    [Fact]
    public void ModalityRouter_PngMagicBytes_DetectedEvenWithoutExtension()
    {
        var router = new ModalityRouter();
        var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        using var stream = new MemoryStream(pngHeader);
        var modality = router.Route("noextension", stream);
        Assert.Equal("image", modality);
    }

    [Fact]
    public void ModalityRouter_JpegMagicBytes_DetectedEvenWithoutExtension()
    {
        var router = new ModalityRouter();
        var jpegHeader = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        using var stream = new MemoryStream(jpegHeader);
        var modality = router.Route("noextension", stream);
        Assert.Equal("image", modality);
    }

    [Theory]
    [InlineData("LlamaForCausalLM",              "DecoderOnly")]
    [InlineData("Qwen2ForCausalLM",              "DecoderOnly")]
    [InlineData("T5ForConditionalGeneration",    "EncoderDecoder")]
    [InlineData("MarianMTModel",                 "EncoderDecoder")]
    [InlineData("BertModel",                     "EncoderOnly")]
    [InlineData("XLMRobertaModel",               "EncoderOnly")]
    [InlineData("BertForSequenceClassification", "Reranker")]
    [InlineData("ViTForImageClassification",     "VisionEncoder")]
    [InlineData("Wav2Vec2ForCTC",                "AudioEncoder")]
    [InlineData("UNet2DConditionModel",          "Diffusion")]
    [InlineData("CLIPModel",                     "Multimodal")]
    [InlineData("MixtralForCausalLM",            "MoE")]
    [InlineData("DeepseekV3ForCausalLM",         "MoeMla")]
    public void ModelArchitectureRouter_DispatchByArchitectureString(string architecture, string expectedFamily)
    {
        var dir = CreateModelDir(architecture);
        try
        {
            var router = new ModelArchitectureRouter();
            var family = router.Route(dir);
            Assert.Equal(expectedFamily, family);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ModelArchitectureRouter_NoConfig_ReturnsUnknown()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var router = new ModelArchitectureRouter();
            Assert.Equal(ModelArchitectureRouter.Unknown, router.Route(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ModelArchitectureRouter_ConfigWithoutArchitectures_ReturnsUnknown()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), "{\"hidden_size\": 768}");
            var router = new ModelArchitectureRouter();
            Assert.Equal(ModelArchitectureRouter.Unknown, router.Route(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateModelDir(string architecture)
    {
        var dir = Directory.CreateTempSubdirectory("laplace_router_test_").FullName;
        var configContent = $"{{\"architectures\": [\"{architecture}\"], \"model_type\": \"test\"}}";
        File.WriteAllText(Path.Combine(dir, "config.json"), configContent, Encoding.UTF8);
        return dir;
    }
}
