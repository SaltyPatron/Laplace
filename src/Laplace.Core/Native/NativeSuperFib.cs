namespace Laplace.Core.Native;

using System.Runtime.InteropServices;

internal static partial class NativeSuperFib
{
    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_super_fibonacci_4d")]
    internal static unsafe partial void At(int i, int total, double* outXyzw);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_super_fibonacci_4d_range")]
    internal static unsafe partial void Range(
        int startInclusive,
        int endExclusive,
        int total,
        double* outArray);
}
