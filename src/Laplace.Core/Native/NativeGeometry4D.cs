namespace Laplace.Core.Native;

using System.Runtime.InteropServices;

internal static partial class NativeGeometry4D
{
    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_point4d_distance")]
    internal static partial double Distance(in NativeS3.Point4D a, in NativeS3.Point4D b);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_point4d_dot")]
    internal static partial double Dot(in NativeS3.Point4D a, in NativeS3.Point4D b);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_point4d_norm")]
    internal static partial double Norm(in NativeS3.Point4D a);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_point4d_vertex_centroid")]
    internal static unsafe partial void VertexCentroid(
        NativeS3.Point4D* points,
        nuint nPoints,
        out NativeS3.Point4D outP);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_frechet_distance_4d")]
    internal static unsafe partial double FrechetDistance(
        NativeS3.Point4D* p, nuint np,
        NativeS3.Point4D* q, nuint nq);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_hausdorff_distance_4d")]
    internal static unsafe partial double HausdorffDistance(
        NativeS3.Point4D* p, nuint np,
        NativeS3.Point4D* q, nuint nq);
}
