namespace Laplace.Core.Native;

using System.Runtime.InteropServices;

internal static partial class NativeRle
{
    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_rle_encode_bytes")]
    internal static unsafe partial nuint EncodeBytes(
        byte* input,
        nuint inputLen,
        byte* outValues,
        int* outCounts);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_rle_decode_bytes")]
    internal static unsafe partial nuint DecodeBytes(
        byte* values,
        int* counts,
        nuint nRuns,
        byte* output,
        nuint outCapacity);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_rle_encode_hashes")]
    internal static unsafe partial nuint EncodeHashes(
        byte* inputHashes,
        nuint inputCount,
        byte* outHashes,
        int* outCounts);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_rle_decode_hashes")]
    internal static unsafe partial nuint DecodeHashes(
        byte* hashes,
        int* counts,
        nuint nRuns,
        byte* outHashes,
        nuint outCapacityCount);
}
