namespace Laplace.Core.Native;

using System.Runtime.InteropServices;

internal static partial class NativeHilbert
{
    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_hilbert_point4d_to_index")]
    internal static partial ulong PointToIndex(in NativeS3.Point4D p);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_hilbert_index_to_point4d")]
    internal static partial void IndexToPoint(ulong h, out NativeS3.Point4D outP);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_hilbert_xyzw_to_index")]
    internal static partial ulong XyzwToIndex(ushort x, ushort y, ushort z, ushort w);
}
