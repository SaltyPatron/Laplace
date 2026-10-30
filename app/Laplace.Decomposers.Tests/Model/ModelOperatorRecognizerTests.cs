using Xunit;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// Shape decides what a checkpoint tensor is; a name only breaks a symmetry
/// between slots of equal shape; what neither decides is reported, not guessed.
/// </summary>
public sealed class ModelOperatorRecognizerTests
{
    private static HeaderTensor T(string name, params int[] shape) => new(name, "BF16", shape);

    [Fact]
    public void NumericAncestors_AreNotAssumedToBeLayerOrExpertOrdinals()
    {
        ModelAnatomy anatomy = ModelOperatorRecognizer.Recognize(
            [T("package.revision.99.blocks.3.fc1.weight", 48, 16), T("package.revision.99.blocks.3.fc2.weight", 16, 48)],
            new Dictionary<string, long> { ["hidden_size"] = 16 }, null);
        OperatorInstance op = Assert.Single(anatomy.Operators, o => o.Family == "mlp");
        Assert.Equal("block", op.Scope);
        Assert.Equal(3, op.Block);
        Assert.Equal(-1, op.Expert);
        Assert.Equal("package.revision.99.blocks.3", op.SourcePath);
    }

    [Fact]
    public void RecipeDeclaredSourceScope_DoesNotRequireARecognizerCodeChange()
    {
        OperatorTemplateManifest recipe = OperatorTemplateManifest.Parse("""
            [[scope]]
            name = "operator-body"
            after = ["segments"]
            [[symbol]]
            name = "d"
            bind = "config"
            config = ["hidden_size"]
            [[symbol]]
            name = "w"
            bind = "instance"
            [[operator]]
            name = "affine-pair"
            family = "mlp"
            scopes = ["operator-body"]
            locality = "owner"
            [[slot]]
            operator = "affine-pair"
            role = "up"
            shape = ["w", "d"]
            required = true
            [[slot]]
            operator = "affine-pair"
            role = "down"
            shape = ["d", "w"]
            required = true
            """);
        ModelAnatomy anatomy = ModelOperatorRecognizer.Recognize(
            [T("package.outer.99.segments.7.up.weight", 48, 16), T("package.outer.99.segments.7.down.weight", 16, 48)],
            new Dictionary<string, long> { ["hidden_size"] = 16 }, null, recipe);
        OperatorInstance op = Assert.Single(anatomy.Operators);
        Assert.Equal("operator-body", op.Scope);
        Assert.Equal("package.outer.99.segments.7", op.SourcePath);
        Assert.Empty(anatomy.Unrecognized);
    }

    [Fact]
    public void RepeatedOrdinalsUnderDifferentSourcePaths_DoNotShareOperands()
    {
        var tensors = new[]
        {
            T("package.left.blocks.0.fc1.weight", 48, 16),
            T("package.left.blocks.0.fc2.weight", 16, 48),
            T("package.right.blocks.0.fc1.weight", 64, 16),
            T("package.right.blocks.0.fc2.weight", 16, 64),
        };
        ModelAnatomy anatomy = ModelOperatorRecognizer.Recognize(tensors,
            new Dictionary<string, long> { ["d_model"] = 16 }, null);

        OperatorInstance[] operators = anatomy.Operators.Where(o => o.Family == "mlp").ToArray();
        Assert.Equal(2, operators.Length);
        Assert.Empty(anatomy.Unrecognized);
        Assert.All(operators, op =>
        {
            Assert.Equal("plain-mlp", op.Operator);
            Assert.All(op.Slots.Where(s => s.IsBound), slot =>
                Assert.StartsWith(op.SourcePath + ".", slot.Tensor!));
        });
        Assert.Equal(["package.left.blocks.0", "package.right.blocks.0"],
            operators.Select(o => o.SourcePath).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ConfiguredWidth_IsNotReplacedByAnUnrelatedBackboneAxis()
    {
        var tensors = new List<HeaderTensor>
        {
            T("model.encoder.layers.0.fc1.weight", 2048, 256),
            T("model.encoder.layers.0.fc2.weight", 256, 2048),
        };
        for (int i = 0; i < 20; i++)
            tensors.Add(T($"backbone.stages.{i}.convolution.weight", 64, 64, 3, 3));
        ModelAnatomy anatomy = ModelOperatorRecognizer.Recognize(tensors,
            new Dictionary<string, long> { ["d_model"] = 256 }, null);

        Assert.Equal(256, anatomy.Symbol("d"));
        Assert.Equal("config", anatomy.Symbols["d"].Basis);
        OperatorInstance mlp = Assert.Single(anatomy.Operators, o => o.Family == "mlp");
        Assert.Equal("plain-mlp", mlp.Operator);
        Assert.Equal("model.encoder.layers.0.fc1.weight", mlp.Slot("up")!.Tensor);
        Assert.Equal("model.encoder.layers.0.fc2.weight", mlp.Slot("down")!.Tensor);
    }

    [Fact]
    public void GatedOperator_RequiresDistinctTensorsForItsRequiredOperands()
    {
        ModelAnatomy anatomy = ModelOperatorRecognizer.Recognize(
            [T("layers.0.fc1.weight", 48, 16), T("layers.0.fc2.weight", 16, 48)],
            new Dictionary<string, long> { ["hidden_size"] = 16 }, null);

        OperatorInstance mlp = Assert.Single(anatomy.Operators, o => o.Family == "mlp");
        Assert.Equal("plain-mlp", mlp.Operator);
        Assert.All(mlp.Slots, slot => Assert.False(slot.IsAmbiguous));
    }

    [Fact]
    public void SourcePaths_SurviveManifestConversion_AndUnqualifiedLookupCannotMixThem()
    {
        var headers = new[]
        {
            T("left.blocks.0.fc1.weight", 48, 16), T("left.blocks.0.fc2.weight", 16, 48),
            T("right.blocks.0.fc1.weight", 64, 16), T("right.blocks.0.fc2.weight", 16, 64),
        }.Select(t => new SafetensorsContainerParser.TensorReference
        {
            Name = t.Name, Dtype = t.Dtype, Shape = t.Shape,
            DataStart = 0, DataEnd = 0, HeaderBytes = 0,
        }).ToArray();
        ModelConfigReader.Result config = ModelConfigReader.Read(
            System.Text.Encoding.UTF8.GetBytes("""{"model_type":"bert","d_model":16}"""));
        ModelManifest manifest = ModelManifest.Recognize(headers, config, null, "two branches");

        Assert.Throws<InvalidDataException>(() => manifest.Single(0, TensorRoleKind.MlpUp));
        Assert.Equal("left.blocks.0.fc1.weight", manifest.Single("left.blocks.0", TensorRoleKind.MlpUp)!.Name);
        Assert.Equal("right.blocks.0.fc1.weight", manifest.Single("right.blocks.0", TensorRoleKind.MlpUp)!.Name);
    }

    [Fact]
    public void RecipeContexts_KeepEncoderDecoderAndCrossAttentionOperandsSeparate()
    {
        var tensors = new List<HeaderTensor>();
        foreach (string owner in new[]
                 { "package.encoder.layers.0.self_attn", "package.decoder.layers.0.self_attn", "package.decoder.layers.0.encoder_attn" })
            foreach (string projection in new[] { "q_proj", "k_proj", "v_proj", "out_proj" })
                tensors.Add(T($"{owner}.{projection}.weight", 16, 16));
        ModelAnatomy anatomy = ModelOperatorRecognizer.Recognize(tensors,
            new Dictionary<string, long>
            {
                ["d_model"] = 16, ["encoder_attention_heads"] = 4, ["decoder_attention_heads"] = 8,
            }, null);

        OperatorInstance[] attention = anatomy.Operators.Where(o => o.Family == "attention").ToArray();
        Assert.Equal(3, attention.Length);
        Assert.Empty(anatomy.Unrecognized);
        foreach (OperatorInstance op in attention)
        {
            long heads = op.SourcePath.StartsWith("package.encoder.", StringComparison.Ordinal) ? 4 : 8;
            Assert.Equal(heads, op.ContextSymbols["h"]);
            Assert.Equal(heads, op.ContextSymbols["h_kv"]);
            Assert.Equal(16 / heads, op.ContextSymbols["d_h"]);
            Assert.Equal(4, op.Slots.Count(s => s.IsBound));
            Assert.All(op.Slots.Where(s => s.IsBound), slot =>
            {
                Assert.StartsWith(op.SourcePath + ".", slot.Tensor!);
            });
        }
    }

    [Fact]
    public void NestedConfigDeclarations_KeepTheirPathsAndBindTheirOwnContexts()
    {
        var config = ModelConfigReader.Read(System.Text.Encoding.UTF8.GetBytes("""
            {"text_config":{"d_model":16,"encoder_attention_heads":4,"decoder_attention_heads":8},
             "vision_config":{"hidden_size":64},"escaped~/":{"values":[7,9]}}
            """));
        Assert.DoesNotContain("hidden_size", config.IntegerFields.Keys);
        Assert.Equal(64, config.JsonIntegerFields["/vision_config/hidden_size"]);
        Assert.Equal(7, config.JsonIntegerFields["/escaped~0~1/values/0"]);
        var headers = new List<SafetensorsContainerParser.TensorReference>();
        foreach (string owner in new[] { "package.encoder.layers.0.attention", "package.decoder.layers.0.attention" })
            foreach (string projection in new[] { "q_proj", "k_proj", "v_proj", "out_proj" })
                headers.Add(new SafetensorsContainerParser.TensorReference
                {
                    Name = $"{owner}.{projection}.weight", Shape = [16, 16], Dtype = "F32",
                    DataStart = 0, DataEnd = 0, HeaderBytes = 0,
                });
        ModelManifest manifest = ModelManifest.Recognize(headers, config, null, "nested config");
        var encoder = manifest.Single("package.encoder.layers.0.attention", TensorRoleKind.AttnQ);
        var decoder = manifest.Single("package.decoder.layers.0.attention", TensorRoleKind.AttnQ);
        Assert.NotNull(encoder);
        Assert.NotNull(decoder);
        Assert.Equal(4, encoder.ContextSymbols["h"]);
        Assert.Equal(8, decoder.ContextSymbols["h"]);
        Assert.Equal(4, encoder.ContextSymbols["d_h"]);
        Assert.Equal(2, decoder.ContextSymbols["d_h"]);
    }

    [Fact]
    public void ConfigArrayContexts_UseTheCapturedSourceStage()
    {
        var config = ModelConfigReader.Read(System.Text.Encoding.UTF8.GetBytes("""
            {"text_config":{"hidden_size":32,"num_attention_heads":4,"num_key_value_heads":2,"head_dim":8},
             "vision_config":{"dim_embed":[8,16,24],"num_heads":[2,4,6]}}
            """));
        var tensors = new List<HeaderTensor>();
        foreach (var stage in new[] { (Index: 0, Width: 8), (Index: 2, Width: 24) })
        {
            string path = $"vision_tower.blocks.{stage.Index}.7.ffn";
            tensors.Add(T(path + ".fc1.weight", stage.Width * 4, stage.Width));
            tensors.Add(T(path + ".fc2.weight", stage.Width, stage.Width * 4));
        }
        var anatomy = ModelOperatorRecognizer.Recognize(tensors, config.IntegerFields, null,
            OperatorTemplateManifest.Governed, config.JsonIntegerFields);
        var mlps = anatomy.Operators.Where(op => op.Family == "mlp").ToArray();
        Assert.Equal(2, mlps.Length);
        Assert.Empty(anatomy.Unrecognized);
        foreach (var op in mlps)
        {
            bool first = op.SourcePath.StartsWith("vision_tower.blocks.0.", StringComparison.Ordinal);
            Assert.Equal(first ? 8 : 24, op.ContextSymbols["d"]);
            Assert.Equal(first ? 2 : 6, op.ContextSymbols["h"]);
            Assert.Equal(op.ContextSymbols["h"], op.ContextSymbols["h_kv"]);
            Assert.Equal(4, op.ContextSymbols["d_h"]);
        }
    }

    [Fact]
    public void ProjectorContext_DoesNotBorrowTheTextOrAudioHeadLayout()
    {
        var config = ModelConfigReader.Read(System.Text.Encoding.UTF8.GetBytes("""
            {"text_config":{"hidden_size":64,"num_attention_heads":8,"num_key_value_heads":2,"head_dim":8},
             "encoder_config":{"hidden_dim":32,"num_heads":4,"dim_head":8},
             "projector_config":{"hidden_size":16,"num_attention_heads":4}}
            """));
        var tensors = new[] { "query", "key", "value" }
            .Select(name => T("projector.qformer.encoder.layer.3.attention.attention." + name + ".weight", 16, 16)).ToList();
        tensors.Add(T("projector.qformer.encoder.layer.3.attention.output.dense.weight", 16, 16));
        var anatomy = ModelOperatorRecognizer.Recognize(tensors, config.IntegerFields, null,
            OperatorTemplateManifest.Governed, config.JsonIntegerFields);
        var attention = Assert.Single(anatomy.Operators, op => op.Family == "attention");
        Assert.Equal(16, attention.ContextSymbols["d"]);
        Assert.Equal(4, attention.ContextSymbols["h"]);
        Assert.Equal(4, attention.ContextSymbols["h_kv"]);
        Assert.Equal(4, attention.ContextSymbols["d_h"]);
        Assert.DoesNotContain(attention.Slots, slot => slot.IsAmbiguous);
    }

    [Theory]
    [InlineData("stage.{ordinal")]
    [InlineData("stage.{ordinal}.{ordinal}")]
    [InlineData("stage.{0bad}")]
    public void InvalidSourceCaptures_AreRejectedAtRecipeLoad(string pattern)
    {
        Assert.Throws<InvalidDataException>(() => new OperatorContext([pattern], "d", ["/width/{ordinal}"], null));
    }

    [Fact]
    public void MissingHeadConfig_IsResolvedFromConnectedProjectionAndNormShapes()
    {
        const string path = "package.layers.7.attention.";
        var tensors = new[] {
            T(path + "q_proj.base_layer.weight", 32, 32),
            T(path + "q_proj.lora_A.weight", 4, 32), T(path + "q_proj.lora_B.weight", 32, 4),
            T(path + "k_proj.weight", 16, 32), T(path + "v_proj.weight", 16, 32),
            T(path + "o_proj.weight", 32, 32), T(path + "q_norm.weight", 4), T(path + "k_norm.weight", 4),
        };
        var anatomy = ModelOperatorRecognizer.Recognize(tensors, new Dictionary<string, long> { ["hidden_size"] = 32 }, null);
        var attention = Assert.Single(anatomy.Operators, op => op.Family == "attention");
        Assert.Equal(8, attention.ContextSymbols["h"]);
        Assert.Equal(4, attention.ContextSymbols["h_kv"]);
        Assert.Equal(4, attention.ContextSymbols["d_h"]);
        Assert.Equal(path + "q_proj.base_layer.weight", attention.Slot("q")!.Tensor);
        Assert.DoesNotContain(attention.Slots, slot => slot.IsAmbiguous);
        Assert.Single(anatomy.Operators, op => op.Family == "low-rank");
        Assert.DoesNotContain(anatomy.Operators, op => op.Family == "mlp");
        var withoutNorms = ModelOperatorRecognizer.Recognize(tensors.Where(t => t.Shape.Length == 2).ToArray(),
            new Dictionary<string, long> { ["hidden_size"] = 32 }, null);
        Assert.DoesNotContain(withoutNorms.Operators, op => op.Family == "attention");
        var unrelatedNorm = tensors.Where(t => t.Shape.Length == 2 && !t.Name.Contains("lora", StringComparison.Ordinal))
            .Select(t => t with { Shape = [32, 32] }).Append(T("package.layers.7.input_layernorm.weight", 32)).ToArray();
        Assert.DoesNotContain(ModelOperatorRecognizer.Recognize(unrelatedNorm,
            new Dictionary<string, long> { ["hidden_size"] = 32 }, null).Operators, op => op.Family == "attention");
    }

    [Theory]
    [InlineData("2*h*d_h", 256, 8L)]
    [InlineData("(h+1)*d_h", 144, 8L)]
    [InlineData("h+4", 12, 8L)]
    [InlineData("4-h", 1, 3L)]
    [InlineData("d_h/h", 2, 8L)]
    [InlineData("h*d_h", 127, null)]
    [InlineData("h/h", 1, null)]
    [InlineData("h*k", 128, null)]
    [InlineData("h*0", 3, null)]
    public void ShapeInference_RequiresOneExactPositiveSolution(string expression, long size, long? expected)
    {
        var result = ShapeExpression.Parse(expression).Infer(size, new Dictionary<string, long> { ["d_h"] = 16 });
        Assert.Equal(expected, result?.Value);
        if (expected is not null) Assert.Equal("h", result!.Value.Key);
    }

    [Fact]
    public void ConditionalAttentionRecipes_KeepContentPositionAndOptionalOperandsDistinct()
    {
        var tensors = new List<HeaderTensor>();
        foreach (int block in new[] { 3, 7 })
        {
            string prefix = $"model.decoder.layers.{block}.";
            foreach (string part in new[] { "qcontent", "qpos", "kcontent", "kpos", "v" })
                tensors.Add(T(prefix + "sa_" + part + "_proj.weight", 16, 16));
            foreach (string part in new[] { "qcontent", "qpos_sine", "kcontent", "kpos", "v" })
                tensors.Add(T(prefix + "ca_" + part + "_proj.weight", 16, 16));
            tensors.Add(T(prefix + "self_attn.out_proj.weight", 16, 16));
            tensors.Add(T(prefix + "encoder_attn.out_proj.weight", 16, 16));
            if (block == 7) tensors.Add(T(prefix + "ca_qpos_proj.weight", 16, 16));
        }
        ModelAnatomy anatomy = ModelOperatorRecognizer.Recognize(tensors,
            new Dictionary<string, long> { ["d_model"] = 16, ["decoder_attention_heads"] = 4 }, null);

        Assert.Empty(anatomy.Unrecognized);
        OperatorInstance[] attention = anatomy.Operators.Where(o => o.Family == "attention").ToArray();
        Assert.Equal(4, attention.Length);
        Assert.All(attention, op => Assert.DoesNotContain(op.Slots, slot => slot.IsAmbiguous));
        Assert.All(attention, op => Assert.All(op.Slots.Where(s => s.IsBound), slot =>
            Assert.StartsWith(op.SourcePath + ".", slot.Tensor!)));
        var first = Assert.Single(attention, o => o.Operator == "conditional-cross-attention" && o.Block == 3);
        var second = Assert.Single(attention, o => o.Operator == "conditional-cross-attention" && o.Block == 7);
        Assert.False(first.Slot("q-position")!.IsBound);
        Assert.True(second.Slot("q-position")!.IsBound);
        Assert.True(first.Slot("q-position-sine")!.IsBound);
    }

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
