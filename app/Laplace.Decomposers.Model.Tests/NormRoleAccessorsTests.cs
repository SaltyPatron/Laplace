using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Model.Tests;

// Track A1 — the norm sub-role accessors disambiguate the multiple [d] norms in a block by NAME
// across every architecture family in the local model zoo. Pure, no DB. These gate the norm-fold
// (A2): if InputNorm/PostAttnNorm/QNorm/etc. pick the wrong tensor, every folded plane is mis-scaled.
public class NormRoleAccessorsTests
{
    private static TensorRole Norm(string name, int layer) =>
        new(name, new[] { 4 }, "F32", TensorRoleKind.Norm, layer, ExpertIndex: -1);

    private static ModelManifest Manifest(params TensorRole[] roles) => new()
    {
        Config = new ModelConfig
        {
            ModelType = "test", Architecture = "Test",
            VocabSize = 8, HiddenSize = 4, NumLayers = 1, NumHeads = 1, NumKvHeads = 1,
            HeadDim = 4, IntermediateSize = 4, NumExperts = 0,
            TieWordEmbeddings = false, QkNorm = false, RopeTheta = 10000, NormEps = 1e-5,
            MlaQLoraRank = 0, MlaKvLoraRank = 0, QkRopeHeadDim = 0, QkNopeHeadDim = 0, VHeadDim = 0,
            RecipeEntityId = Hash128.OfCanonical("substrate/test/norm/recipe"),
            CanonicalJson = Encoding.UTF8.GetBytes("{}"),
        },
        Roles = roles, Modality = Modality.Text, Coverage = Coverage.Full, ModelName = "norm-test",
    };

    [Fact]
    public void Llama_TwoBlockNorms_Disambiguated()
    {
        var m = Manifest(
            Norm("model.layers.0.input_layernorm.weight", 0),
            Norm("model.layers.0.post_attention_layernorm.weight", 0),
            Norm("model.norm.weight", -1));   // final model norm — must NOT leak into layer 0

        Assert.Equal("model.layers.0.input_layernorm.weight", m.InputNorm(0)!.Name);
        Assert.Equal("model.layers.0.post_attention_layernorm.weight", m.PostAttnNorm(0)!.Name);
        Assert.Null(m.QNorm(0));
        Assert.Null(m.KNorm(0));
        Assert.Null(m.QaLatentNorm(0));
        Assert.Null(m.KvaLatentNorm(0));
    }

    [Fact]
    public void Qwen3_PerHeadQkNorms_Disambiguated()
    {
        var m = Manifest(
            Norm("model.layers.0.input_layernorm.weight", 0),
            Norm("model.layers.0.post_attention_layernorm.weight", 0),
            Norm("model.layers.0.self_attn.q_norm.weight", 0),
            Norm("model.layers.0.self_attn.k_norm.weight", 0));

        Assert.Equal("model.layers.0.self_attn.q_norm.weight", m.QNorm(0)!.Name);
        Assert.Equal("model.layers.0.self_attn.k_norm.weight", m.KNorm(0)!.Name);
        // q/k norms must not be mistaken for the block input/post norms.
        Assert.Equal("model.layers.0.input_layernorm.weight", m.InputNorm(0)!.Name);
        Assert.Equal("model.layers.0.post_attention_layernorm.weight", m.PostAttnNorm(0)!.Name);
    }

    [Fact]
    public void Phi2_ParallelBlock_SingleNorm_FallsBackForPostAttn()
    {
        // Phi-2's parallel block has ONE input_layernorm feeding both attention and MLP.
        var m = Manifest(Norm("model.layers.0.input_layernorm.weight", 0));

        Assert.Equal("model.layers.0.input_layernorm.weight", m.InputNorm(0)!.Name);
        // PostAttnNorm has no post_attention tensor → must fall back to the input norm, not null.
        Assert.Equal("model.layers.0.input_layernorm.weight", m.PostAttnNorm(0)!.Name);
    }

    [Fact]
    public void SingleUnnamedNorm_ResolvesAsInputByElimination()
    {
        // A lone, non-standardly-named norm in a layer is the input norm by elimination.
        var m = Manifest(Norm("model.layers.0.ln.weight", 0));
        Assert.Equal("model.layers.0.ln.weight", m.InputNorm(0)!.Name);
    }

    [Fact]
    public void Mla_LatentNorms_Disambiguated()
    {
        var m = Manifest(
            Norm("model.layers.0.input_layernorm.weight", 0),
            Norm("model.layers.0.post_attention_layernorm.weight", 0),
            Norm("model.layers.0.self_attn.q_a_layernorm.weight", 0),
            Norm("model.layers.0.self_attn.kv_a_layernorm.weight", 0));

        Assert.Equal("model.layers.0.self_attn.q_a_layernorm.weight", m.QaLatentNorm(0)!.Name);
        Assert.Equal("model.layers.0.self_attn.kv_a_layernorm.weight", m.KvaLatentNorm(0)!.Name);
        // latent norms must not be mistaken for the block input norm.
        Assert.Equal("model.layers.0.input_layernorm.weight", m.InputNorm(0)!.Name);
    }
}
