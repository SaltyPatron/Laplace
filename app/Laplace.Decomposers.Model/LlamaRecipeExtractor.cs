using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
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
        public required double  RmsNormEps     { get; init; }
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
        double theta   = GetDoubleOr(root, "rope_theta", 10000.0);
        // Phi (and others) carry rms_norm_eps:null and use layer_norm_eps instead; GetDouble()
        // on a JSON null throws. Take rms_norm_eps if it's a real number, else layer_norm_eps,
        // else 1e-5 — generic across norm conventions, never crashes on a null.
        double rmsEps  = GetDoubleOr(root, "rms_norm_eps", GetDoubleOr(root, "layer_norm_eps", 1e-5));
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
            RmsNormEps       = rmsEps,
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
        Hash128 hasHiddenSizeTypeId,
        Hash128 hasNumLayersTypeId,
        Hash128 hasNumHeadsTypeId,
        Hash128 hasNumKvHeadsTypeId,
        Hash128 hasIntermSizeTypeId,
        Hash128 hasVocabSizeTypeId,
        Hash128 isAKindId,
        Hash128 architectureEntityId)
    {
        var b = new SubstrateChangeBuilder(sourceId, "recipe/config.json",
            entityCapacity: 2, physicalityCapacity: 0, attestationCapacity: 8);

        b.AddEntity(recipe.RecipeEntityId, (byte)MetaTier.Meta, modelRecipeTypeId, firstObservedBy: sourceId);

        // Recipe attestations are categorical config facts (HAS_*; the value lives in
        // the object entity, not the score). A confirm observation, full trust.
        void AddAttestation(Hash128 typeId, Hash128? objectId)
            => b.AddAttestation(AttestationFactory.CreateCategorical(
                recipe.RecipeEntityId, typeId, objectId, sourceId, contextId: null,
                confirm: true, witnessWeight: 1.0));

        /* Scalar-valued recipe parameters stored as content entities */
        void AddScalar(Hash128 typeId, string value)
        {
            var valueBytes = Encoding.UTF8.GetBytes(value);
            var valueId    = Hash128.Blake3(valueBytes);
            b.AddEntity(valueId, (byte)MetaTier.Meta, Hash128.OfCanonical("substrate/type/Scalar/v1"), sourceId);
            AddAttestation(typeId, valueId);
        }

        AddScalar(hasHiddenSizeTypeId,  recipe.HiddenSize.ToString());
        AddScalar(hasNumLayersTypeId,   recipe.NumLayers.ToString());
        AddScalar(hasNumHeadsTypeId,    recipe.NumHeads.ToString());
        AddScalar(hasNumKvHeadsTypeId,  recipe.NumKvHeads.ToString());
        AddScalar(hasIntermSizeTypeId,  recipe.IntermediateSize.ToString());
        AddScalar(hasVocabSizeTypeId,   recipe.VocabSize.ToString());

        /* IS_A attestation → architecture entity */
        b.AddEntity(architectureEntityId, (byte)MetaTier.Meta, Hash128.OfCanonical("substrate/type/Architecture/v1"), sourceId);
        AddAttestation(isAKindId, architectureEntityId);

        return b.Build();
    }

    private static int GetInt(JsonElement root, string key, int def)
    {
        if (root.TryGetProperty(key, out var prop) && prop.ValueTypeId == JsonValueTypeId.Number)
            return prop.GetInt32();
        return def;   // absent OR null (e.g. num_key_value_heads:null) → default
    }

    // Double-or-default that tolerates absent AND null (JSON null is ValueTypeId.Null, not
    // Number → GetDouble() would throw). Generic across configs that null out a key they
    // don't use (Phi: rms_norm_eps:null).
    private static double GetDoubleOr(JsonElement root, string key, double def)
    {
        if (root.TryGetProperty(key, out var prop) && prop.ValueTypeId == JsonValueTypeId.Number)
            return prop.GetDouble();
        return def;
    }

    // No default — a missing required structural dim throws rather than silently
    // substituting a (Llama-shaped) guess. Per the exact/faithful mandate: never
    // invent model geometry. The embed-tensor size check in WeightTensorETL is a
    // backstop; this fails earlier with a clearer message.
    private static int GetIntRequired(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var prop) && prop.ValueTypeId == JsonValueTypeId.Number)
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
