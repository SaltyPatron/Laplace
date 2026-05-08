namespace Laplace.Smoke.Tests;

using Laplace.Core;

using Xunit;

/// <summary>
/// B15 KnnExactService property tests. Verifies the MKL GEMM-based exact
/// cosine KNN against hand-computed expected results on small inputs.
/// </summary>
public class KnnExactTests
{
    [Fact]
    public void ExactCosine_FourPointsInDimTwo_ReturnsExactNeighbors()
    {
        // Dictionary: 4 unit-circle points along the x-axis-aligned and
        // y-axis-aligned diagonals at angles 0°, 90°, 180°, 270°.
        var dict = new double[]
        {
             1.0,  0.0,   // 0°
             0.0,  1.0,   // 90°
            -1.0,  0.0,   // 180°
             0.0, -1.0,   // 270°
        };

        // Queries: a near-0° vector, a near-90° vector.
        var q = new double[]
        {
             0.99,  0.01,   // very close to dict[0] (0°)
             0.01,  0.99,   // very close to dict[1] (90°)
        };

        var knn = new KnnExact();
        var result = knn.ExactCosine(q, 2, dict, 4, 2, 3);

        // k=3 of n=4 means the top-3 by descending sim. Query 0 = (0.99, 0.01):
        //   sim with dict[0] (0°)   ≈ +0.99   ← best
        //   sim with dict[1] (90°)  ≈ +0.01
        //   sim with dict[3] (270°) ≈ -0.01
        //   sim with dict[2] (180°) ≈ -0.99   ← worst, excluded from top-3
        Assert.Equal(0, result.Indices[0]);
        Assert.True(result.Similarities[0] > 0.99);

        var q0Indices = new int[] { result.Indices[0], result.Indices[1], result.Indices[2] };
        Assert.DoesNotContain(2, q0Indices);   // antipodal not in top-3

        // Similarities monotonically non-increasing.
        Assert.True(result.Similarities[0] >= result.Similarities[1]);
        Assert.True(result.Similarities[1] >= result.Similarities[2]);
    }

    [Fact]
    public void ExactCosine_IdenticalDictAndQuery_FirstNeighborIsSelf()
    {
        var dict = new double[]
        {
            1.0, 0.0, 0.0,
            0.0, 1.0, 0.0,
            0.0, 0.0, 1.0,
        };
        var knn = new KnnExact();
        var result = knn.ExactCosine(dict, 3, dict, 3, 3, 1);

        // Each query's top-1 neighbor is itself (cosine sim = 1).
        Assert.Equal(0, result.Indices[0]);
        Assert.Equal(1, result.Indices[1]);
        Assert.Equal(2, result.Indices[2]);
        Assert.Equal(1.0, result.Similarities[0], precision: 12);
        Assert.Equal(1.0, result.Similarities[1], precision: 12);
        Assert.Equal(1.0, result.Similarities[2], precision: 12);
    }

    [Fact]
    public void SelfCosine_ExcludesDiagonal_NeverReturnsRowAsItsOwnNeighbor()
    {
        var dict = new double[]
        {
            1.0, 0.0,
            0.5, 0.5,
            0.0, 1.0,
        };
        var knn = new KnnExact();
        var result = knn.SelfCosine(dict, 3, 2, 2);

        // For each row i, check that i is NOT in its own neighbor list.
        for (var row = 0; row < 3; row++)
        {
            Assert.NotEqual(row, result.Indices[row * 2 + 0]);
            Assert.NotEqual(row, result.Indices[row * 2 + 1]);
        }
    }

    [Fact]
    public void ExactCosine_NormalizesInternally_NonUnitVectorsHandledCorrectly()
    {
        // Same direction, different magnitudes — cosine should be 1 regardless.
        var dict = new double[]
        {
            2.0, 0.0,    // raw norm = 2
            0.0, 5.0,    // raw norm = 5
        };
        var q = new double[] { 7.0, 0.0 };  // raw norm = 7, same direction as dict[0]

        var knn = new KnnExact();
        var result = knn.ExactCosine(q, 1, dict, 2, 2, 1);

        Assert.Equal(0, result.Indices[0]);
        Assert.Equal(1.0, result.Similarities[0], precision: 12);
    }

    [Fact]
    public void ExactCosine_KEqualsN_ReturnsAllNeighborsSorted()
    {
        var dict = new double[]
        {
            1.0, 0.0,
            0.7071067811865475, 0.7071067811865475,
            0.0, 1.0,
            -1.0, 0.0,
        };
        var q = new double[] { 1.0, 0.0 };

        var knn = new KnnExact();
        var result = knn.ExactCosine(q, 1, dict, 4, 2, 4);

        // Order by descending sim against q=(1,0):
        //   dict[0] sim = 1
        //   dict[1] sim = 0.707
        //   dict[2] sim = 0
        //   dict[3] sim = -1
        Assert.Equal(0, result.Indices[0]);
        Assert.Equal(1, result.Indices[1]);
        Assert.Equal(2, result.Indices[2]);
        Assert.Equal(3, result.Indices[3]);

        Assert.True(result.Similarities[0] > result.Similarities[1]);
        Assert.True(result.Similarities[1] > result.Similarities[2]);
        Assert.True(result.Similarities[2] > result.Similarities[3]);
    }
}
