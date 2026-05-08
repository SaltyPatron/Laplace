namespace Laplace.Decomposers.Model.OperatorShapes;

using System;

using Laplace.Core.Abstractions;

/// <summary>
/// Project a weight matrix (e.g., W_Q / W_K / W_V / W_O / W_up / W_down) to
/// a LINESTRING4D operator shape for storage in the model_weights_4d
/// physicality partition. Each row of the matrix becomes one POINT4D vertex
/// via IFireflyExtraction's Laplacian eigenmap projection (exact KNN cosine
/// graph + symmetric normalized Laplacian + leading-4 eigenpairs +
/// Gram-Schmidt + S³ projection). The LINESTRING is the row-ordered
/// sequence — preserving row order matters because matmul applies rows
/// sequentially.
///
/// ST_FrechetDistance4D between two such LINESTRINGs detects mechanistic
/// circuit similarity across models (Llama L3H7's W_Q vs Qwen L5H2's W_Q
/// → Frechet-near means equivalent attention head function), enabling
/// cross-model circuit discovery, surgical head replacement, model
/// archaeology, and the geometric matmul replacement at re-export time.
/// ST_VertexCentroid4D gives the operator's "summary position" for fast
/// spatial bucketing via GiST + Hilbert clustering on the centroid.
///
/// Phase 4 / Track F5 / supports model_weights_4d emission per #38
/// per-tensor extractors.
/// </summary>
public sealed class MatrixToLineString4D
{
    private const int   DefaultK    = 20;
    private const ulong DefaultSeed = 0UL;

    private readonly IFireflyExtraction _firefly;

    public MatrixToLineString4D(IFireflyExtraction firefly)
    {
        _firefly = firefly ?? throw new ArgumentNullException(nameof(firefly));
    }

    /// <summary>
    /// Project a row-major [<paramref name="rowCount"/> × <paramref name="columnCount"/>]
    /// matrix to a LINESTRING4D. Returns one POINT4D per row in row order.
    /// kNearest defaults to min(20, rowCount - 1). seed is deterministic.
    /// </summary>
    public Point4D[] Project(
        ReadOnlyMemory<double> matrix,
        int                    rowCount,
        int                    columnCount,
        int                    kNearest = DefaultK,
        ulong                  seed     = DefaultSeed)
    {
        if (rowCount < 2)
        {
            return new[] { new Point4D(0d, 0d, 0d, 1d) };
        }
        var k = Math.Min(kNearest, rowCount - 1);
        return _firefly.Project(matrix, rowCount, columnCount, k, seed);
    }
}
