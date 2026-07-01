using System.Text.Json;
using System.Text.Json.Nodes;

namespace Laplace.Decomposers.Model;









public static class RecipeSynthesizer
{
    public static RecipeExtractor.RecipeInfo Synthesize(
        ModelManifest manifest,
        IReadOnlyDictionary<(int Layer, int Head), string>? headEncodings = null)
        => RecipeExtractor.ParseText(BuildRecipeJson(manifest, headEncodings), "(synthesized)");

    public static string BuildRecipeJson(
        ModelManifest manifest,
        IReadOnlyDictionary<(int Layer, int Head), string>? headEncodings = null)
    {
        var cfg = manifest.Config;
        bool rope = !cfg.ModelType.Equals("bert", StringComparison.OrdinalIgnoreCase);
        bool layerNm = cfg.ModelType is "bert" or "phi" or "gpt2";
        int heads = Math.Max(1, cfg.NumHeads);
        int kvHeads = Math.Max(1, cfg.NumKvHeads);
        int layers = Math.Max(manifest.LayerCount, cfg.NumLayers);

        var layersArr = new JsonArray();
        for (int L = 0; L < layers; L++)
        {
            var headsArr = new JsonArray();
            for (int h = 0; h < heads; h++)
            {
                string? enc = headEncodings is not null
                    && headEncodings.TryGetValue((L, h), out var e) ? e : null;


                headsArr.Add(enc is not null
                    ? new JsonObject { ["op"] = "relation", ["type"] = enc }
                    : new JsonObject { ["op"] = "relation", ["type"] = "ATTENDS" });
            }
            var ffn = cfg.IsMoe
                ? new JsonObject { ["op"] = "relation", ["type"] = "COMPLETES_TO", ["experts"] = cfg.NumExperts }
                : new JsonObject { ["op"] = "relation", ["type"] = "COMPLETES_TO" };
            layersArr.Add(new JsonObject
            {
                ["kv_heads"] = kvHeads,
                ["heads"] = headsArr,
                ["ffn"] = ffn,
            });
        }

        var doc = new JsonObject
        {
            ["kind"] = "laplace.recipe",
            ["name"] = manifest.ModelName,
            ["structure"] = cfg.IsMoe ? "moe" : "dense",
            ["model_type"] = cfg.ModelType,
            ["hidden_size"] = cfg.HiddenSize > 0 ? cfg.HiddenSize : (JsonNode?)"auto",
            ["intermediate_size"] = cfg.IntermediateSize,
            ["num_layers"] = layers,
            ["rope"] = rope,
            ["tie_embeddings"] = cfg.TieWordEmbeddings,
            ["norm"] = layerNm ? "layernorm" : "rmsnorm",
            ["embed"] = new JsonObject { ["op"] = "coord" },
            ["lm_head"] = new JsonObject { ["op"] = "trajectory" },
            ["layers"] = layersArr,
            ["vocab"] = new JsonObject
            {
                ["source"] = "tokenizer",
                ["size"] = cfg.VocabSize,
                ["tokenizer"] = manifest.ModelName,
            },
            ["compile"] = "continuation",
        };

        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
