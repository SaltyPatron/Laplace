namespace Laplace.Core.Native;

using System.Runtime.InteropServices;

/// <summary>
/// P/Invoke surface for B15 KnnExactService. Two entry points: query × dict
/// brute-force cosine top-k, and self × self with diagonal exclusion (used
/// by B17 LaplacianEigenmap to build sparse Laplacians).
/// </summary>
internal static partial class NativeKnnExact
{
    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_knn_exact_cosine_d")]
    internal static unsafe partial int ExactCosine(
        double* queries,
        int     nQueries,
        double* dictionary,
        int     nDict,
        int     dim,
        int     k,
        int*    outIndices,
        double* outSimilarities);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_knn_self_cosine_d")]
    internal static unsafe partial int SelfCosine(
        double* dictionary,
        int     nDict,
        int     dim,
        int     k,
        int*    outIndices,
        double* outSimilarities);
}
