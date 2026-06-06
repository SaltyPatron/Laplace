using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Exact memory-layout mirror of <c>laplace_perfcache_record_t</c>
/// (engine/core/include/laplace/core/perfcache_format.h) — the 80-byte T0
/// codepoint perf-cache record. Read directly from the mmap'd blob the
/// engine loads (<see cref="CodepointPerfcache"/>); zero marshalling.
///
/// <para>
/// For the T0 DB seed: <see cref="Hash"/> IS the codepoint's substrate
/// entity id (BLAKE3-128 of its UTF-8 bytes), and
/// <see cref="CoordX"/>..<see cref="CoordM"/> + <see cref="Hilbert"/> are the
/// substrate-canonical CONTENT physicality for that codepoint (coords placed
/// by super-Fibonacci over DUCET collation rank).
/// </para>
///
/// Explicit offsets match the C struct field-by-field so a layout drift on
/// either side is a compile/contract break, not silent corruption (the C
/// side is guarded by <c>_Static_assert(sizeof == 80)</c>).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public readonly struct CodepointRecord
{
    [FieldOffset(0)]  public readonly uint Codepoint;
    [FieldOffset(4)]  public readonly uint UcaOrder;
    [FieldOffset(8)]  public readonly double CoordX;
    [FieldOffset(16)] public readonly double CoordY;
    [FieldOffset(24)] public readonly double CoordZ;
    [FieldOffset(32)] public readonly double CoordM;
    [FieldOffset(40)] public readonly Hilbert128 Hilbert;
    [FieldOffset(56)] public readonly Hash128 Hash;
    [FieldOffset(72)] public readonly uint Flags;
    [FieldOffset(76)] public readonly uint Pad;

    // === packed flag accessors (mirror laplace_pc_* in perfcache_format.h) ===
    public byte GraphemeBreak     => (byte)((Flags & 0x0000000Fu) >> 0);
    public byte WordBreak         => (byte)((Flags & 0x000001F0u) >> 4);
    public byte SentenceBreak     => (byte)((Flags & 0x00001E00u) >> 9);
    public byte IndicConjunctBreak => (byte)((Flags & 0x00006000u) >> 13);
    public byte CombiningClass    => (byte)((Flags & 0x007F8000u) >> 15);
}
