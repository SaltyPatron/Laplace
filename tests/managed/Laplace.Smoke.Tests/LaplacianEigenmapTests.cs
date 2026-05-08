namespace Laplace.Smoke.Tests;

using System;

using Laplace.Core;

using Xunit;

/// <summary>
/// B17 LaplacianEigenmapService property tests. Verifies the Spectra-backed
/// symmetric eigensolver + S^3 projection on a synthetic two-cluster KNN
/// graph: same-cluster pairs end up close in the 4D embedding; cross-cluster
/// pairs are far. Every output row is on S^3 (norm = 1).
/// </summary>
public class LaplacianEigenmapTests
{
    private static readonly int[]    SinglePointIndices  = { 0 };
    private static readonly double[] SinglePointSims     = { 0.5 };
    private static readonly int[]    TwoNodeIndices      = { 1, 0 };
    private static readonly double[] TwoNodeSims         = { 1.0, 1.0 };

    [Fact]
    public void TwoClusterKnnGraph_EmbedsClustersToSeparateRegionsOnS3()
    {
        // Six nodes split into two tight clusters of 3.
        // Cluster A = {0, 1, 2}, Cluster B = {3, 4, 5}.
        // KNN graph: each node lists its 2 cluster-mates as neighbors with
        // high similarity. (Cross-cluster edges absent — graph is two
        // connected components weighted by within-cluster cosine sim.)
        const int n = 6;
        const int k = 2;
        var indices = new int[]
        {
            1, 2,    // node 0 → cluster A neighbors
            0, 2,
            0, 1,
            4, 5,    // node 3 → cluster B neighbors
            3, 5,
            3, 4,
        };
        var sims = new double[]
        {
            0.95, 0.92,
            0.95, 0.94,
            0.92, 0.94,
            0.93, 0.96,
            0.93, 0.91,
            0.96, 0.91,
        };

        var eigenmap = new LaplacianEigenmap();
        var embedding = eigenmap.EmbedToSphere(indices, sims, n, k, outputDimension: 4);

        Assert.Equal(n * 4, embedding.Length);

        // Every row is on S^3 (norm = 1 within float epsilon).
        for (var i = 0; i < n; i++)
        {
            var s = embedding[i * 4 + 0] * embedding[i * 4 + 0]
                  + embedding[i * 4 + 1] * embedding[i * 4 + 1]
                  + embedding[i * 4 + 2] * embedding[i * 4 + 2]
                  + embedding[i * 4 + 3] * embedding[i * 4 + 3];
            Assert.InRange(System.Math.Sqrt(s), 1.0 - 1e-9, 1.0 + 1e-9);
        }

        // Within-cluster cosine on S^3 should be HIGHER than cross-cluster.
        // Pick representative pairs:
        //   within: (0,1), (3,4)
        //   cross:  (0,3), (1,4), (2,5)
        var within01 = Dot4(embedding, 0, 1);
        var within34 = Dot4(embedding, 3, 4);
        var cross03 = Dot4(embedding, 0, 3);
        var cross14 = Dot4(embedding, 1, 4);
        var cross25 = Dot4(embedding, 2, 5);

        Assert.True(within01 > cross03, $"within (0,1)={within01:F4} should be > cross (0,3)={cross03:F4}");
        Assert.True(within01 > cross14, $"within (0,1)={within01:F4} should be > cross (1,4)={cross14:F4}");
        Assert.True(within34 > cross03, $"within (3,4)={within34:F4} should be > cross (0,3)={cross03:F4}");
        Assert.True(within34 > cross25, $"within (3,4)={within34:F4} should be > cross (2,5)={cross25:F4}");
    }

    [Fact]
    public void SinglePoint_NotEnoughNodes_ThrowsArgumentOutOfRange()
    {
        var eigenmap = new LaplacianEigenmap();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            eigenmap.EmbedToSphere(SinglePointIndices, SinglePointSims, n: 1, kNeighbors: 1, outputDimension: 4));
    }

    [Fact]
    public void OutputDimensionTooLarge_ThrowsArgumentOutOfRange()
    {
        var eigenmap = new LaplacianEigenmap();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            eigenmap.EmbedToSphere(TwoNodeIndices, TwoNodeSims, n: 2, kNeighbors: 1, outputDimension: 4));
    }

    private static double Dot4(double[] e, int i, int j)
    {
        return e[i * 4 + 0] * e[j * 4 + 0]
             + e[i * 4 + 1] * e[j * 4 + 1]
             + e[i * 4 + 2] * e[j * 4 + 2]
             + e[i * 4 + 3] * e[j * 4 + 3];
    }
}
