using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// PostgreSQL's HASH partition law for 16-byte keys (engine/core partition_route.c):
/// the remainder of the leaf that owns each key under a table's modulus.
/// </summary>
public static class PartitionRoute
{
    public static void Remainders(ReadOnlySpan<Hash128> keys, int modulus, Span<int> remainders)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(modulus);
        if (remainders.Length < keys.Length)
            throw new ArgumentException("one remainder per key", nameof(remainders));
        if (keys.IsEmpty) return;
        NativeInterop.PgHashPartition16(
            MemoryMarshal.AsBytes(keys), (nuint)keys.Length, (uint)modulus,
            MemoryMarshal.Cast<int, uint>(remainders));
    }

    public static ulong HashBytesExtended(ReadOnlySpan<byte> key, ulong seed)
        => NativeInterop.PgHashBytesExtended(key, (nuint)key.Length, seed);
}

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "laplace_pg_hash_bytes_extended")]
    internal static partial ulong PgHashBytesExtended(ReadOnlySpan<byte> key, nuint len, ulong seed);

    [LibraryImport(Library, EntryPoint = "laplace_pg_hash_partition16")]
    internal static partial void PgHashPartition16(
        ReadOnlySpan<byte> keys, nuint n, uint modulus, Span<uint> remainders);
}
