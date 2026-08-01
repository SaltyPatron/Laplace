using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// #540 / #541 — config-witnessed activation identity and norm epsilon must
/// reach ArchitectureProfile and the FFN act dispatch, not sit unread on the recipe.
/// </summary>
public sealed class ArchitectureProfileWitnessTests
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

    [Fact]
    public void ArchitectureProfile_For_overlays_witnessed_scalars()
    {
        var cfg = new ModelConfig
        {
            ModelType = "bert",
            Architecture = "BertModel",
            VocabSize = 8,
            HiddenSize = 4,
            NumLayers = 1,
            NumHeads = 1,
            NumKvHeads = 1,
            HeadDim = 4,
            IntermediateSize = 4,
            NumExperts = 0,
            TieWordEmbeddings = false,
            QkNorm = false,
            RopeTheta = 10000,
            NormEps = 2.5e-12,
            HiddenAct = "gelu_new",
            MlaQLoraRank = 0,
            MlaKvLoraRank = 0,
            QkRopeHeadDim = 0,
            QkNopeHeadDim = 0,
            VHeadDim = 0,
            RecipeEntityId = Hash128.Zero,
            CanonicalJson = Encoding.UTF8.GetBytes("{}"),
        };

        var profile = ArchitectureProfile.For(cfg);
        Assert.Equal(2.5e-12, profile.NormEps);
        Assert.Equal("gelu_new", profile.HiddenAct);
        Assert.Equal(1, profile.ResolveFfnActCode(gatePresent: false));
        Assert.Equal(1, profile.ResolveFfnActCode(gatePresent: true)); // GELU ignores gate
    }

    [Fact]
    public void ResolveFfnActCode_silu_requires_gate_tensor()
    {
        var p = ArchitectureProfile.Llama with { HiddenAct = "silu", NormEps = 3e-5 };
        Assert.Equal(0, p.ResolveFfnActCode(gatePresent: true));
        Assert.Equal(1, p.ResolveFfnActCode(gatePresent: false));
        Assert.Equal(3e-5, p.NormEps);
    }

    [Fact]
    public void Empty_hidden_act_keeps_family_default()
    {
        var cfg = new ModelConfig
        {
            ModelType = "phi",
            Architecture = "PhiForCausalLM",
            VocabSize = 8,
            HiddenSize = 4,
            NumLayers = 1,
            NumHeads = 1,
            NumKvHeads = 1,
            HeadDim = 4,
            IntermediateSize = 4,
            NumExperts = 0,
            TieWordEmbeddings = false,
            QkNorm = false,
            RopeTheta = 10000,
            NormEps = 1e-5,
            HiddenAct = "",
            MlaQLoraRank = 0,
            MlaKvLoraRank = 0,
            QkRopeHeadDim = 0,
            QkNopeHeadDim = 0,
            VHeadDim = 0,
            RecipeEntityId = Hash128.Zero,
            CanonicalJson = Encoding.UTF8.GetBytes("{}"),
        };
        var profile = ArchitectureProfile.For(cfg);
        Assert.Equal("gelu_new", profile.HiddenAct);
        Assert.Equal(1, profile.ResolveFfnActCode(gatePresent: false));
    }
}
