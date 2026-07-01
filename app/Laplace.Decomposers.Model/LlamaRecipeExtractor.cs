using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

public sealed class LlamaRecipeExtractor
{
    public sealed class RecipeInfo
    {
        public required Hash128 RecipeEntityId { get; init; }
        public required string Architecture { get; init; }
        public required int HiddenSize { get; init; }
        public required int NumLayers { get; init; }
        public required int NumHeads { get; init; }
        public required int NumKvHeads { get; init; }
        public required int IntermediateSize { get; init; }
        public required int VocabSize { get; init; }
        public required string TorchDtype { get; init; }
        public required string HiddenAct { get; init; }
        public required double RopeTheta { get; init; }
        public required double RmsNormEps { get; init; }
        public required string ModelType { get; init; }
        public required byte[] CanonicalJson { get; init; }
    }

    public static RecipeInfo Parse(string configJsonPath)
    {
        byte[] raw = File.ReadAllBytes(configJsonPath);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        string arch = "LlamaForCausalLM";
        if (root.TryGetProperty("architectures", out var archArr))
        {
            int len = archArr.GetArrayLength();
            if (len > 0) arch = archArr[0].GetString() ?? arch;
        }

        int hiddenSize = GetIntRequired(root, "hidden_size");
        int numLayers = GetIntRequired(root, "num_hidden_layers");
        int numHeads = GetIntRequired(root, "num_attention_heads");
        int numKvHeads = GetInt(root, "num_key_value_heads", numHeads);
        int intermSize = GetIntRequired(root, "intermediate_size");
        int vocabSize = GetIntRequired(root, "vocab_size");
        string dtype = root.TryGetProperty("torch_dtype", out var dtProp) ? dtProp.GetString() ?? "bfloat16" : "bfloat16";
        string act = root.TryGetProperty("hidden_act", out var actProp) ? actProp.GetString() ?? "silu" : "silu";
        double theta = GetDoubleOr(root, "rope_theta", 10000.0);
        double rmsEps = GetDoubleOr(root, "rms_norm_eps", GetDoubleOr(root, "layer_norm_eps", 1e-5));
        string mtype = root.TryGetProperty("model_type", out var mtProp) ? mtProp.GetString() ?? "llama" : "llama";

        byte[] canonical = CanonicalizeJson(root);
        var recipeId = Hash128.Blake3(canonical);

        return new RecipeInfo
        {
            RecipeEntityId = recipeId,
            Architecture = arch,
            HiddenSize = hiddenSize,
            NumLayers = numLayers,
            NumHeads = numHeads,
            NumKvHeads = numKvHeads,
            IntermediateSize = intermSize,
            VocabSize = vocabSize,
            TorchDtype = dtype,
            HiddenAct = act,
            RopeTheta = theta,
            RmsNormEps = rmsEps,
            ModelType = mtype,
            CanonicalJson = canonical,
        };
    }

    public static SubstrateChange BuildChange(
        RecipeInfo recipe,
        Hash128 sourceId,
        Hash128 modelRecipeTypeId,
        Hash128 hasHiddenSizeTypeId,
        Hash128 hasNumLayersTypeId,
        Hash128 hasNumHeadsTypeId,
        Hash128 hasNumKvHeadsTypeId,
        Hash128 hasIntermSizeTypeId,
        Hash128 hasVocabSizeTypeId,
        Hash128 isATypeId,
        Hash128 architectureEntityId)
    {
        var b = new SubstrateChangeBuilder(sourceId, "recipe/config.json",
            entityCapacity: 2, physicalityCapacity: 0, attestationCapacity: 8);

        b.AddEntity(recipe.RecipeEntityId, EntityTier.Word, modelRecipeTypeId, firstObservedBy: sourceId);

        void AddAttestation(Hash128 typeId, Hash128? objectId)
            => b.AddAttestation(NativeAttestation.CategoricalResolved(
                recipe.RecipeEntityId, typeId, objectId, sourceId, null, 1.0));

        void AddScalar(Hash128 typeId, string value)
        {
            var valueBytes = Encoding.UTF8.GetBytes(value);
            var valueId = Hash128.Blake3(valueBytes);
            b.AddEntity(valueId, EntityTier.Word, EntityTypeRegistry.Scalar, sourceId);
            AddAttestation(typeId, valueId);
        }

        AddScalar(hasHiddenSizeTypeId, recipe.HiddenSize.ToString());
        AddScalar(hasNumLayersTypeId, recipe.NumLayers.ToString());
        AddScalar(hasNumHeadsTypeId, recipe.NumHeads.ToString());
        AddScalar(hasNumKvHeadsTypeId, recipe.NumKvHeads.ToString());
        AddScalar(hasIntermSizeTypeId, recipe.IntermediateSize.ToString());
        AddScalar(hasVocabSizeTypeId, recipe.VocabSize.ToString());

        b.AddEntity(architectureEntityId, EntityTier.Word, EntityTypeRegistry.Architecture, sourceId);
        AddAttestation(isATypeId, architectureEntityId);

        return b.Build();
    }

    private static int GetInt(JsonElement root, string key, int def)
    {
        if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        return def;
    }

    private static double GetDoubleOr(JsonElement root, string key, double def)
    {
        if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetDouble();
        return def;
    }

    private static int GetIntRequired(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        throw new InvalidOperationException(
            $"config.json missing required field '{key}' — refusing to assume a default. " +
            "The model architecture must declare its dimensions explicitly.");
    }

    private static byte[] CanonicalizeJson(JsonElement root)
    {
        var opts = new JsonSerializerOptions { WriteIndented = false };
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartObject();
        var props = new List<JsonProperty>();
        foreach (var p in root.EnumerateObject()) props.Add(p);
        props.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        foreach (var p in props)
        {
            writer.WritePropertyName(p.Name);
            p.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }

    internal static Hash128 ComputeAttestationId(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 source)
    {
        Span<byte> buf = stackalloc byte[80];
        subject.WriteBytes(buf.Slice(0, 16));
        typeId.WriteBytes(buf.Slice(16, 16));
        (obj ?? default).WriteBytes(buf.Slice(32, 16));
        source.WriteBytes(buf.Slice(48, 16));
        buf.Slice(64, 16).Clear();
        return Hash128.Blake3(buf);
    }
}
