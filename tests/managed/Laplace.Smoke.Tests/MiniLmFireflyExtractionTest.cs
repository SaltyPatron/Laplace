namespace Laplace.Smoke.Tests;

using System;
using System.IO;

using Laplace.Core;
using Laplace.Core.Abstractions;

using Xunit;

/// <summary>
/// End-to-end F5 firefly-extraction validation on a real ~90 MB HuggingFace
/// model (sentence-transformers/all-MiniLM-L6-v2 — 30,522 vocab × 384
/// embedding dim). Exercises the just-built native primitives B15
/// KnnExactService + B17 LaplacianEigenmapService + B19 TensorDecodeService
/// against actual model weights, in-memory, no DB, no Python.
///
/// Validates:
///   1. TensorReader (B19 P/Invoke wrapper) reads the safetensors header +
///      streams the embedding tensor losslessly into double[].
///   2. KnnExact.SelfCosine produces vocab × k indices/sims with no NaN/Inf.
///   3. LaplacianEigenmap.EmbedToSphere produces vocab × 4 unit vectors on S^3.
///   4. Determinism — re-running on the same input yields byte-identical
///      output (eligible to be captured as a per-model golden delta record
///      per the AI-models-are-static-content design principle).
///
/// Skipped (not failed) when the model isn't on disk — env-gated like the
/// substrate generator tests.
/// </summary>
public class MiniLmFireflyExtractionTest
{
    private const string ModelDir =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed1d9f207416be6d2e6f8de32d1f16199bf";

    [Fact]
    public void MiniLmEmbedding_KnnPlusEigenmap_ProducesUnitVectorsOnS3_DeterministicallyTwice()
    {
        var safetensorsPath = Path.Combine(ModelDir, "model.safetensors");
        if (!File.Exists(safetensorsPath))
        {
            return; // env-gated
        }

        // Step 1: open safetensors via TensorReader; locate the word_embeddings tensor.
        var reader = new TensorReader();
        using var handle = reader.Open(safetensorsPath);

        SafetensorEntry? embeddingEntry = null;
        foreach (var e in handle.Entries)
        {
            if (e.Name.EndsWith("word_embeddings.weight", StringComparison.Ordinal))
            {
                embeddingEntry = e;
                break;
            }
        }
        Assert.NotNull(embeddingEntry);
        Assert.Equal(2, embeddingEntry.Shape.Length);

        var vocabSize = (int)embeddingEntry.Shape[0];
        var hiddenDim = (int)embeddingEntry.Shape[1];
        Assert.True(vocabSize > 0 && hiddenDim > 0);

        // Step 2: stream tensor losslessly into double[]. Handles F32/F16/BF16/etc.
        var embedding = new double[(long)vocabSize * hiddenDim];
        handle.ReadFloat64(embeddingEntry, embedding);

        // Step 3: run KnnExact.SelfCosine for k = 20.
        const int k = 20;
        var knn = new KnnExact();
        var firstKnn = knn.SelfCosine(embedding, vocabSize, hiddenDim, k);
        Assert.Equal((long)vocabSize * k, firstKnn.Indices.Length);
        Assert.Equal((long)vocabSize * k, firstKnn.Similarities.Length);

        // Spot-check no NaN/Inf in similarities and indices in [0, vocabSize).
        for (var i = 0; i < firstKnn.Similarities.Length; i++)
        {
            Assert.True(double.IsFinite(firstKnn.Similarities[i]),
                $"non-finite similarity at index {i}: {firstKnn.Similarities[i]}");
            Assert.InRange(firstKnn.Indices[i], 0, vocabSize - 1);
        }

        // Step 4: run LaplacianEigenmap.EmbedToSphere for output_dim = 4.
        var eigenmap = new LaplacianEigenmap();
        var firstFireflies = eigenmap.EmbedToSphere(firstKnn.Indices, firstKnn.Similarities, vocabSize, k, 4);
        Assert.Equal((long)vocabSize * 4, firstFireflies.Length);

        // Step 5: every row is on S^3.
        for (var row = 0; row < vocabSize; row++)
        {
            var x = firstFireflies[row * 4 + 0];
            var y = firstFireflies[row * 4 + 1];
            var z = firstFireflies[row * 4 + 2];
            var w = firstFireflies[row * 4 + 3];
            var norm = System.Math.Sqrt(x * x + y * y + z * z + w * w);
            Assert.InRange(norm, 1.0 - 1e-9, 1.0 + 1e-9);
        }

        // Step 6: determinism. Re-run the chain end-to-end and check byte
        // equivalence on the firefly positions. This is the property that
        // makes per-model golden delta records meaningful.
        var secondKnn = knn.SelfCosine(embedding, vocabSize, hiddenDim, k);
        var secondFireflies = eigenmap.EmbedToSphere(secondKnn.Indices, secondKnn.Similarities, vocabSize, k, 4);

        Assert.Equal(firstFireflies.Length, secondFireflies.Length);
        for (var i = 0; i < firstFireflies.Length; i++)
        {
            Assert.Equal(firstFireflies[i], secondFireflies[i]);
        }
    }
}
