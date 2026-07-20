using Laplace.Decomposers.Model;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// LlamaRecipeExtractor.Parse now reads config.json through the NATIVE recipe parser
/// (#263/#264) instead of a second managed JSON pass. These tests pin two things:
/// the field values it extracts, and — critically — that RecipeEntityId is unchanged,
/// because that id is content-addressed and moving it would re-mint every recipe entity.
/// </summary>
public sealed class LlamaRecipeExtractorTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "laplace-recipe-tests-" + Guid.NewGuid().ToString("N"));

    public LlamaRecipeExtractorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteConfig(string json)
    {
        string path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string TinyLlamaish = """
        {
          "architectures": ["LlamaForCausalLM"],
          "hidden_act": "silu",
          "hidden_size": 2048,
          "intermediate_size": 5632,
          "model_type": "llama",
          "num_attention_heads": 32,
          "num_hidden_layers": 22,
          "num_key_value_heads": 4,
          "rms_norm_eps": 1e-05,
          "rope_theta": 10000.0,
          "torch_dtype": "bfloat16",
          "vocab_size": 32000
        }
        """;

    [Fact]
    public void ParsesEveryDeclaredField()
    {
        var r = LlamaRecipeExtractor.Parse(WriteConfig(TinyLlamaish));

        Assert.Equal("LlamaForCausalLM", r.Architecture);
        Assert.Equal(2048, r.HiddenSize);
        Assert.Equal(22, r.NumLayers);
        Assert.Equal(32, r.NumHeads);
        Assert.Equal(4, r.NumKvHeads);
        Assert.Equal(5632, r.IntermediateSize);
        Assert.Equal(32000, r.VocabSize);
        Assert.Equal("bfloat16", r.TorchDtype);
        Assert.Equal("silu", r.HiddenAct);
        Assert.Equal("llama", r.ModelType);
        Assert.Equal(10000.0, r.RopeTheta);
        Assert.Equal(1e-05, r.RmsNormEps);
    }

    // Identity guard: the recipe id is BLAKE3 over the canonical JSON. Canonicalization
    // deliberately stayed managed when field extraction moved native (#552 tracks moving
    // it); this test fails if either side drifts and starts re-minting recipe entities.
    [Fact]
    public void RecipeEntityIdIsContentAddressedAndStable()
    {
        string a = WriteConfig(TinyLlamaish);
        var first = LlamaRecipeExtractor.Parse(a);
        var second = LlamaRecipeExtractor.Parse(a);
        Assert.Equal(first.RecipeEntityId, second.RecipeEntityId);
        Assert.Equal(first.RecipeEntityId, Hash128.Blake3(first.CanonicalJson));

        // Key ORDER must not change the id — canonicalization sorts.
        string reordered = """
            {
              "vocab_size": 32000,
              "torch_dtype": "bfloat16",
              "rope_theta": 10000.0,
              "rms_norm_eps": 1e-05,
              "num_key_value_heads": 4,
              "num_hidden_layers": 22,
              "num_attention_heads": 32,
              "model_type": "llama",
              "intermediate_size": 5632,
              "hidden_size": 2048,
              "hidden_act": "silu",
              "architectures": ["LlamaForCausalLM"]
            }
            """;
        var shuffled = LlamaRecipeExtractor.Parse(WriteConfig(reordered));
        Assert.Equal(first.RecipeEntityId, shuffled.RecipeEntityId);

        // A different VALUE must change it.
        var changed = LlamaRecipeExtractor.Parse(
            WriteConfig(TinyLlamaish.Replace("\"hidden_size\": 2048", "\"hidden_size\": 4096")));
        Assert.NotEqual(first.RecipeEntityId, changed.RecipeEntityId);
    }

    [Fact]
    public void MissingRequiredDimensionThrowsRatherThanDefaulting()
    {
        string json = TinyLlamaish.Replace("\"hidden_size\": 2048,", "");
        var ex = Assert.Throws<InvalidOperationException>(
            () => LlamaRecipeExtractor.Parse(WriteConfig(json)));
        Assert.Contains("hidden_size", ex.Message);
    }

    // A present-but-malformed value must fail loudly. Silently falling back would
    // record a wrong architecture as if the source had asserted it.
    [Fact]
    public void MalformedOptionalValueIsRefusedNotDefaulted()
    {
        string json = TinyLlamaish.Replace("\"num_key_value_heads\": 4", "\"num_key_value_heads\": \"four\"");
        Assert.Throws<InvalidDataException>(() => LlamaRecipeExtractor.Parse(WriteConfig(json)));
    }

    [Fact]
    public void AbsentOptionalsFallBackToTheDocumentedDefaults()
    {
        string json = TinyLlamaish
            .Replace("\"num_key_value_heads\": 4,", "")
            .Replace("\"rope_theta\": 10000.0,", "")
            .Replace("\"rms_norm_eps\": 1e-05,", "");
        var r = LlamaRecipeExtractor.Parse(WriteConfig(json));
        Assert.Equal(r.NumHeads, r.NumKvHeads);   // defaults to num_attention_heads
        Assert.Equal(10000.0, r.RopeTheta);
        Assert.Equal(1e-5, r.RmsNormEps);
    }

    [Fact]
    public void LayerNormEpsIsUsedWhenRmsNormEpsIsAbsent()
    {
        string json = TinyLlamaish.Replace("\"rms_norm_eps\": 1e-05,", "\"layer_norm_eps\": 1e-12,");
        var r = LlamaRecipeExtractor.Parse(WriteConfig(json));
        Assert.Equal(1e-12, r.RmsNormEps);
    }

    [Fact]
    public void NonObjectJsonIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => LlamaRecipeExtractor.Parse(WriteConfig("[1,2,3]")));
    }
}
