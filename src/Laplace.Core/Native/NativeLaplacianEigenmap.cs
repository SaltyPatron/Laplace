namespace Laplace.Core.Native;

using System.Runtime.InteropServices;

/// <summary>
/// P/Invoke surface for B17 LaplacianEigenmapService — symmetric normalized
/// graph Laplacian eigendecomposition via Spectra + per-row L2 projection
/// to S^(output_dim - 1). Used by F5 firefly extraction.
/// </summary>
internal static partial class NativeLaplacianEigenmap
{
    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_laplacian_eigenmap_s3_d")]
    internal static unsafe partial int EmbedToSphere(
        int*    knnIndices,
        double* knnSimilarities,
        int     n,
        int     kNeighbors,
        int     outputDimension,
        double* outEmbedding);
}
