namespace Laplace.Core.Native;

/// <summary>
/// Single point of truth for the native library name. Loaded by every
/// <c>[LibraryImport]</c> declaration in this namespace. CMake produces
/// <c>laplace_native.dll</c> on Windows and <c>liblaplace_native.so</c> on
/// Linux; .NET's library resolver applies the platform prefix/suffix.
///
/// Phase 2 / Track D / D2 (managed P/Invoke wrappers).
/// </summary>
internal static class NativeLibrary
{
    public const string Name = "laplace_native";
}
