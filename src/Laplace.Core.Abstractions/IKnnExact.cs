namespace Laplace.Core.Abstractions;

using System;

/// <summary>
/// Exact KNN over double-precision dense matrices via cosine similarity.
/// Per CLAUDE.md banned-patterns: NO HNSW, NO approximate KNN — substrate
/// inference uses brute-force GEMM-based exact KNN, period.
///
/// Phase 2 / Track B / Service B15. Backed by Intel oneMKL cblas_dgemm in
/// the native implementation.
/// </summary>
public interface IKnnExact
{
    /// <summary>
    /// For each query row, return the indices and cosine similarities of
    /// its k nearest neighbors in the dictionary.
    /// </summary>
    /// <param name="queries">n_queries × dim row-major matrix.</param>
    /// <param name="dictionary">n_dict × dim row-major matrix.</param>
    /// <param name="k">Number of nearest neighbors per query (1..n_dict).</param>
    /// <returns>(indices, similarities) — both n_queries × k row-major.</returns>
    KnnResult ExactCosine(
        ReadOnlyMemory<double> queries,    int nQueries,
        ReadOnlyMemory<double> dictionary, int nDict,
        int                    dimension,
        int                    k);

    /// <summary>
    /// Self-similarity KNN: dictionary × dictionary with i==j edges
    /// excluded. Used to build sparse Laplacians for B17 LaplacianEigenmap.
    /// </summary>
    KnnResult SelfCosine(
        ReadOnlyMemory<double> dictionary, int nDict,
        int                    dimension,
        int                    k);
}

/// <summary>
/// (Indices, Similarities) result of an exact-cosine KNN call. Both
/// arrays are n × k row-major where n is the query count.
/// </summary>
public sealed record KnnResult(int[] Indices, double[] Similarities);
