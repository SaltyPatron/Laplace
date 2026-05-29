using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Extracts a Model_Recipe entity + per-field attestations from config.json.
/// The recipe entity ID = Hash128.Blake3(canonical config.json bytes).
/// Per-field attestations record the model architecture parameters on the recipe
/// entity (HAS_HIDDEN_SIZE, HAS_NUM_LAYERS, etc.) for substrate queries.
/// </summary>
public sealed class LlamaRecipeExtractor
{
    public sealed class RecipeInfo
    {
        public required Hash128 RecipeEntityId { get; init; }
        public required string  Architecture   { get; init; }
        public required int     HiddenSize     { get; init; }
        public required int     NumLayers      { get; init; }
        public required int     NumHeads       { get; init; }
        public required int     NumKvHeads     { get; init; }
        public required int     IntermediateSize { get; init; }
        public required int     VocabSize      { get; init; }
        public required string  TorchDtype     { get; init; }
        public required string  HiddenAct      { get; init; }
        public required double  RopeTheta      { get; init; }
        public required string  ModelType      { get; init; }
        public required byte[]  CanonicalJson  { get; init; }
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

        // Required structural dims — refuse to guess. A missing key means the
        // config isn't the architecture we assume; a silent Llama-shaped default
        // (2048/22/32/5632/32000) would corrupt the ingest. Faithfulness mandate.
        int hiddenSize = GetIntRequired(root, "hidden_size");
        int numLayers  = GetIntRequired(root, "num_hidden_layers");
        int numHeads   = GetIntRequired(root, "num_attention_heads");
        // num_key_value_heads legitimately absent on pure-MHA models → = numHeads.
        int numKvHeads = GetInt(root, "num_key_value_heads", numHeads);
        int intermSize = GetIntRequired(root, "intermediate_size");
        int vocabSize  = GetIntRequired(root, "vocab_size");
        string dtype   = root.TryGetProperty("torch_dtype",  out var dtProp) ? dtProp.GetString() ?? "bfloat16" : "bfloat16";
        string act     = root.TryGetProperty("hidden_act",   out var actProp) ? actProp.GetString() ?? "silu" : "silu";
        double theta   = root.TryGetProperty("rope_theta",   out var thetaProp) ? thetaProp.GetDouble() : 10000.0;
        string mtype   = root.TryGetProperty("model_type",   out var mtProp) ? mtProp.GetString() ?? "llama" : "llama";

        /* Canonical bytes = deterministic JSON re-serialisation (sorted keys). */
        byte[] canonical = CanonicalizeJson(root);
        var recipeId = Hash128.Blake3(canonical);

        return new RecipeInfo
        {
            RecipeEntityId   = recipeId,
            Architecture     = arch,
            HiddenSize       = hiddenSize,
            NumLayers        = numLayers,
            NumHeads         = numHeads,
            NumKvHeads       = numKvHeads,
            IntermediateSize = intermSize,
            VocabSize        = vocabSize,
            TorchDtype       = dtype,
            HiddenAct        = act,
            RopeTheta        = theta,
            ModelType        = mtype,
            CanonicalJson    = canonical,
        };
    }

    /// <summary>
    /// Yield the recipe entity + recipe-parameter attestations as a single SubstrateChange.
    /// </summary>
    public static SubstrateChange BuildChange(
        RecipeInfo recipe,
        Hash128 sourceId,
        Hash128 modelRecipeTypeId,
        Hash128 hasHiddenSizeKindId,
        Hash128 hasNumLayersKindId,
        Hash128 hasNumHeadsKindId,
        Hash128 hasNumKvHeadsKindId,
        Hash128 hasIntermSizeKindId,
        Hash128 hasVocabSizeKindId,
        Hash128 isAKindId,
        Hash128 architectureEntityId)
    {
        var b = new SubstrateChangeBuilder(sourceId, "recipe/config.json",
            entityCapacity: 2, physicalityCapacity: 0, attestationCapacity: 8);

        b.AddEntity(recipe.RecipeEntityId, tier: 0, modelRecipeTypeId, firstObservedBy: sourceId);

        void AddAttestation(Hash128 kindId, Hash128? objectId, long rating)
        {
            var attId = ComputeAttestationId(recipe.RecipeEntityId, kindId, objectId, sourceId);
            b.AddAttestation(new AttestationRow(
                Id:               attId,
                SubjectId:        recipe.RecipeEntityId,
                KindId:           kindId,
                ObjectId:         objectId,
                SourceId:         sourceId,
                ContextId:        null,
                RatingFp1e9:      rating,
                RdFp1e9:          350_000_000_000L,
                VolatilityFp1e9:  60_000_000L,
                LastObservedAtUnixUs: 0,
                ObservationCount: 1));
        }

        /* Scalar-valued recipe parameters stored as content entities */
        void AddScalar(Hash128 kindId, string value)
        {
            var valueBytes = Encoding.UTF8.GetBytes(value);
            var valueId    = Hash128.Blake3(valueBytes);
            b.AddEntity(valueId, tier: 0, Hash128.OfCanonical("substrate/type/Scalar/v1"), sourceId);
            AddAttestation(kindId, valueId, 1_500_000_000_000L);
        }

        AddScalar(hasHiddenSizeKindId,  recipe.HiddenSize.ToString());
        AddScalar(hasNumLayersKindId,   recipe.NumLayers.ToString());
        AddScalar(hasNumHeadsKindId,    recipe.NumHeads.ToString());
        AddScalar(hasNumKvHeadsKindId,  recipe.NumKvHeads.ToString());
        AddScalar(hasIntermSizeKindId,  recipe.IntermediateSize.ToString());
        AddScalar(hasVocabSizeKindId,   recipe.VocabSize.ToString());

        /* IS_A attestation → architecture entity */
        b.AddEntity(architectureEntityId, tier: 0, Hash128.OfCanonical("substrate/type/Architecture/v1"), sourceId);
        AddAttestation(isAKindId, architectureEntityId, 1_500_000_000_000L);

        return b.Build();
    }

    private static int GetInt(JsonElement root, string key, int def)
    {
        if (root.TryGetProperty(key, out var prop)) return prop.GetInt32();
        return def;
    }

    // No default — a missing required structural dim throws rather than silently
    // substituting a (Llama-shaped) guess. Per the exact/faithful mandate: never
    // invent model geometry. The embed-tensor size check in WeightTensorETL is a
    // backstop; this fails earlier with a clearer message.
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
        /* Sort keys alphabetically for determinism. */
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
        Hash128 subject, Hash128 kind, Hash128? obj, Hash128 source)
    {
        Span<byte> buf = stackalloc byte[80];
        subject.WriteBytes(buf.Slice(0, 16));
        kind.WriteBytes(buf.Slice(16, 16));
        (obj ?? default).WriteBytes(buf.Slice(32, 16));
        source.WriteBytes(buf.Slice(48, 16));
        buf.Slice(64, 16).Clear(); /* context = zero */
        return Hash128.Blake3(buf);
    }
}
