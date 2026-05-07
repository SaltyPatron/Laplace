namespace Laplace.Core.Native;

using System.Runtime.InteropServices;

internal static partial class NativeS3
{
    /// <summary>Native (x, y, z, w) layout matching <c>laplace_point4d_t</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point4D
    {
        public double X;
        public double Y;
        public double Z;
        public double W;
    }

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_s3_is_on_sphere")]
    internal static partial int IsOnSphere(in Point4D p, double tol);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_s3_normalize")]
    internal static partial void Normalize(in Point4D p, out Point4D outP);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_s3_geodesic_distance")]
    internal static partial double GeodesicDistance(in Point4D a, in Point4D b);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_s3_slerp")]
    internal static partial void Slerp(in Point4D a, in Point4D b, double t, out Point4D outP);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_s3_eigenvalue_centroid")]
    internal static unsafe partial void EigenvalueCentroid(
        Point4D* points,
        double* weights,
        nuint nPoints,
        out Point4D outP);
}
