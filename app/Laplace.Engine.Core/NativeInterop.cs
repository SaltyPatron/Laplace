using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// P/Invoke bindings to liblaplace_core (per ADR 0024 + 0026).
///
/// Per RULES.md R14: engine-boundary types are POD; no exceptions cross
/// the C ABI. Per RULES.md R22: this project does NOT define parallel
/// C# types for liblwgeom's POINT4D — geometry round-trips PG ↔ C# via
/// Npgsql.NetTopologySuite (NTS Coordinate with Ordinates.XYZM).
///
/// Real bindings (math4d, hash128, hilbert4d, mantissa, glicko2,
/// codepoint_table, astar, trajectory) land per-Chunk. For now the
/// single binding to laplace_core_version() proves the .so loads and
/// the P/Invoke surface works.
/// </summary>
public static partial class NativeInterop
{
    // Linux: liblaplace_core.so   macOS: liblaplace_core.dylib   Windows: laplace_core.dll
    private const string Library = "laplace_core";

    /// <summary>Returns the liblaplace_core version string.</summary>
    // Returns IntPtr (not string) because the C side returns a pointer to a
    // .rodata string literal. `string` + StringMarshalling.Utf8 would make
    // the source-generated marshaller call NativeMemory.Free() on the
    // returned pointer after copy — crashing with `free(): invalid pointer`
    // on the .rodata pointer. PtrToStringUTF8 copies without freeing.
    [LibraryImport(Library, EntryPoint = "laplace_core_version")]
    private static partial IntPtr LaplaceCoreVersionPtr();

    public static string LaplaceCoreVersion() =>
        Marshal.PtrToStringUTF8(LaplaceCoreVersionPtr()) ?? string.Empty;

    // TODO Chunk 1: math4d_distance, math4d_norm, math4d_centroid (operate on double[4])
    // TODO Chunk 1: hash128_blake3, hash128_merkle, hash128_compare
    //               (16-byte buffer marshalled as byte[] or [StructLayout(Sequential)] Hash128)
    // TODO Chunk 1: hilbert4d_encode, hilbert4d_decode (byte[16] in/out)
    // TODO Chunk 1: mantissa_pack, mantissa_unpack
    // TODO Chunk 3: codepoint_table_lookup (returns IntPtr → 64-byte struct)
    // TODO Chunk 5: glicko2_init, glicko2_update, glicko2_decay_rd_in_place
    // TODO Chunk 5: astar_open / astar_next / astar_close — opaque handle iteration
}
