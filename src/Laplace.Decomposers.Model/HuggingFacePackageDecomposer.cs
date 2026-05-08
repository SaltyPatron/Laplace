namespace Laplace.Decomposers.Model;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Abstractions;

/// <summary>
/// F5 top-level orchestrator. Takes the path to a HuggingFace package on
/// disk (any of the three observed layouts: HF cache / direct dir /
/// diffusers multi-component) and emits all substrate state for the model:
///   1. Layout resolution → content root
///   2. Manifest discovery → semantic pieces (config / tokenizer /
///      safetensors shard(s) / preprocessor / chat template / etc.)
///   3. Per-piece decomposition via dedicated sub-decomposers
///   4. Recursion into diffusers subcomponents
///
/// Sub-decomposers compose F1 TextDecomposer + F2 number/unit + emission
/// services + provenance. Each emits substrate entities + edges via the
/// caller-supplied IBatchSink.
///
/// Phase 4 / Track F5 / G5. The first F5 product slice — combined with
/// per-tensor extractors (P5.2) and Recomposer.Model (P5.3), this is the
/// ingest+export loop Anthony's invention specifies.
/// </summary>
public sealed class HuggingFacePackageDecomposer
{
    /// <summary>Tensor-name suffixes that identify the model's vocab embedding
    /// tensor. The first matching tensor in the safetensors header is used as
    /// the source for firefly extraction. Per architecture conventions:
    ///   - BERT/MiniLM: bert.embeddings.word_embeddings.weight
    ///   - GPT/Llama/Qwen: model.embed_tokens.weight or transformer.wte.weight
    ///   - T5: shared.weight
    /// </summary>
    private static readonly string[] EmbeddingTensorSuffixes =
    {
        "word_embeddings.weight",
        "embed_tokens.weight",
        "wte.weight",
        "shared.weight",
    };

    private const int FireflyKnnK     = 20;
    private const int FireflyOutputDim = 4; // S³

    private readonly TokenizerAssetDecomposer     _tokenizer;
    private readonly SafetensorsHeaderDecomposer  _safetensors;
    private readonly ITensorReader?               _tensorReader;
    private readonly IFireflyExtraction?          _fireflyExtraction;
    private readonly IFireflyJar?                 _fireflyJar;

    public HuggingFacePackageDecomposer(
        TokenizerAssetDecomposer     tokenizer,
        SafetensorsHeaderDecomposer  safetensors,
        ITensorReader?               tensorReader      = null,
        IFireflyExtraction?          fireflyExtraction = null,
        IFireflyJar?                 fireflyJar        = null)
    {
        _tokenizer         = tokenizer;
        _safetensors       = safetensors;
        _tensorReader      = tensorReader;
        _fireflyExtraction = fireflyExtraction;
        _fireflyJar        = fireflyJar;
    }

    public async Task<DecomposeResult> DecomposeAsync(
        AtomId             modelEntityHash,
        string             modelSourceCanonicalName,
        string             packagePath,
        CancellationToken  cancellationToken)
    {
        var resolved = HuggingFacePackageLayoutResolver.Resolve(packagePath);
        var manifest = resolved.Discover();
        var result   = new DecomposeResult();

        await DecomposeManifest(modelEntityHash, modelSourceCanonicalName, manifest, result, cancellationToken)
            .ConfigureAwait(false);

        // Diffusers multi-component: recurse into each subcomponent. Each
        // subcomponent uses the same model entity hash + source name —
        // Voronoi consensus across subcomponents emerges naturally because
        // they share the model identity.
        foreach (var sub in manifest.SubComponents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DecomposeManifest(modelEntityHash, modelSourceCanonicalName, sub, result, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    private async Task DecomposeManifest(
        AtomId                       modelEntityHash,
        string                       modelSourceCanonicalName,
        HuggingFacePackageManifest   manifest,
        DecomposeResult              result,
        CancellationToken            cancellationToken)
    {
        // 1. Tokenizer asset (vocab → substrate text entities + has_vocab_token edges).
        //    Returns token_id → substrate token entity map for downstream firefly
        //    binding (each embedding row maps to the substrate entity its token surface
        //    canonicalizes to).
        IReadOnlyDictionary<int, AtomId>? tokenIdToSubstrateEntity = null;
        if (manifest.TokenizerJsonPath is not null)
        {
            tokenIdToSubstrateEntity = await _tokenizer.DecomposeAsync(
                modelEntityHash,
                modelSourceCanonicalName,
                manifest.TokenizerJsonPath,
                cancellationToken).ConfigureAwait(false);
            result.TokenizerAssetsDecomposed++;
        }

        // 2. Safetensors header(s) (per-tensor entity + has_tensor edge).
        foreach (var shardPath in manifest.SafetensorShardPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(shardPath)) { continue; }
            var emitted = await _safetensors.DecomposeAsync(
                modelEntityHash,
                modelSourceCanonicalName,
                shardPath,
                cancellationToken).ConfigureAwait(false);
            result.TotalTensorsDecomposed += emitted;
            result.SafetensorShardsDecomposed++;
        }

        // 3. Embedding fireflies. Requires all three optional services + a
        //    successful tokenizer decompose (need the token_id → substrate
        //    entity map to bind tensor rows to substrate token entities).
        if (_tensorReader is not null && _fireflyExtraction is not null && _fireflyJar is not null
            && tokenIdToSubstrateEntity is not null
            && manifest.SafetensorShardPaths.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var firefliesEmitted = await ExtractAndStoreEmbeddingFireflies(
                modelEntityHash,
                manifest.SafetensorShardPaths,
                tokenIdToSubstrateEntity,
                cancellationToken).ConfigureAwait(false);
            result.FirefliesEmitted += firefliesEmitted;
        }

        // 3. Future: ConfigJsonDecomposer (P5.x), GenerationConfigDecomposer,
        //    PreprocessorConfigDecomposer, SpecialTokensMapDecomposer,
        //    ChatTemplateDecomposer, ReadmeDecomposer, LicenseDecomposer,
        //    PythonModelingDecomposer. Each of those depends on the format
        //    decomposer layer (P2.1-P2.9: tree-sitter + JsonAst + Markdown +
        //    Jinja + Python AST). Lands as the format-decomposer layer
        //    completes.
    }

    /// <summary>
    /// Find the embedding tensor in the safetensors shard, extract its rows
    /// to S³ via FireflyExtraction, and store one firefly per token via
    /// FireflyJar. Returns the number of fireflies stored. Returns 0 if no
    /// embedding tensor was found in any shard.
    /// </summary>
    private async Task<int> ExtractAndStoreEmbeddingFireflies(
        AtomId                            modelEntityHash,
        IReadOnlyList<string>             shardPaths,
        IReadOnlyDictionary<int, AtomId>  tokenIdToSubstrateEntity,
        CancellationToken                 cancellationToken)
    {
        if (_tensorReader is null || _fireflyExtraction is null || _fireflyJar is null)
        {
            return 0;
        }

        // Find embedding tensor: try each shard until we hit one that
        // contains a tensor whose name ends with one of the known suffixes.
        foreach (var shardPath in shardPaths)
        {
            if (!File.Exists(shardPath)) { continue; }
            using var handle = _tensorReader.Open(shardPath);

            SafetensorEntry? embedding = null;
            foreach (var e in handle.Entries)
            {
                foreach (var suffix in EmbeddingTensorSuffixes)
                {
                    if (e.Name.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        embedding = e;
                        break;
                    }
                }
                if (embedding is not null) { break; }
            }
            if (embedding is null) { continue; }
            if (embedding.Shape.Length != 2)  { continue; }

            var vocabSize = (int)embedding.Shape[0];
            var hiddenDim = (int)embedding.Shape[1];

            // Decode the tensor losslessly to double[].
            var matrix = new double[(long)vocabSize * hiddenDim];
            handle.ReadFloat64(embedding, matrix);

            // KNN + Laplacian eigenmap → one Point4D per row.
            var fireflies = _fireflyExtraction.Project(
                matrix, vocabSize, hiddenDim, FireflyKnnK, seed: 0);
            _ = FireflyOutputDim;

            // Store one firefly per (token_id, substrate_entity) pair from
            // the tokenizer map. Token IDs not in the map (rare gap-fill IDs
            // or special tokens with no canonical text mapping) are skipped.
            var stored = 0;
            for (var tokenId = 0; tokenId < vocabSize; tokenId++)
            {
                if (!tokenIdToSubstrateEntity.TryGetValue(tokenId, out var substrateEntity))
                {
                    continue;
                }
                await _fireflyJar.StoreAsync(
                    substrateEntity,
                    modelEntityHash,
                    fireflies[tokenId],
                    cancellationToken).ConfigureAwait(false);
                stored++;
            }
            return stored;
        }

        return 0; // no embedding tensor found across any shard
    }

    public sealed class DecomposeResult
    {
        public int TokenizerAssetsDecomposed   { get; set; }
        public int SafetensorShardsDecomposed  { get; set; }
        public int TotalTensorsDecomposed      { get; set; }
        public int FirefliesEmitted            { get; set; }
    }
}
