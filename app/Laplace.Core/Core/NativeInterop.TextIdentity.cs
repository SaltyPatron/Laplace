using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "hash128_text")]
    internal static partial int Hash128Text(byte* bytes, nuint length, Hash128* result);
}
