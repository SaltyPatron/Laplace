using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Laplace.Engine.Core;

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "laplace_content_word_segment")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ContentWordSegment(byte* bytes, nuint length,
        delegate* unmanaged[Cdecl]<void*, uint, byte*, uint, Hash128*, void> emit, void* context);

    [LibraryImport(Library, EntryPoint = "hash128_text")]
    internal static partial int Hash128Text(byte* bytes, nuint length, Hash128* result);
}
