using Xunit;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// #540 / #541: the config's declared activation and norm epsilon reach the FFN
/// operator dispatch; an activation the native operator lacks is reported, never
/// replaced by another function.
/// </summary>
public sealed class ModelConfigWitnessTests
{
    [Fact]
    public void ModelConfigReader_reads_hidden_act_and_norm_eps()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), """
                {
                  "model_type": "llama",
                  "architectures": ["LlamaForCausalLM"],
                  "vocab_size": 32,
                  "hidden_size": 16,
                  "num_hidden_layers": 2,
                  "num_attention_heads": 4,
                  "num_key_value_heads": 2,
                  "intermediate_size": 64,
                  "hidden_act": "silu",
                  "rms_norm_eps": 1e-5
                }
                """);
            var r = ModelConfigReader.Read(Path.Combine(dir, "config.json"));
            Assert.Equal("silu", r.Config.HiddenAct);
            Assert.Equal(1e-5, r.Config.NormEps);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ModelConfigReader_prefers_hidden_activation_alias()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), """
                {
                  "model_type": "gemma",
                  "vocab_size": 8,
                  "hidden_size": 8,
                  "num_hidden_layers": 1,
                  "num_attention_heads": 2,
                  "intermediate_size": 16,
                  "hidden_activation": "gelu_pytorch_tanh",
                  "rms_norm_eps": 1e-6
                }
                """);
            var r = ModelConfigReader.Read(Path.Combine(dir, "config.json"));
            Assert.Equal("gelu_pytorch_tanh", r.Config.HiddenAct);
            Assert.Equal(1e-6, r.Config.NormEps);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("silu", true, 0)]
    [InlineData("silu", false, 5)]
    [InlineData("gelu", false, 1)]
    [InlineData("gelu_pytorch_tanh", false, 2)]
    [InlineData("", true, 0)]
    public void DeclaredActivation_SelectsItsNativeFfnOperator(string act, bool gated, int expected)
    {
        Assert.Equal(expected, Manifest(act).FfnActivation(gated));
    }

    [Fact]
    public void UndeclaredActivation_HasNoNativeOperatorRatherThanASubstitute()
    {
        Assert.Null(Manifest("xielu").FfnActivation(gated: true));
        Assert.Null(Manifest("").FfnActivation(gated: false));
    }

    private static ModelManifest Manifest(string act)
    {
        var config = new ModelConfig
        {
            ModelType = "llama", Architecture = "LlamaForCausalLM",
            VocabSize = 4, HiddenSize = 2, NumLayers = 0, NumHeads = 1, NumKvHeads = 1,
            HeadDim = 2, IntermediateSize = 4, NumExperts = 0,
            TieWordEmbeddings = true, QkNorm = false, RopeTheta = 10000, NormEps = 1e-5,
            HiddenAct = act, MlaQLoraRank = 0, MlaKvLoraRank = 0,
            QkRopeHeadDim = 0, QkNopeHeadDim = 0, VHeadDim = 0,
        };
        return ModelManifest.Recognize(
            new HeaderTensor[] { new("embed_tokens.weight", "F32", [4, 2]) },
            new ModelConfigReader.Result(config, Modality.Text, Coverage.Full,
                new Dictionary<string, long> { ["hidden_size"] = 2, ["num_attention_heads"] = 1 }),
            4, "activation-fixture");
    }
}
