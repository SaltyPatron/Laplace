namespace Laplace.Core;

using System;

using Laplace.Core.Abstractions;

/// <summary>
/// IFireflyExtraction implementation. Projects an AI model's embedding matrix
/// to per-row Point4D positions on S³ via:
///   1. Exact symmetric k-NN cosine graph (B15 KnnExactService — MKL-tiled
///      brute-force GEMM, NEVER HNSW per CLAUDE.md banned patterns)
///   2. Normalized Laplacian + leading-k eigenpairs via Spectra Lanczos
///      (B17 LaplacianEigenmapService) + Gram-Schmidt orthonormalization
///      + S³ projection (4D unit-quaternion).
///
/// One firefly per (token row × ingested model). Per substrate invariant:
/// fireflies are AI-MODEL-EXTRACTION ARTIFACTS, NOT substrate primitives.
/// They live in the firefly_s3_extracted physicality partition, separate
/// from the codepoint_s3_substrate partition holding atom positions.
///
/// Phase 4 / Track F5 / G5. The MiniLM smoke test already proves this
/// pipeline is deterministic + produces unit vectors on S³.
/// </summary>
public sealed class FireflyExtraction : IFireflyExtraction
{
    private readonly IKnnExact            _knn;
    private readonly ILaplacianEigenmap   _eigenmap;

    public FireflyExtraction(IKnnExact knn, ILaplacianEigenmap eigenmap)
    {
        _knn      = knn;
        _eigenmap = eigenmap;
    }

    public Point4D[] Project(
        ReadOnlyMemory<double> embeddingMatrix,
        int                    vocabSize,
        int                    hiddenDim,
        int                    kNearest,
        ulong                  seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vocabSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hiddenDim);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(kNearest);
        if (embeddingMatrix.Length < (long)vocabSize * hiddenDim)
        {
            throw new ArgumentException(
                $"embeddingMatrix too small: have {embeddingMatrix.Length}, need {(long)vocabSize * hiddenDim}",
                nameof(embeddingMatrix));
        }

        // 1. Self k-NN cosine. Indices [vocabSize × kNearest], similarities ditto.
        var knn = _knn.SelfCosine(embeddingMatrix, vocabSize, hiddenDim, kNearest);

        // 2. Laplacian eigenmap → S³. Output is row-major [vocabSize × 4] doubles.
        const int OutputDim = 4;
        var rawFireflies = _eigenmap.EmbedToSphere(
            knn.Indices, knn.Similarities, vocabSize, kNearest, OutputDim);

        // 3. Wrap as Point4D records.
        var result = new Point4D[vocabSize];
        for (var row = 0; row < vocabSize; row++)
        {
            var off = row * OutputDim;
            result[row] = new Point4D(
                rawFireflies[off + 0],
                rawFireflies[off + 1],
                rawFireflies[off + 2],
                rawFireflies[off + 3]);
        }
        return result;

        // seed parameter is currently unused — Spectra's Lanczos initial
        // vector is internally fixed for determinism. Reserved for future
        // when we expose per-call seeding.
    }
}
