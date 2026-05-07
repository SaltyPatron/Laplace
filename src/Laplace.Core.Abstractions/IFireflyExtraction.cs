namespace Laplace.Core.Abstractions;

using System;

/// <summary>
/// Project an AI model's embedding rows into S³ via Laplacian eigenmap.
/// Pipeline: exact symmetric k-NN cosine graph (MKL-tiled GEMM brute-force,
/// NEVER HNSW) → normalized Laplacian → leading-k eigenpairs via Spectra
/// (SymEigsSolver, Lanczos) → Gram-Schmidt orthonormalization → S³ projection
/// (4D unit-quaternion).
///
/// One firefly per (token row × ingested model). FIREFLIES ARE AI MODEL
/// EXTRACTION ARTIFACTS — they are NOT substrate primitives. Stored in the
/// firefly_s3_extracted physicality partition, separate from the
/// codepoint_s3_substrate partition that holds substrate atom positions.
/// Voronoi consensus over per-token firefly clouds emerges from cumulative
/// model ingestion.
/// </summary>
public interface IFireflyExtraction
{
    /// <summary>
    /// Project one model's embedding matrix to per-row S³ positions.
    /// </summary>
    /// <param name="embeddingMatrix">N × D row-major embedding tensor (already decoded to f64).</param>
    /// <param name="vocabSize">Number of rows (vocabulary size).</param>
    /// <param name="hiddenDim">Number of columns.</param>
    /// <param name="kNearest">k for the kNN affinity graph (typical 10-50).</param>
    /// <param name="seed">RNG seed for any Lanczos starting vector (deterministic).</param>
    /// <returns>One Point4D per vocabulary row, on S³.</returns>
    Point4D[] Project(
        ReadOnlyMemory<double> embeddingMatrix,
        int vocabSize,
        int hiddenDim,
        int kNearest,
        ulong seed);
}
