namespace Laplace.Core.Native;

using System;
using System.Runtime.InteropServices;

/// <summary>
/// P/Invoke surface for B19 TensorDecodeService. Native parser reads
/// the safetensors header (8-byte LE uint64 length + JSON header + raw
/// data section), exposes per-tensor metadata, and serves as the source
/// of file offsets for managed-side data streaming.
/// </summary>
internal static partial class NativeSafetensors
{
    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_safetensors_open",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr Open(string path);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_safetensors_close")]
    internal static partial void Close(IntPtr handle);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_safetensors_entry_count")]
    internal static partial nuint EntryCount(IntPtr handle);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_safetensors_entry")]
    internal static partial IntPtr EntryAt(IntPtr handle, nuint index);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_safetensors_find",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr Find(IntPtr handle, string name);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_safetensors_data_section_offset")]
    internal static partial ulong DataSectionOffset(IntPtr handle);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_dtype_byte_width")]
    internal static partial nuint DtypeByteWidth(int dtype);

    /// <summary>
    /// Native struct layout matching laplace_tensor_entry_t in safetensors.h.
    /// name[256] + dtype + rank + shape[8] + data_offset + data_byte_length.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct EntryNative
    {
        public const int MaxNameBytes = 256;
        public const int MaxRank = 8;

        public fixed byte name[MaxNameBytes];
        public int        dtype;
        public int        rank;
        public fixed long shape[MaxRank];
        public ulong      data_offset;
        public ulong      data_byte_length;
    }
}
