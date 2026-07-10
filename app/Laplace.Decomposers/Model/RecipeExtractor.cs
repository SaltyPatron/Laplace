using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;







public sealed class RecipeExtractor
{
    public sealed class RecipeInfo
    {
        public required Hash128 RecipeEntityId { get; init; }
        public required string Name { get; init; }
        public required string Structure { get; init; }
        public required string HiddenSize { get; init; }
        public required int NumLayers { get; init; }
        public required byte[] CanonicalJson { get; init; }
    }

    public static RecipeInfo Parse(string recipeJsonPath)
        => ParseText(File.ReadAllText(recipeJsonPath), recipeJsonPath);



    /// <summary>Canonical BLAKE3 input — sorted JSON keys, matches ingest + export identity.</summary>
    public static byte[] CanonicalBytes(JsonElement root) => CanonicalizeJson(root);

    public static RecipeInfo ParseText(string recipeJson, string origin = "(in-memory)")
    {
        using var doc = JsonDocument.Parse(recipeJson);
        var root = doc.RootElement;

        string kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
        if (kind != "laplace.recipe")
            throw new InvalidOperationException(
                $"not a laplace.recipe (kind='{kind}'): {origin}");

        string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "recipe" : "recipe";
        string structure = root.TryGetProperty("structure", out var s) ? s.GetString() ?? "dense" : "dense";

        string hiddenSize = "auto";
        if (root.TryGetProperty("hidden_size", out var hs))
            hiddenSize = hs.ValueKind == JsonValueKind.Number ? hs.GetInt32().ToString()
                       : hs.GetString() ?? "auto";

        if (!root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"recipe has no layers[]: {origin}");
        int numLayers = layers.GetArrayLength();
        if (root.TryGetProperty("num_layers", out var nl) && nl.ValueKind == JsonValueKind.Number
            && nl.GetInt32() != numLayers)
            throw new InvalidOperationException(
                $"num_layers ({nl.GetInt32()}) != layers.length ({numLayers}) in {origin}");

        byte[] canonical = CanonicalizeJson(root);
        var recipeId = Hash128.Blake3(canonical);

        return new RecipeInfo
        {
            RecipeEntityId = recipeId,
            Name = name,
            Structure = structure,
            HiddenSize = hiddenSize,
            NumLayers = numLayers,
            CanonicalJson = canonical,
        };
    }

    public static SubstrateChange BuildChange(
        RecipeInfo recipe,
        Hash128 sourceId,
        Hash128 modelRecipeTypeId,
        Hash128 hasHiddenSizeTypeId,
        Hash128 hasNumLayersTypeId)
    {
        var b = new SubstrateChangeBuilder(sourceId, "recipe/laplace.recipe",
            entityCapacity: 4, physicalityCapacity: 0, attestationCapacity: 4);
        StageRecipe(b, recipe, sourceId, modelRecipeTypeId, hasHiddenSizeTypeId, hasNumLayersTypeId);
        return b.Build();
    }

    public static void StageRecipe(
        SubstrateChangeBuilder b,
        RecipeInfo recipe,
        Hash128 sourceId,
        Hash128 modelRecipeTypeId,
        Hash128 hasHiddenSizeTypeId,
        Hash128 hasNumLayersTypeId)
    {
        b.AddEntity(recipe.RecipeEntityId, EntityTier.Word, modelRecipeTypeId, firstObservedBy: sourceId);

        void AddScalar(Hash128 typeId, string value)
        {
            var valueId = ModelCoordinates.ScalarId(value);
            b.AddEntity(valueId, EntityTier.Word, EntityTypeRegistry.Scalar, sourceId);
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                recipe.RecipeEntityId, typeId, valueId, sourceId, null, 1.0));
        }

        AddScalar(hasHiddenSizeTypeId, recipe.HiddenSize);
        AddScalar(hasNumLayersTypeId, recipe.NumLayers.ToString());
    }

    public static string CanonicalName(RecipeInfo recipe) =>
        Encoding.UTF8.GetString(recipe.CanonicalJson);

    private static byte[] CanonicalizeJson(JsonElement root)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        WriteCanonical(root, writer);
        writer.Flush();
        return ms.ToArray();
    }



    private static void WriteCanonical(JsonElement el, Utf8JsonWriter w)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                var props = new List<JsonProperty>();
                foreach (var p in el.EnumerateObject()) props.Add(p);
                props.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
                foreach (var p in props) { w.WritePropertyName(p.Name); WriteCanonical(p.Value, w); }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray()) WriteCanonical(item, w);
                w.WriteEndArray();
                break;
            default:
                el.WriteTo(w);
                break;
        }
    }
}
