namespace Laplace.Decomposers.Model;

using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Discover the semantic pieces present in a HuggingFace package's content
/// root. Each piece type maps to a dedicated decomposer downstream
/// (ConfigJsonDecomposer, TokenizerAssetDecomposer, SafetensorsHeaderDecomposer,
/// ChatTemplateDecomposer, etc.). For diffusers multi-component layouts the
/// manifest recurses into named subcomponent directories.
///
/// Phase 4 / F5 / G5.
/// </summary>
public sealed record HuggingFacePackageManifest(
    string                            ContentRoot,
    HuggingFacePackageLayout          Layout,
    string?                           ConfigJsonPath,
    string?                           TokenizerJsonPath,
    string?                           TokenizerConfigJsonPath,
    string?                           SpecialTokensMapPath,
    string?                           VocabPath,           // vocab.json or vocab.txt
    string?                           MergesTxtPath,       // BPE merges
    string?                           SentencePieceModelPath,
    string?                           GenerationConfigPath,
    string?                           PreprocessorConfigPath,
    string?                           ProcessorConfigPath,
    string?                           ChatTemplatePath,
    string?                           ReadmePath,
    string?                           LicensePath,
    string?                           ModelIndexJsonPath,  // diffusers
    IReadOnlyList<string>             SafetensorShardPaths,// possibly multiple
    string?                           SafetensorIndexJsonPath,// sharded index
    string?                           PytorchBinPath,      // legacy fallback
    IReadOnlyList<string>             CustomCodePyPaths,   // modeling_*.py / configuration_*.py / processing_*.py
    IReadOnlyList<HuggingFacePackageManifest> SubComponents); // diffusers

public static class HuggingFacePackageManifestExtensions
{
    public static HuggingFacePackageManifest Discover(this HuggingFacePackageLayoutResolver.Resolved resolved)
    {
        return DiscoverAt(resolved.ContentRoot, resolved.Layout, isSubComponent: false);
    }

    private static HuggingFacePackageManifest DiscoverAt(
        string                    contentRoot,
        HuggingFacePackageLayout  layout,
        bool                      isSubComponent)
    {
        var files = Directory.GetFiles(contentRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(f => new FileInfo(f))
            .ToArray();

        string? Find(string fileName) => files
            .FirstOrDefault(f => string.Equals(f.Name, fileName, System.StringComparison.OrdinalIgnoreCase))?
            .FullName;

        // Safetensors: single model.safetensors OR sharded model-XXXXX-of-YYYYY.safetensors
        // (+ index.json). Diffusers components use diffusion_pytorch_model* naming.
        var safetensorShards = files
            .Where(f => f.Extension.Equals(".safetensors", System.StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FullName)
            .OrderBy(p => p, System.StringComparer.Ordinal)
            .ToArray();

        // model.safetensors.index.json or diffusion_pytorch_model.safetensors.index.json
        var safetensorIndexJson = files.FirstOrDefault(f =>
            f.Name.EndsWith(".safetensors.index.json", System.StringComparison.OrdinalIgnoreCase))?.FullName;

        // Custom HF model code (modeling_*.py / configuration_*.py / processing_*.py).
        var customCode = files
            .Where(f => f.Extension.Equals(".py", System.StringComparison.OrdinalIgnoreCase) &&
                        (f.Name.StartsWith("modeling_",      System.StringComparison.OrdinalIgnoreCase) ||
                         f.Name.StartsWith("configuration_", System.StringComparison.OrdinalIgnoreCase) ||
                         f.Name.StartsWith("processing_",    System.StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.FullName)
            .ToArray();

        // Vocab can be vocab.json (BPE byte-level) or vocab.txt (WordPiece).
        var vocabPath = Find("vocab.json") ?? Find("vocab.txt");

        // SentencePiece model files: tokenizer.model is the convention.
        var spModel = Find("tokenizer.model");

        // Diffusers components: recurse into subdirs. Subcomponent dirs are
        // declared in model_index.json — for the manifest layer we just
        // discover all directories with a config.json (the universal
        // marker of a model-shaped subcomponent).
        var subComponents = layout == HuggingFacePackageLayout.DiffusersMultiComponent && !isSubComponent
            ? Directory.GetDirectories(contentRoot)
                .Where(d => File.Exists(Path.Combine(d, "config.json"))
                         || File.Exists(Path.Combine(d, "scheduler_config.json"))
                         || File.Exists(Path.Combine(d, "tokenizer_config.json"))
                         || File.Exists(Path.Combine(d, "preprocessor_config.json")))
                .Select(d => DiscoverAt(d, HuggingFacePackageLayout.DirectDirectory, isSubComponent: true))
                .ToArray()
            : System.Array.Empty<HuggingFacePackageManifest>();

        return new HuggingFacePackageManifest(
            ContentRoot:             contentRoot,
            Layout:                  layout,
            ConfigJsonPath:          Find("config.json"),
            TokenizerJsonPath:       Find("tokenizer.json"),
            TokenizerConfigJsonPath: Find("tokenizer_config.json"),
            SpecialTokensMapPath:    Find("special_tokens_map.json"),
            VocabPath:               vocabPath,
            MergesTxtPath:           Find("merges.txt"),
            SentencePieceModelPath:  spModel,
            GenerationConfigPath:    Find("generation_config.json"),
            PreprocessorConfigPath:  Find("preprocessor_config.json"),
            ProcessorConfigPath:     Find("processor_config.json"),
            ChatTemplatePath:        Find("chat_template.jinja"),
            ReadmePath:              Find("README.md"),
            LicensePath:             Find("LICENSE") ?? Find("LICENSE.txt") ?? Find("LICENSE.md"),
            ModelIndexJsonPath:      Find("model_index.json"),
            SafetensorShardPaths:    safetensorShards,
            SafetensorIndexJsonPath: safetensorIndexJson,
            PytorchBinPath:          Find("pytorch_model.bin"),
            CustomCodePyPaths:       customCode,
            SubComponents:           subComponents);
    }
}
