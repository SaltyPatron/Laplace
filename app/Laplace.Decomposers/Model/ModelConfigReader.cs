using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;







public static class ModelConfigReader
{
    public sealed record Result(ModelConfig Config, Modality Modality, Coverage Coverage);


    private static readonly HashSet<string> VisionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "clip_vision_model", "vit", "siglip_vision_model", "siglip", "convnext", "dinov2",
        "swin", "beit", "deit", "vision",
    };
    private static readonly HashSet<string> AudioTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "whisper", "wav2vec2", "hubert", "encodec", "musicgen", "audio",
    };
    private static readonly HashSet<string> DiffusionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "unet", "unet2dconditionmodel", "dit", "diffusion",
    };

    public static Result Read(string configJsonPath)
    {
        if (!File.Exists(configJsonPath))
            return Unsupported("(no config.json)", "(unknown)");

        byte[] raw;
        JsonDocument doc;
        try
        {
            raw = File.ReadAllBytes(configJsonPath);
            doc = JsonDocument.Parse(raw);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "ModelConfigReader: failed to read/parse '{Path}': {Message}", configJsonPath, ex.Message);
            return Unsupported("(unparseable config.json)", "(unknown)");
        }

        using (doc)
        {
            var root = doc.RootElement;

            string arch = "(unknown)";
            if (root.TryGetProperty("architectures", out var archArr)
                && archArr.ValueKind == JsonValueKind.Array && archArr.GetArrayLength() > 0)
                arch = archArr[0].GetString() ?? arch;

            string modelType = Str(root, "model_type", "");
            if (modelType.Length == 0) modelType = InferModelTypeFromArch(arch);

            Modality modality = ClassifyModality(modelType, arch);

            int vocab = FirstInt(root, 0, "vocab_size");
            int hidden = FirstInt(root, 0, "hidden_size", "n_embd", "d_model", "model_dim");
            int layers = FirstInt(root, 0, "num_hidden_layers", "n_layer", "num_layers", "n_layers");
            int heads = FirstInt(root, 0, "num_attention_heads", "n_head", "num_heads");
            int kvHeads = FirstInt(root, heads, "num_key_value_heads", "num_kv_heads", "n_kv_heads");
            int interm = FirstInt(root, 0, "intermediate_size", "ffn_dim", "n_inner", "encoder_ffn_dim");
            int headDim = FirstInt(root, 0, "head_dim", "attention_head_dim");
            if (headDim <= 0 && heads > 0 && hidden > 0) headDim = hidden / heads;

            int experts = FirstInt(root, 0, "num_local_experts", "num_experts", "n_routed_experts");

            bool tie = Bool(root, "tie_word_embeddings", false);
            bool qkNorm = Bool(root, "use_qk_norm", false)
                       || Bool(root, "qk_layernorm", false)
                       || Bool(root, "attention_qk_norm", false);
            double rope = Dbl(root, 10000.0, "rope_theta", "rotary_emb_base");
            double eps = Dbl(root, 1e-5, "rms_norm_eps", "layer_norm_eps", "layer_norm_epsilon");

            int qLora = FirstInt(root, 0, "q_lora_rank");
            int kvLora = FirstInt(root, 0, "kv_lora_rank");
            int qkRope = FirstInt(root, 0, "qk_rope_head_dim");
            int qkNope = FirstInt(root, 0, "qk_nope_head_dim");
            int vHeadDim = FirstInt(root, 0, "v_head_dim");

            byte[] canonical = CanonicalizeJson(root);
            var recipeId = Hash128.Blake3(canonical);

            var cfg = new ModelConfig
            {
                ModelType = modelType.Length == 0 ? "(unknown)" : modelType,
                Architecture = arch,
                VocabSize = vocab,
                HiddenSize = hidden,
                NumLayers = layers,
                NumHeads = heads,
                NumKvHeads = kvHeads > 0 ? kvHeads : heads,
                HeadDim = headDim,
                IntermediateSize = interm,
                NumExperts = experts,
                TieWordEmbeddings = tie,
                QkNorm = qkNorm,
                RopeTheta = rope,
                NormEps = eps,
                MlaQLoraRank = qLora,
                MlaKvLoraRank = kvLora,
                QkRopeHeadDim = qkRope,
                QkNopeHeadDim = qkNope,
                VHeadDim = vHeadDim,
                RecipeEntityId = recipeId,
                CanonicalJson = canonical,
            };

            Coverage coverage = VerdictFor(cfg, modality);
            return new Result(cfg, modality, coverage);
        }
    }

    private static Coverage VerdictFor(ModelConfig cfg, Modality modality)
    {

        bool textAnchored = cfg.VocabSize > 0 && cfg.HiddenSize > 0
                          && cfg.NumLayers > 0 && cfg.NumHeads > 0;
        if (modality == Modality.Text && textAnchored) return Coverage.Full;


        if (modality is Modality.Vision or Modality.Audio or Modality.Diffusion) return Coverage.Partial;
        if (cfg.HiddenSize > 0 && cfg.VocabSize > 0) return Coverage.Partial;

        return Coverage.Unsupported;
    }

    private static Modality ClassifyModality(string modelType, string arch)
    {
        string m = modelType.ToLowerInvariant();
        string a = arch.ToLowerInvariant();
        if (VisionTypes.Contains(m) || a.Contains("vision") || a.Contains("vit")) return Modality.Vision;
        if (AudioTypes.Contains(m) || a.Contains("whisper") || a.Contains("wav2vec")) return Modality.Audio;
        if (DiffusionTypes.Contains(m) || a.Contains("unet") || a.Contains("diffusion")) return Modality.Diffusion;
        if (m.Length == 0 && a == "(unknown)") return Modality.Unknown;

        return Modality.Text;
    }

    private static string InferModelTypeFromArch(string arch)
    {
        string a = arch.ToLowerInvariant();
        if (a.Contains("llama")) return "llama";
        if (a.Contains("qwen3moe") || a.Contains("qwen3_moe")) return "qwen3_moe";
        if (a.Contains("qwen3")) return "qwen3";
        if (a.Contains("qwen2")) return "qwen2";
        if (a.Contains("mixtral")) return "mixtral";
        if (a.Contains("mistral")) return "mistral";
        if (a.Contains("deepseek")) return "deepseek_v2";
        if (a.Contains("phi")) return "phi";
        if (a.Contains("gemma")) return "gemma";
        if (a.Contains("bert")) return "bert";
        return "";
    }

    private static Result Unsupported(string mtype, string arch)
    {
        var cfg = new ModelConfig
        {
            ModelType = mtype,
            Architecture = arch,
            VocabSize = 0,
            HiddenSize = 0,
            NumLayers = 0,
            NumHeads = 0,
            NumKvHeads = 0,
            HeadDim = 0,
            IntermediateSize = 0,
            NumExperts = 0,
            TieWordEmbeddings = false,
            QkNorm = false,
            RopeTheta = 10000.0,
            NormEps = 1e-5,
            MlaQLoraRank = 0,
            MlaKvLoraRank = 0,
            QkRopeHeadDim = 0,
            QkNopeHeadDim = 0,
            VHeadDim = 0,
            RecipeEntityId = Hash128.Zero,
            CanonicalJson = Encoding.UTF8.GetBytes("{}"),
        };
        return new Result(cfg, Modality.Unknown, Coverage.Unsupported);
    }

    private static int FirstInt(JsonElement root, int def, params string[] keys)
    {
        foreach (var key in keys)
            if (root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number
                && p.TryGetInt32(out var v)) return v;
        return def;
    }

    private static double Dbl(JsonElement root, double def, params string[] keys)
    {
        foreach (var key in keys)
            if (root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number)
                return p.GetDouble();
        return def;
    }

    private static bool Bool(JsonElement root, string key, bool def)
        => root.TryGetProperty(key, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => def,
            }
            : def;

    private static string Str(JsonElement root, string key, string def)
        => root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? def : def;

    private static byte[] CanonicalizeJson(JsonElement root)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        var props = new List<JsonProperty>();
        foreach (var p in root.EnumerateObject()) props.Add(p);
        props.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        foreach (var p in props) { writer.WritePropertyName(p.Name); p.Value.WriteTo(writer); }
        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }
}
