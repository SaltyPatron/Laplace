namespace Laplace.Smoke.Tests;

using System.IO;

using Laplace.Core;
using Laplace.Core.Abstractions;

using Xunit;

/// <summary>
/// Validates the FireflyExtraction wrapper composes B15 KnnExactService +
/// B17 LaplacianEigenmapService into one operation that maps an AI model's
/// embedding matrix to per-token Point4D positions on S³.
///
/// Tested against MiniLM (30,522 × 384 BERT WordPiece embedding); each
/// row is asserted to lie on S³ (‖q‖ = 1 ± 1e-9) and the projection is
/// deterministic across runs.
///
/// Env-gated: skipped when MiniLM isn't on disk.
/// </summary>
public class FireflyExtractionTest
{
    private const string ModelDir =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed1d9f207416be6d2e6f8de32d1f16199bf";

    [Fact]
    public void FireflyExtraction_OnMiniLm_ProjectsAllTokensToUnitSphere()
    {
        var safetensorsPath = Path.Combine(ModelDir, "model.safetensors");
        if (!File.Exists(safetensorsPath))
        {
            return; // env-gated
        }

        var reader = new TensorReader();
        using var handle = reader.Open(safetensorsPath);

        SafetensorEntry? embedding = null;
        foreach (var e in handle.Entries)
        {
            if (e.Name.EndsWith("word_embeddings.weight", System.StringComparison.Ordinal))
            {
                embedding = e;
                break;
            }
        }
        Assert.NotNull(embedding);

        var vocabSize = (int)embedding.Shape[0];
        var hiddenDim = (int)embedding.Shape[1];
        var matrix    = new double[(long)vocabSize * hiddenDim];
        handle.ReadFloat64(embedding, matrix);

        var extractor = new FireflyExtraction(new KnnExact(), new LaplacianEigenmap());
        var fireflies = extractor.Project(matrix, vocabSize, hiddenDim, kNearest: 20, seed: 0);

        Assert.Equal(vocabSize, fireflies.Length);

        // Every firefly is on S³ within numerical tolerance.
        for (var i = 0; i < fireflies.Length; i++)
        {
            var p = fireflies[i];
            Assert.InRange(p.Norm, 1.0 - 1e-9, 1.0 + 1e-9);
        }
    }

    [Fact]
    public void FireflyExtraction_OnMiniLm_DeterministicAcrossRuns()
    {
        var safetensorsPath = Path.Combine(ModelDir, "model.safetensors");
        if (!File.Exists(safetensorsPath))
        {
            return; // env-gated
        }

        var reader = new TensorReader();
        using var handle = reader.Open(safetensorsPath);

        SafetensorEntry? embedding = null;
        foreach (var e in handle.Entries)
        {
            if (e.Name.EndsWith("word_embeddings.weight", System.StringComparison.Ordinal))
            {
                embedding = e;
                break;
            }
        }
        Assert.NotNull(embedding);

        var vocabSize = (int)embedding.Shape[0];
        var hiddenDim = (int)embedding.Shape[1];
        var matrix    = new double[(long)vocabSize * hiddenDim];
        handle.ReadFloat64(embedding, matrix);

        var extractor = new FireflyExtraction(new KnnExact(), new LaplacianEigenmap());
        var first  = extractor.Project(matrix, vocabSize, hiddenDim, kNearest: 20, seed: 0);
        var second = extractor.Project(matrix, vocabSize, hiddenDim, kNearest: 20, seed: 0);

        Assert.Equal(first.Length, second.Length);
        for (var i = 0; i < first.Length; i++)
        {
            Assert.Equal(first[i].X, second[i].X);
            Assert.Equal(first[i].Y, second[i].Y);
            Assert.Equal(first[i].Z, second[i].Z);
            Assert.Equal(first[i].W, second[i].W);
        }
    }
}
