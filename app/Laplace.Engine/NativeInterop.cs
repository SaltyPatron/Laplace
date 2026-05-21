using System.Runtime.InteropServices;

namespace Laplace.Engine;

/// <summary>
/// P/Invoke bindings to liblaplace_engine. Placeholder — populated as the engine
/// C ABI grows in subsequent chunks per .agent/status/plan.md.
///
/// Per RULES.md R14: all engine-boundary types are POD; no exceptions cross
/// the ABI. C# wrappers translate engine error codes into managed exceptions.
/// </summary>
public static partial class NativeInterop
{
    // Library name resolved at runtime via DLL search:
    // - Linux: liblaplace_engine.so (typical /usr/local/lib or LD_LIBRARY_PATH)
    // - macOS: liblaplace_engine.dylib
    // - Windows: laplace_engine.dll
    private const string Library = "laplace_engine";

    /// <summary>Returns the Laplace engine version string (currently "0.1.0").</summary>
    [LibraryImport(Library, EntryPoint = "laplace_version", StringMarshalling = StringMarshalling.Utf8)]
    public static partial string LaplaceVersion();

    // TODO Chunk 1: coord4d_distance, coord4d_norm, coord4d_centroid
    // TODO Chunk 1: hash128_xxh3, hash128_merkle
    // TODO Chunk 1: hilbert4d_encode, hilbert4d_decode
    // TODO Chunk 1: mantissa_pack, mantissa_unpack
    // TODO Chunk 2: geometry4d_serialize, geometry4d_deserialize, geometry4d_frechet_discrete
    // TODO Chunk 3: codepoint_table_lookup
    // TODO Chunk 5: glicko2_update, glicko2_decay_rd_in_place
    // TODO Chunk 6: procrustes_fit, procrustes_apply
}
