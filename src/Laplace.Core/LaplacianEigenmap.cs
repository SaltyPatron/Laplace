namespace Laplace.Core;

using System;

using Laplace.Core.Abstractions;
using Laplace.Core.Native;

/// <summary>
/// Managed wrapper over the native B17 LaplacianEigenmapService. The native
/// implementation uses Eigen sparse matrices + Spectra symmetric eigensolver
/// + per-row L2 projection to S^(output_dim - 1).
///
/// Phase 2 / Track B / Service B17.
/// </summary>
public sealed class LaplacianEigenmap : ILaplacianEigenmap
{
    public double[] EmbedToSphere(
        ReadOnlyMemory<int>    knnIndices,
        ReadOnlyMemory<double> knnSimilarities,
        int                    n,
        int                    kNeighbors,
        int                    outputDimension)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(kNeighbors);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputDimension);
        if (outputDimension >= n) { throw new ArgumentOutOfRangeException(nameof(outputDimension), "outputDimension must be < n"); }

        var expected = (long)n * kNeighbors;
        if (knnIndices.Length      < expected) { throw new ArgumentException("knnIndices buffer smaller than n × kNeighbors",      nameof(knnIndices)); }
        if (knnSimilarities.Length < expected) { throw new ArgumentException("knnSimilarities buffer smaller than n × kNeighbors", nameof(knnSimilarities)); }

        var embedding = new double[(long)n * outputDimension];
        unsafe
        {
            using var iPin = knnIndices.Pin();
            using var sPin = knnSimilarities.Pin();
            fixed (double* outPtr = embedding)
            {
                var rc = NativeLaplacianEigenmap.EmbedToSphere(
                    (int*)iPin.Pointer,
                    (double*)sPin.Pointer,
                    n, kNeighbors, outputDimension,
                    outPtr);
                if (rc != 0) { throw new InvalidOperationException($"laplace_laplacian_eigenmap_s3_d returned {rc}"); }
            }
        }
        return embedding;
    }
}
