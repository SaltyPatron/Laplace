namespace Laplace.Core.Native;

using System.Runtime.InteropServices;

internal static partial class NativeQuaternion
{
    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_quaternion_multiply")]
    internal static partial void Multiply(in NativeS3.Point4D a, in NativeS3.Point4D b, out NativeS3.Point4D outP);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_quaternion_conjugate")]
    internal static partial void Conjugate(in NativeS3.Point4D q, out NativeS3.Point4D outP);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_quaternion_inverse")]
    internal static partial void Inverse(in NativeS3.Point4D q, out NativeS3.Point4D outP);
}
