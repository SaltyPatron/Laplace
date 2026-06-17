using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;

// The read-side, architecture-agnostic descriptor parsed from a build-a-bear recipe
// (docs/invention/recipe-schema.md). Export fetches the recipe_json from laplace.model_recipes()
// and parses it into this typed form, which drives the generic tensor manifest + per-head operator
// materialize. This replaces the hardcoded ArchitectureProfile classes on the synthesis path.

// One operator: a relation head (Type set), a metric head (Metric set), or a structural op
// (trajectory / unary / coord / spectral) identified by Op alone.
public sealed record OperatorSpec(string Op, string? Type, string? Metric)
{
    public static OperatorSpec FromJson(JsonElement e)
    {
        string op = e.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
        string? type   = e.TryGetProperty("type", out var t) ? t.GetString() : null;
        string? metric = e.TryGetProperty("metric", out var m) ? m.GetString() : null;
        if (string.IsNullOrEmpty(op))
            throw new InvalidOperationException($"operator missing 'op': {e}");
        return new OperatorSpec(op, type, metric);
    }

    // Stable key for grouping / per-operator factoring and provenance.
    public string Key => Op switch
    {
        "relation" => $"relation:{Type}",
        "metric"   => $"metric:{Metric}",
        _          => Op,
    };
}

public sealed record LayerSpec(int KvHeads, IReadOnlyList<OperatorSpec> Heads, OperatorSpec Ffn);

public sealed record VocabSpec(
    string Source, IReadOnlyList<string> Seeds, int Hops, int Fanout, int Size, string? Tokenizer);

public sealed record RecipeDescriptor(
    Hash128 RecipeId,
    string Name,
    string Structure,
    string HiddenSize,            // "auto" or an int as string
    int IntermediateSize,
    int NumLayers,
    bool Rope,
    bool TieEmbeddings,
    string Norm,
    OperatorSpec Embed,
    OperatorSpec LmHead,
    IReadOnlyList<LayerSpec> Layers,
    VocabSpec Vocab,
    byte[] CanonicalJson)
{
    public bool HiddenSizeAuto => HiddenSize == "auto";
    public int  HiddenSizeOr(int fallback) => int.TryParse(HiddenSize, out var v) ? v : fallback;

    public static RecipeDescriptor Parse(string recipeJson)
    {
        using var doc = JsonDocument.Parse(recipeJson);
        var root = doc.RootElement;

        string kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
        if (kind != "laplace.recipe")
            throw new InvalidOperationException($"not a laplace.recipe (kind='{kind}')");

        string name      = Str(root, "name", "recipe");
        string structure = Str(root, "structure", "dense");
        string hidden    = root.TryGetProperty("hidden_size", out var hs)
            ? (hs.ValueKind == JsonValueKind.Number ? hs.GetInt32().ToString() : hs.GetString() ?? "auto")
            : "auto";
        int hiddenInt = int.TryParse(hidden, out var hv) ? hv : 0;
        int intermediate = Int(root, "intermediate_size",
            hiddenInt > 0 ? RoundTo64(hiddenInt * 8 / 3) : 1024);   // SwiGLU ~2.67x, rounded to 64
        bool rope = root.TryGetProperty("rope", out var rp) && rp.ValueKind == JsonValueKind.True;
        bool tie  = root.TryGetProperty("tie_embeddings", out var te) && te.ValueKind == JsonValueKind.True;
        string norm = Str(root, "norm", "rmsnorm");

        var embed  = root.TryGetProperty("embed", out var em) ? OperatorSpec.FromJson(em)
                                                              : new OperatorSpec("coord", null, null);
        var lmHead = root.TryGetProperty("lm_head", out var lm) ? OperatorSpec.FromJson(lm)
                                                                : new OperatorSpec("trajectory", null, null);

        var layers = new List<LayerSpec>();
        if (!root.TryGetProperty("layers", out var layersEl) || layersEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("recipe has no layers[]");
        foreach (var layer in layersEl.EnumerateArray())
        {
            int kv = layer.TryGetProperty("kv_heads", out var kvh) && kvh.ValueKind == JsonValueKind.Number
                ? kvh.GetInt32() : 1;
            var heads = new List<OperatorSpec>();
            if (layer.TryGetProperty("heads", out var headsEl) && headsEl.ValueKind == JsonValueKind.Array)
                foreach (var h in headsEl.EnumerateArray()) heads.Add(OperatorSpec.FromJson(h));
            if (heads.Count == 0)
                throw new InvalidOperationException($"layer {layers.Count} has no heads[]");
            var ffn = layer.TryGetProperty("ffn", out var ffnEl) ? OperatorSpec.FromJson(ffnEl)
                                                                 : new OperatorSpec("unary", null, null);
            layers.Add(new LayerSpec(kv, heads, ffn));
        }

        var vocab = ParseVocab(root);
        byte[] canonical = Encoding.UTF8.GetBytes(recipeJson);

        return new RecipeDescriptor(
            Hash128.Blake3(canonical), name, structure, hidden, intermediate, layers.Count,
            rope, tie, norm, embed, lmHead, layers, vocab, canonical);
    }

    private static int RoundTo64(int x) => Math.Max(64, ((x + 63) / 64) * 64);

    private static VocabSpec ParseVocab(JsonElement root)
    {
        if (!root.TryGetProperty("vocab", out var v) || v.ValueKind != JsonValueKind.Object)
            return new VocabSpec("crawl", Array.Empty<string>(), 2, 30, 1500, null);
        string source = Str(v, "source", "crawl");
        var seeds = new List<string>();
        if (v.TryGetProperty("seeds", out var se) && se.ValueKind == JsonValueKind.Array)
            foreach (var s in se.EnumerateArray())
                if (s.GetString() is { } str) seeds.Add(str);
        int hops   = Int(v, "hops", 2);
        int fanout = Int(v, "fanout", 30);
        int size   = Int(v, "size", 1500);
        string? tok = v.TryGetProperty("tokenizer", out var tk) ? tk.GetString() : null;
        return new VocabSpec(source, seeds, hops, fanout, size, tok);
    }

    private static string Str(JsonElement e, string key, string def)
        => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? def : def;
    private static int Int(JsonElement e, string key, int def)
        => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : def;
}
