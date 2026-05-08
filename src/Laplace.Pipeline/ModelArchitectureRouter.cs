namespace Laplace.Pipeline;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Laplace.Decomposers.Abstractions;

/// <summary>
/// F3 IModelArchitectureRouter. Reads <c>config.json</c> from a HuggingFace
/// model directory and maps the <c>architectures</c> array's first entry to
/// one of the canonical architecture-family names registered as substrate
/// concept entities (DecoderOnly / EncoderDecoder / EncoderOnly / MoE /
/// MoeMla / VisionEncoder / AudioEncoder / AudioDecoder / Diffusion /
/// Multimodal / Reranker). Returns "unknown" when config.json is missing
/// or the architectures field doesn't match a known family.
/// </summary>
public sealed class ModelArchitectureRouter : IModelArchitectureRouter
{
    public const string Unknown = "unknown";

    /// <summary>HuggingFace architectures → canonical family name. Reads
    /// the architecture string and matches by suffix or by substring on
    /// well-known model-family markers.</summary>
    private static readonly (string ArchitectureSubstring, string FamilyName)[] FamilyMap =
    {
        // Pure decoder-only causal LMs.
        ("ForCausalLM",                     "DecoderOnly"),
        ("LMHeadModel",                     "DecoderOnly"),
        ("LlamaModel",                      "DecoderOnly"),

        // Encoder-decoder.
        ("ForConditionalGeneration",        "EncoderDecoder"),
        ("Seq2SeqLM",                       "EncoderDecoder"),
        ("MarianMTModel",                   "EncoderDecoder"),

        // Reranker / pairwise-relevance heads.
        ("ForSequenceClassification",       "Reranker"),
        ("Reranker",                        "Reranker"),
        ("CrossEncoder",                    "Reranker"),

        // Encoder-only (sentence-transformer style — embeddings).
        ("BertModel",                       "EncoderOnly"),
        ("RobertaModel",                    "EncoderOnly"),
        ("DistilBertModel",                 "EncoderOnly"),
        ("XLMRobertaModel",                 "EncoderOnly"),
        ("BertForMaskedLM",                 "EncoderOnly"),
        ("MPNetModel",                      "EncoderOnly"),
        ("E5Model",                         "EncoderOnly"),

        // Vision.
        ("ViTModel",                        "VisionEncoder"),
        ("ViTForImageClassification",       "VisionEncoder"),
        ("CLIPVisionModel",                 "VisionEncoder"),
        ("Dinov2Model",                     "VisionEncoder"),

        // Audio.
        ("Wav2Vec2Model",                   "AudioEncoder"),
        ("Wav2Vec2ForCTC",                  "AudioEncoder"),
        ("WhisperEncoder",                  "AudioEncoder"),
        ("HubertModel",                     "AudioEncoder"),

        ("WhisperDecoder",                  "AudioDecoder"),
        ("VocosDecoder",                    "AudioDecoder"),

        // Diffusion.
        ("StableDiffusionPipeline",         "Diffusion"),
        ("UNet2DConditionModel",            "Diffusion"),
        ("FluxPipeline",                    "Diffusion"),
        ("SD3Transformer2DModel",           "Diffusion"),

        // Multimodal.
        ("ClipModel",                       "Multimodal"),
        ("CLIPModel",                       "Multimodal"),
        ("Florence2ForConditionalGeneration","Multimodal"),
        ("LlavaForConditionalGeneration",   "Multimodal"),
        ("Qwen2VLForConditionalGeneration", "Multimodal"),

        // MoE.
        ("MixtralForCausalLM",              "MoE"),
        ("DeepseekV3ForCausalLM",           "MoeMla"),
        ("DeepseekV2ForCausalLM",           "MoeMla"),
    };

    public string Route(string modelDirectory)
    {
        ArgumentNullException.ThrowIfNull(modelDirectory);
        var configPath = Path.Combine(modelDirectory, "config.json");
        if (!File.Exists(configPath)) { return Unknown; }

        string archString;
        try
        {
            using var fs = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("architectures", out var archsEl)) { return Unknown; }
            if (archsEl.ValueKind != JsonValueKind.Array || archsEl.GetArrayLength() == 0) { return Unknown; }
            var first = archsEl[0];
            if (first.ValueKind != JsonValueKind.String) { return Unknown; }
            archString = first.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return Unknown;
        }

        if (string.IsNullOrEmpty(archString)) { return Unknown; }

        // Match longest suffix/substring first — "Mixtral...ForCausalLM"
        // would otherwise match DecoderOnly via "ForCausalLM"; explicit MoE
        // overrides through being checked first for that prefix.
        if (archString.Contains("Mixtral", StringComparison.Ordinal))   { return "MoE"; }
        if (archString.Contains("DeepseekV3", StringComparison.Ordinal) ||
            archString.Contains("DeepseekV2", StringComparison.Ordinal)) { return "MoeMla"; }

        foreach (var (substring, family) in FamilyMap)
        {
            if (archString.Contains(substring, StringComparison.Ordinal))
            {
                return family;
            }
        }
        return Unknown;
    }
}
