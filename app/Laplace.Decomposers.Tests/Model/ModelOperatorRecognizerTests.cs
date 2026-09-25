using Xunit;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// Shape decides what a checkpoint tensor is; a name only breaks a symmetry
/// between slots of equal shape; what neither decides is reported, not guessed.
/// </summary>
public sealed class ModelOperatorRecognizerTests
{
    private static HeaderTensor T(string name, params int[] shape) => new(name, "BF16", shape);

    private static IEnumerable<HeaderTensor> LlamaBlock(int b, int d, int q, int kv, int f)
    {
        string p = $"model.layers.{b}.";
        yield return T(p + "input_layernorm.weight", d);
        yield return T(p + "post_attention_layernorm.weight", d);
        yield return T(p + "self_attn.q_proj.weight", q, d);
        yield return T(p + "self_attn.k_proj.weight", kv, d);
        yield return T(p + "self_attn.v_proj.weight", kv, d);
        yield return T(p + "self_attn.o_proj.weight", d, q);
        yield return T(p + "mlp.gate_proj.weight", f, d);
        yield return T(p + "mlp.up_proj.weight", f, d);
        yield return T(p + "mlp.down_proj.weight", d, f);
    }

    [Fact]
    public void GroupedQueryLlama_IsRecognizedWithoutAFamilyTable()
    {
        var tensors = new List<HeaderTensor>
        {
            T("model.embed_tokens.weight", 320, 64), T("lm_head.weight", 320, 64), T("model.norm.weight", 64),
        };
        for (int b = 0; b < 3; b++) tensors.AddRange(LlamaBlock(b, 64, 64, 16, 176));
        var config = new Dictionary<string, long>
        {
            ["num_attention_heads"] = 8, ["num_key_value_heads"] = 2, ["intermediate_size"] = 176,
            ["hidden_size"] = 64, ["num_hidden_layers"] = 3,
        };

        ModelAnatomy a = ModelOperatorRecognizer.Recognize(tensors, config, 320);

        Assert.Empty(a.Unrecognized);
        Assert.Empty(a.Conflicts);
        Assert.Equal(64, a.Symbol("d"));
        Assert.Equal(8, a.Symbol("d_h"));
        Assert.Equal(3, a.Operators.Count(o => o.Operator == "self-attention"));
        OperatorInstance attention = a.Operators.First(o => o.Operator == "self-attention");
        // q and o share [64, 64]; k and v share [16, 64]: the names break both symmetries.
        Assert.EndsWith("q_proj.weight", attention.Slot("q")!.Tensor);
        Assert.EndsWith("o_proj.weight", attention.Slot("o")!.Tensor);
        Assert.EndsWith("k_proj.weight", attention.Slot("k")!.Tensor);
        Assert.EndsWith("v_proj.weight", attention.Slot("v")!.Tensor);
        OperatorInstance vocab = Assert.Single(a.Operators, o => o.Family == "vocabulary");
        Assert.Equal("model.embed_tokens.weight", vocab.Slot("embedding")!.Tensor);
        Assert.Equal("lm_head.weight", vocab.Slot("unembedding")!.Tensor);
    }

    [Fact]
    public void TransposedFusedAttention_IsRecognizedInItsStoredOrientation()
    {
        // GPT-2 stores Conv1D weights as [in, out] and fuses q, k and v.
        var tensors = new List<HeaderTensor>
        {
            T("wte.weight", 500, 48), T("wpe.weight", 128, 48), T("ln_f.weight", 48), T("ln_f.bias", 48),
        };
        for (int b = 0; b < 2; b++)
        {
            string p = $"h.{b}.";
            tensors.AddRange(
            [
                T(p + "ln_1.weight", 48), T(p + "ln_1.bias", 48),
                T(p + "attn.c_attn.weight", 48, 144), T(p + "attn.c_attn.bias", 144),
                T(p + "attn.c_proj.weight", 48, 48), T(p + "attn.c_proj.bias", 48),
                T(p + "ln_2.weight", 48), T(p + "ln_2.bias", 48),
                T(p + "mlp.c_fc.weight", 48, 192), T(p + "mlp.c_fc.bias", 192),
                T(p + "mlp.c_proj.weight", 192, 48), T(p + "mlp.c_proj.bias", 48),
            ]);
        }
        var config = new Dictionary<string, long> { ["n_embd"] = 48, ["n_head"] = 4, ["n_layer"] = 2, ["n_positions"] = 128 };

        ModelAnatomy a = ModelOperatorRecognizer.Recognize(tensors, config, 500);

        Assert.Empty(a.Unrecognized);
        OperatorInstance attention = a.Operators.First(o => o.Family == "attention");
        Assert.Equal("self-attention-fused", attention.Operator);
        Assert.Equal("in,out", attention.Slot("qkv")!.Orientation);
        Assert.Equal("h.0.attn.c_attn.bias", attention.Slot("qkv")!.Bias);
        OperatorInstance mlp = a.Operators.First(o => o.Family == "mlp");
        Assert.Equal("plain-mlp", mlp.Operator);
        Assert.Equal("h.0.mlp.c_fc.weight", mlp.Slot("up")!.Tensor);
        Assert.Equal("in,out", mlp.Slot("up")!.Orientation);
        Assert.Equal(192, mlp.InstanceSymbols["w"]);
        Assert.Contains(a.Operators, o => o.Operator == "position-embedding");
    }

    [Fact]
    public void EqualShapesWithoutAnyHint_AreAmbiguousNotGuessed()
    {
        var tensors = new[] { T("a.weight", 100, 16), T("b.weight", 100, 16), T("c.weight", 16) };
        ModelAnatomy a = ModelOperatorRecognizer.Recognize(
            tensors, new Dictionary<string, long> { ["hidden_size"] = 16 }, 100);

        OperatorInstance vocab = Assert.Single(a.Operators, o => o.Family == "vocabulary");
        SlotBinding embedding = vocab.Slot("embedding")!;
        Assert.True(embedding.IsAmbiguous);
        Assert.Null(embedding.Tensor);
        Assert.Equal(["a.weight", "b.weight"], embedding.Candidates);
    }

    [Fact]
    public void UnfamiliarArchitecture_IsReportedUnrecognized_NeverThrown()
    {
        var tensors = new[]
        {
            T("backbone.conv1.weight", 64, 3, 7, 7), T("backbone.bn1.running_mean", 64),
            T("head.anchor_points", 900, 4), T("head.class_embed.weight", 91, 256),
        };
        ModelAnatomy a = ModelOperatorRecognizer.Recognize(tensors, new Dictionary<string, long>(), null);

        Assert.Equal(tensors.Length, a.Unrecognized.Count);
        Assert.DoesNotContain(a.Operators, o => o.Family is "attention" or "mlp" or "vocabulary");
    }

    [Fact]
    public void RoutedExperts_KeepTheirOwnWidth()
    {
        var tensors = new List<HeaderTensor>
        {
            T("model.embed_tokens.weight", 64, 32),
            T("model.layers.0.self_attn.q_proj.weight", 32, 32), T("model.layers.0.self_attn.k_proj.weight", 8, 32),
            T("model.layers.0.self_attn.v_proj.weight", 8, 32), T("model.layers.0.self_attn.o_proj.weight", 32, 32),
            T("model.layers.0.mlp.gate.weight", 4, 32),
        };
        for (int e = 0; e < 4; e++)
            tensors.AddRange(
            [
                T($"model.layers.0.mlp.experts.{e}.gate_proj.weight", 24, 32),
                T($"model.layers.0.mlp.experts.{e}.up_proj.weight", 24, 32),
                T($"model.layers.0.mlp.experts.{e}.down_proj.weight", 32, 24),
            ]);
        var config = new Dictionary<string, long>
        {
            ["hidden_size"] = 32, ["num_attention_heads"] = 4, ["num_key_value_heads"] = 1,
            ["num_experts"] = 4, ["intermediate_size"] = 96,
        };

        ModelAnatomy a = ModelOperatorRecognizer.Recognize(tensors, config, 64);

        Assert.Empty(a.Unrecognized);
        Assert.Single(a.Operators, o => o.Operator == "moe-router");
        var experts = a.Operators.Where(o => o.Scope == "expert").ToArray();
        Assert.Equal(4, experts.Length);
        Assert.All(experts, o => Assert.Equal(24, o.InstanceSymbols["w"]));
        Assert.Contains(a.Conflicts, c => c.StartsWith("f:", StringComparison.Ordinal));
    }

    [Fact]
    public void GovernedManifest_DeclaresEverySymbolItsSlotsUse()
    {
        OperatorTemplateManifest m = OperatorTemplateManifest.Governed;
        Assert.Contains(m.Operators, o => o.Name == "self-attention");
        Assert.Contains(m.Operators, o => o.Name == "self-attention-fused");
        Assert.Contains(m.Operators, o => o.Name == "gated-mlp");
        Assert.Contains(m.Operators, o => o.Name == "moe-router");
        Assert.Contains(m.Operators, o => o.Name == "low-rank-factor" && o.Repeat);
        Assert.Equal(["d", "V", "L", "h", "h_kv", "d_h", "f", "E"],
            m.Symbols.Select(s => s.Name).Take(8).ToArray());
    }
}
