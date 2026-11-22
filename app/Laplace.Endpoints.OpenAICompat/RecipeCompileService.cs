using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record RecipeCompileResult(
    string RecipeIdHex,
    string Name,
    string Structure,
    string HiddenSize,
    int NumLayers,
    string CompileMode);

internal interface IRecipeCompileService
{
    RecipeCompileResult Compile(string recipeJson);
}

internal sealed class RecipeCompileService : IRecipeCompileService
{
    public RecipeCompileResult Compile(string recipeJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeJson);
        using var doc = JsonDocument.Parse(recipeJson.Trim());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Recipe must be a JSON object.");

        string kind = Str(root, "kind", "");
        if (!string.Equals(kind, "laplace.recipe", StringComparison.Ordinal))
            throw new InvalidOperationException($"not a laplace.recipe (kind='{kind}')");

        string name = Str(root, "name", "recipe");
        string structure = Str(root, "structure", "dense");
        if (structure is not ("dense" or "moe"))
            throw new InvalidOperationException($"unknown recipe structure '{structure}'");

        string hidden = root.TryGetProperty("hidden_size", out var hs)
            ? (hs.ValueKind == JsonValueKind.Number ? hs.GetInt32().ToString() : hs.GetString() ?? "auto")
            : "auto";
        if (hidden != "auto" && (!int.TryParse(hidden, out int hiddenVal) || hiddenVal <= 0))
            throw new InvalidOperationException("hidden_size must be a positive integer or 'auto'.");

        if (!root.TryGetProperty("layers", out var layersEl) || layersEl.ValueKind != JsonValueKind.Array || layersEl.GetArrayLength() == 0)
            throw new InvalidOperationException("recipe requires a non-empty layers[] array");

        int layerIdx = 0;
        foreach (var layer in layersEl.EnumerateArray())
        {
            int kv = layer.TryGetProperty("kv_heads", out var kvh) && kvh.ValueKind == JsonValueKind.Number
                ? kvh.GetInt32() : 1;
            if (kv <= 0)
                throw new InvalidOperationException($"layer {layerIdx} kv_heads must be positive");
            if (!layer.TryGetProperty("heads", out var headsEl) || headsEl.ValueKind != JsonValueKind.Array || headsEl.GetArrayLength() == 0)
                throw new InvalidOperationException($"layer {layerIdx} has no heads[]");
            foreach (var h in headsEl.EnumerateArray())
                ValidateOperator(h);
            if (headsEl.GetArrayLength() % kv != 0)
                throw new InvalidOperationException($"layer {layerIdx} heads count must be divisible by kv_heads");
            if (layer.TryGetProperty("ffn", out var ffnEl))
                ValidateOperator(ffnEl);
            if (layer.TryGetProperty("experts", out var expEl) && expEl.ValueKind == JsonValueKind.Array)
                foreach (var ex in expEl.EnumerateArray())
                    ValidateOperator(ex);
            layerIdx++;
        }

        if (root.TryGetProperty("num_layers", out var nl) && nl.ValueKind == JsonValueKind.Number
            && nl.GetInt32() != layerIdx)
            throw new InvalidOperationException($"num_layers ({nl.GetInt32()}) != layers.length ({layerIdx})");

        if (root.TryGetProperty("embed", out var em)) ValidateOperator(em);
        if (root.TryGetProperty("lm_head", out var lm)) ValidateOperator(lm);

        string compile = Str(root, "compile", "");
        if (compile.Length == 0) compile = "full";

        var canonicalBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(root));
        var id = Hash128.Blake3(canonicalBytes);

        return new RecipeCompileResult(
            RecipeIdHex: Convert.ToHexStringLower(id.ToBytes()),
            Name: name,
            Structure: structure,
            HiddenSize: hidden,
            NumLayers: layerIdx,
            CompileMode: compile);
    }

    private static void ValidateOperator(JsonElement op)
    {
        string kind = op.ValueKind == JsonValueKind.Object && op.TryGetProperty("op", out var p)
            ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(kind))
            throw new InvalidOperationException($"operator missing 'op': {op}");

        if (op.TryGetProperty("max_outside_probability", out var boundEl)
            && boundEl.ValueKind == JsonValueKind.Number)
        {
            double bound = boundEl.GetDouble();
            if (!double.IsFinite(bound) || bound < 0 || bound >= 1)
                throw new InvalidOperationException("invalid output candidate probability bound");
        }
    }

    private static string Str(JsonElement e, string key, string fallback) =>
        e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? fallback : fallback;
}
