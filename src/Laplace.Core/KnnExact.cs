namespace Laplace.Core;

using System;

using Laplace.Core.Abstractions;
using Laplace.Core.Native;

/// <summary>
/// Managed wrapper over the native B15 KnnExactService. Brute-force GEMM-
/// based exact cosine KNN — no HNSW, no approximate methods, per CLAUDE.md
/// banned patterns.
///
/// Phase 2 / Track B / Service B15.
/// </summary>
public sealed class KnnExact : IKnnExact
{
    public KnnResult ExactCosine(
        ReadOnlyMemory<double> queries,    int nQueries,
        ReadOnlyMemory<double> dictionary, int nDict,
        int                    dimension,
        int                    k)
    {
        ValidateArgs(nQueries, nDict, dimension, k);
        if (queries.Length    < (long)nQueries * dimension) { throw new ArgumentException("queries buffer smaller than nQueries × dimension", nameof(queries)); }
        if (dictionary.Length < (long)nDict    * dimension) { throw new ArgumentException("dictionary buffer smaller than nDict × dimension", nameof(dictionary)); }

        var indices      = new int[(long)nQueries * k];
        var similarities = new double[(long)nQueries * k];

        unsafe
        {
            using var qPin = queries.Pin();
            using var dPin = dictionary.Pin();
            fixed (int*    iPtr = indices)
            fixed (double* sPtr = similarities)
            {
                var rc = NativeKnnExact.ExactCosine(
                    (double*)qPin.Pointer, nQueries,
                    (double*)dPin.Pointer, nDict,
                    dimension, k,
                    iPtr, sPtr);
                if (rc != 0) { throw new InvalidOperationException($"laplace_knn_exact_cosine_d returned {rc}"); }
            }
        }
        return new KnnResult(indices, similarities);
    }

    public KnnResult SelfCosine(
        ReadOnlyMemory<double> dictionary, int nDict,
        int                    dimension,
        int                    k)
    {
        ValidateArgs(nDict, nDict, dimension, k);
        if (dictionary.Length < (long)nDict * dimension) { throw new ArgumentException("dictionary buffer smaller than nDict × dimension", nameof(dictionary)); }

        var indices      = new int[(long)nDict * k];
        var similarities = new double[(long)nDict * k];

        unsafe
        {
            using var dPin = dictionary.Pin();
            fixed (int*    iPtr = indices)
            fixed (double* sPtr = similarities)
            {
                var rc = NativeKnnExact.SelfCosine(
                    (double*)dPin.Pointer, nDict, dimension, k,
                    iPtr, sPtr);
                if (rc != 0) { throw new InvalidOperationException($"laplace_knn_self_cosine_d returned {rc}"); }
            }
        }
        return new KnnResult(indices, similarities);
    }

    private static void ValidateArgs(int nQueries, int nDict, int dimension, int k)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nQueries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nDict);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        if (k > nDict) { throw new ArgumentOutOfRangeException(nameof(k), "k must be ≤ nDict"); }
    }
}
