using Laplace.Decomposers.Model;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model.Tests;

public sealed class ModelAttestationTensorFillTests
{
    private static LlamaRecipeExtractor.RecipeInfo TinyRecipe() => new()
    {
        RecipeEntityId = Hash128.Zero,
        Architecture = "LlamaForCausalLM",
        HiddenSize = 4,
        NumLayers = 1,
        NumHeads = 2,
        NumKvHeads = 2,
        IntermediateSize = 8,
        VocabSize = 3,
        TorchDtype = "bfloat16",
        HiddenAct = "silu",
        RopeTheta = 10000,
        ModelType = "llama",
        CanonicalJson = [0x7b, 0x7d],
    };

    [Fact]
    public void Embeds_maps_token_to_hidden_column()
    {
        var recipe = TinyRecipe();
        var king = Hash128.Blake3("King"u8.ToArray());
        var feat2 = ModelFeatureEntityId.For("d", 2);
        var tok = new Dictionary<Hash128, int> { [king] = 1 };
        var feat = ModelFeatureEntityId.BuildIndex("d", recipe.HiddenSize);

        var mapped = ModelAttestationTensorFill.MapEdge(
            new ModelAttestationTensorFill.AttestationEdge(king, feat2, 1_500_000_000L),
            ModelDecomposer.EmbedsKind,
            "model.embed_tokens.weight",
            recipe,
            tok,
            feat,
            rows: recipe.VocabSize,
            cols: recipe.HiddenSize);

        Assert.Equal(ModelAttestationTensorFill.FillOutcome.Filled, mapped.Outcome);
        Assert.Equal(1, mapped.Row);
        Assert.Equal(2, mapped.Col);
    }

    [Fact]
    public void QProjects_on_hidden_square_is_shape_mismatch()
    {
        var recipe = TinyRecipe();
        var a = Hash128.Blake3("a"u8.ToArray());
        var b = Hash128.Blake3("b"u8.ToArray());
        var tok = new Dictionary<Hash128, int> { [a] = 0, [b] = 1 };

        var mapped = ModelAttestationTensorFill.MapEdge(
            new ModelAttestationTensorFill.AttestationEdge(a, b, 1L),
            ModelDecomposer.QProjectsKind,
            "model.layers.0.self_attn.q_proj.weight",
            recipe,
            tok,
            featureToDim: null,
            rows: recipe.HiddenSize,
            cols: recipe.HiddenSize);

        Assert.Equal(ModelAttestationTensorFill.FillOutcome.SkippedShapeMismatch, mapped.Outcome);
    }

    [Fact]
    public void FillBuffer_writes_embed_cell()
    {
        var recipe = TinyRecipe();
        var king = Hash128.Blake3("King"u8.ToArray());
        var feat1 = ModelFeatureEntityId.For("d", 1);
        var tok = new Dictionary<Hash128, int> { [king] = 0 };

        var buf = new byte[recipe.VocabSize * recipe.HiddenSize * 2];
        int n = ModelAttestationTensorFill.FillBuffer(
            [new ModelAttestationTensorFill.AttestationEdge(king, feat1, 2_000_000_000L)],
            ModelDecomposer.EmbedsKind,
            "model.embed_tokens.weight",
            recipe,
            tok,
            buf,
            recipe.VocabSize,
            recipe.HiddenSize,
            dtype: 2);

        Assert.Equal(1, n);
        Assert.True(ModelAttestationTensorFill.CountNonZeroCells(buf, dtype: 2) > 0);
    }
}
