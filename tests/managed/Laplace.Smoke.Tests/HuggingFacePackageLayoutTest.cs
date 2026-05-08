namespace Laplace.Smoke.Tests;

using System;
using System.IO;
using System.Linq;

using Laplace.Decomposers.Model;

using Xunit;

/// <summary>
/// Validates HuggingFacePackageLayoutResolver + HuggingFacePackageManifest
/// against real model directories on disk at D:\Models\hub. Three layouts:
///   1. HuggingFace cache: models--&lt;org&gt;--&lt;name&gt;/snapshots/&lt;sha&gt;/
///      → MiniLM (sentence-transformers/all-MiniLM-L6-v2)
///   2. Direct directory: top-level dir has all artifacts
///      → Florence-2-base
///   3. Diffusers multi-component: model_index.json + named subcomponent dirs
///      → FLUX.2-dev
///
/// Each test is env-gated: skipped (not failed) when the model isn't on disk.
/// </summary>
public class HuggingFacePackageLayoutTest
{
    private const string MiniLmCachePath =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2";

    private const string Florence2DirectPath =
        @"D:\Models\hub\Florence-2-base";

    private const string FluxDiffusionPath =
        @"D:\Models\hub\xmodels--black-forest-labs--FLUX.2-dev";

    [Fact]
    public void Resolve_MiniLmCacheLayout_PointsAtSnapshotDirectory()
    {
        if (!Directory.Exists(MiniLmCachePath)) { return; }

        var resolved = HuggingFacePackageLayoutResolver.Resolve(MiniLmCachePath);

        Assert.Equal(HuggingFacePackageLayout.HuggingFaceCache, resolved.Layout);
        Assert.Equal(MiniLmCachePath, resolved.PackagePath);
        Assert.NotEqual(MiniLmCachePath, resolved.ContentRoot);
        Assert.Contains("snapshots", resolved.ContentRoot);
        Assert.True(Directory.Exists(resolved.ContentRoot));
        // Snapshot dir must contain config.json (anchor file).
        Assert.True(File.Exists(Path.Combine(resolved.ContentRoot, "config.json")));
    }

    [Fact]
    public void Resolve_Florence2DirectLayout_ContentRootEqualsPackagePath()
    {
        if (!Directory.Exists(Florence2DirectPath)) { return; }

        var resolved = HuggingFacePackageLayoutResolver.Resolve(Florence2DirectPath);

        Assert.Equal(HuggingFacePackageLayout.DirectDirectory, resolved.Layout);
        Assert.Equal(Florence2DirectPath, resolved.PackagePath);
        Assert.Equal(Florence2DirectPath, resolved.ContentRoot);
        Assert.True(File.Exists(Path.Combine(resolved.ContentRoot, "config.json")));
    }

    [Fact]
    public void Resolve_FluxDiffusionLayout_DetectsMultiComponent()
    {
        if (!Directory.Exists(FluxDiffusionPath)) { return; }

        var resolved = HuggingFacePackageLayoutResolver.Resolve(FluxDiffusionPath);

        Assert.Equal(HuggingFacePackageLayout.HuggingFaceCache, resolved.Layout);
        // FLUX is in HF cache layout; the snapshot dir is the diffusers root.
        Assert.True(File.Exists(Path.Combine(resolved.ContentRoot, "model_index.json")));
    }

    [Fact]
    public void Manifest_MiniLm_DiscoversConfigTokenizerAndSafetensors()
    {
        if (!Directory.Exists(MiniLmCachePath)) { return; }

        var resolved = HuggingFacePackageLayoutResolver.Resolve(MiniLmCachePath);
        var manifest = resolved.Discover();

        Assert.NotNull(manifest.ConfigJsonPath);
        Assert.NotNull(manifest.TokenizerJsonPath);
        Assert.NotNull(manifest.TokenizerConfigJsonPath);
        Assert.NotNull(manifest.SpecialTokensMapPath);
        Assert.NotNull(manifest.VocabPath);          // vocab.txt for BERT WordPiece
        Assert.EndsWith("vocab.txt", manifest.VocabPath);
        Assert.Single(manifest.SafetensorShardPaths); // single model.safetensors
        Assert.Null(manifest.SafetensorIndexJsonPath); // not sharded
        Assert.NotNull(manifest.ReadmePath);
        Assert.Empty(manifest.SubComponents);          // not diffusers
    }

    [Fact]
    public void Manifest_Florence2_DiscoversCustomCodeAndPreprocessor()
    {
        if (!Directory.Exists(Florence2DirectPath)) { return; }

        var resolved = HuggingFacePackageLayoutResolver.Resolve(Florence2DirectPath);
        var manifest = resolved.Discover();

        Assert.NotNull(manifest.ConfigJsonPath);
        Assert.NotNull(manifest.TokenizerJsonPath);
        Assert.NotNull(manifest.PreprocessorConfigPath); // vision model has preprocessor
        Assert.NotNull(manifest.VocabPath);              // vocab.json (BPE)
        Assert.EndsWith("vocab.json", manifest.VocabPath);
        Assert.Single(manifest.SafetensorShardPaths);
        Assert.NotEmpty(manifest.CustomCodePyPaths);     // modeling_florence2.py etc.
        Assert.Contains(manifest.CustomCodePyPaths, p => p.EndsWith("modeling_florence2.py",      StringComparison.Ordinal));
        Assert.Contains(manifest.CustomCodePyPaths, p => p.EndsWith("configuration_florence2.py", StringComparison.Ordinal));
        Assert.Contains(manifest.CustomCodePyPaths, p => p.EndsWith("processing_florence2.py",    StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_FluxDiffusion_DiscoversSubComponents()
    {
        if (!Directory.Exists(FluxDiffusionPath)) { return; }

        var resolved = HuggingFacePackageLayoutResolver.Resolve(FluxDiffusionPath);
        // FLUX is HF cache layout but the snapshot dir IS the diffusers root.
        // Re-resolve using the snapshot dir so the manifest sees the
        // model_index.json + subcomponents.
        var diffusersResolved = HuggingFacePackageLayoutResolver.Resolve(resolved.ContentRoot);
        Assert.Equal(HuggingFacePackageLayout.DiffusersMultiComponent, diffusersResolved.Layout);

        var manifest = diffusersResolved.Discover();

        Assert.NotNull(manifest.ModelIndexJsonPath);
        Assert.NotEmpty(manifest.SubComponents);

        // FLUX components: scheduler, text_encoder, tokenizer, transformer, vae.
        var componentNames = manifest.SubComponents
            .Select(sc => Path.GetFileName(sc.ContentRoot))
            .ToHashSet();
        Assert.Contains("text_encoder", componentNames);
        Assert.Contains("transformer",  componentNames);
        Assert.Contains("vae",          componentNames);
        Assert.Contains("tokenizer",    componentNames);

        // Each transformer component is sharded.
        var transformer = manifest.SubComponents
            .First(sc => Path.GetFileName(sc.ContentRoot) == "transformer");
        Assert.True(transformer.SafetensorShardPaths.Count > 1);
        Assert.NotNull(transformer.SafetensorIndexJsonPath);
    }
}
