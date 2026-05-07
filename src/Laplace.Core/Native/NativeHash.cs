namespace Laplace.Core.Native;

using System.Runtime.InteropServices;

internal static partial class NativeHash
{
    public const int HashBytes = 32;

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_hash_atom")]
    internal static unsafe partial void Atom(
        byte* content,
        nuint contentLen,
        byte* outHash);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_hash_composition")]
    internal static unsafe partial void Composition(
        byte* childHashes,
        int* rleCounts,
        nuint nChildren,
        byte* outHash);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_hash_edge")]
    internal static unsafe partial void Edge(
        byte* edgeTypeHash,
        byte* roleHashes,
        int* rolePositions,
        byte* participantHashes,
        nuint nMembers,
        byte* outHash);
}
