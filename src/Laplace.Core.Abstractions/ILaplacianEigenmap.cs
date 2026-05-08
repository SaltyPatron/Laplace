namespace Laplace.Core.Abstractions;

using System;

/// <summary>
/// Symmetric normalized Laplacian eigenmap → S³ projection. Per the
/// synthesis doc + CLAUDE.md invariant 8: AI model fireflies are extracted
/// by computing leading eigenvectors of the symmetric normalized graph
/// Laplacian over a k-NN cosine-similarity graph among embedding rows,
/// then projecting each row to the unit sphere.
///
/// Phase 2 / Track B / Service B17. Backed by Eigen::SparseMatrix +
/// Spectra::SymEigsSolver in the native implementation.
/// </summary>
public interface ILaplacianEigenmap
{
    /// <summary>
    /// Compute the eigenmap embedding from a precomputed KNN graph and
    /// return per-row unit vectors on the (output_dim - 1)-sphere.
    /// </summary>
    /// <param name="knnIndices">n × kNeighbors row-major neighbor indices.</param>
    /// <param name="knnSimilarities">n × kNeighbors row-major cosine similarities.</param>
    /// <param name="n">Number of nodes in the KNN graph.</param>
    /// <param name="kNeighbors">Per-node degree of the KNN graph.</param>
    /// <param name="outputDimension">Target embedding dimension (4 for S^3).</param>
    /// <returns>n × outputDimension row-major matrix; each row has L2 norm = 1.</returns>
    double[] EmbedToSphere(
        ReadOnlyMemory<int>    knnIndices,
        ReadOnlyMemory<double> knnSimilarities,
        int                    n,
        int                    kNeighbors,
        int                    outputDimension);
}
