using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

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

    /// <summary>
    /// Reads config.json through the NATIVE recipe parser (engine/synthesis/src/recipe.cpp)
    /// — one parser for this file, not one per language. Typed fields come from
    /// recipe_get_int / recipe_get_double, which refuse a malformed value instead of
    /// substituting a default.
    ///
    /// CanonicalizeJson stays managed on purpose: its bytes are hashed into
    /// RecipeEntityId, so any change to it moves a content id. Relocating it to the
    /// engine is GH #552, gated on proving byte-identical output over real configs.
    /// </summary>
    public static RecipeInfo Parse(string configJsonPath)
    {
        byte[] raw = File.ReadAllBytes(configJsonPath);

        IntPtr r;
        unsafe
        {
            fixed (byte* p = raw) r = SynInterop.RecipeParse(p, (nuint)raw.Length);
        }
        if (r == IntPtr.Zero)
            throw new InvalidDataException($"recipe_parse rejected '{configJsonPath}' — not a JSON object.");

        try
        {
            // architectures is an array; the native parser surfaces its first element.
            string arch = GetString(r, "architectures") ?? "LlamaForCausalLM";

            int hiddenSize = RequireInt(r, "hidden_size", configJsonPath);
            int numLayers  = RequireInt(r, "num_hidden_layers", configJsonPath);
            int numHeads   = RequireInt(r, "num_attention_heads", configJsonPath);
            int numKvHeads = OptionalInt(r, "num_key_value_heads", numHeads, configJsonPath);
            int intermSize = RequireInt(r, "intermediate_size", configJsonPath);
            int vocabSize  = RequireInt(r, "vocab_size", configJsonPath);

            string dtype = GetString(r, "torch_dtype") ?? "bfloat16";
            string act   = GetString(r, "hidden_act") ?? "silu";
            string mtype = GetString(r, "model_type") ?? "llama";

            double theta  = OptionalDouble(r, "rope_theta", 10000.0, configJsonPath);
            double rmsEps = OptionalDouble(r, "rms_norm_eps",
                                OptionalDouble(r, "layer_norm_eps", 1e-5, configJsonPath),
                                configJsonPath);

            byte[] canonical = CanonicalizeJson(raw);
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
        finally
        {
            SynInterop.RecipeFree(r);
        }
    }

    private const int RecipeOk = 0, RecipeMissing = -2, RecipeTypeErr = -3;

    private static string? GetString(IntPtr recipe, string field)
    {
        IntPtr p = SynInterop.RecipeGetField(recipe, field);
        return p == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p);
    }

    private static int RequireInt(IntPtr recipe, string field, string path)
    {
        long v;
        int rc;
        unsafe { rc = SynInterop.RecipeGetInt(recipe, field, &v); }
        if (rc == RecipeMissing)
            throw new InvalidOperationException(
                $"config.json missing required field '{field}' — refusing to assume a default. " +
                "The model architecture must declare its dimensions explicitly.");
        if (rc != RecipeOk)
            throw new InvalidDataException(
                $"config.json field '{field}' in '{path}' is not an integer (recipe_get_int={rc}).");
        return checked((int)v);
    }

    private static int OptionalInt(IntPtr recipe, string field, int fallback, string path)
    {
        long v;
        int rc;
        unsafe { rc = SynInterop.RecipeGetInt(recipe, field, &v); }
        if (rc == RecipeMissing) return fallback;
        if (rc != RecipeOk)
            throw new InvalidDataException(
                $"config.json field '{field}' in '{path}' is present but not an integer " +
                $"(recipe_get_int={rc}) — refusing to fall back to {fallback} over a malformed value.");
        return checked((int)v);
    }

    private static double OptionalDouble(IntPtr recipe, string field, double fallback, string path)
    {
        double v;
        int rc;
        unsafe { rc = SynInterop.RecipeGetDouble(recipe, field, &v); }
        if (rc == RecipeMissing) return fallback;
        if (rc != RecipeOk)
            throw new InvalidDataException(
                $"config.json field '{field}' in '{path}' is present but not a number " +
                $"(recipe_get_double={rc}) — refusing to fall back to {fallback} over a malformed value.");
        return v;
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
        StageLegacyRecipe(b, recipe, sourceId, modelRecipeTypeId, hasHiddenSizeTypeId, hasNumLayersTypeId,
            hasNumHeadsTypeId, hasNumKvHeadsTypeId, hasIntermSizeTypeId, hasVocabSizeTypeId,
            isATypeId, architectureEntityId);
        return b.Build();
    }

    public static void StageLegacyRecipe(
        SubstrateChangeBuilder b,
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
        b.AddEntity(recipe.RecipeEntityId, EntityTier.Word, modelRecipeTypeId, firstObservedBy: sourceId);

        void AddAttestation(Hash128 typeId, Hash128? objectId)
            => b.AddAttestation(NativeAttestation.CategoricalResolved(
                recipe.RecipeEntityId, typeId, objectId, sourceId, null, 1.0));

        void AddScalar(Hash128 typeId, string value)
        {
            var valueId = ModelCoordinates.ScalarId(value);
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
    }




    // The ONLY remaining managed JSON use: its output bytes are hashed into
    // RecipeEntityId, so moving it is an identity-affecting change (GH #552).
    private static byte[] CanonicalizeJson(byte[] rawUtf8)
    {
        using var doc = JsonDocument.Parse(rawUtf8);
        var root = doc.RootElement;
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
