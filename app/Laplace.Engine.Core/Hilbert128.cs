using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// 16-byte 4D Hilbert index — exact memory layout match for <c>hilbert128_t
/// {uint8_t bytes[16]}</c> in the engine (engine/core/include/laplace/core/hilbert4d.h).
///
/// Stored as <c>bytea(16)</c> in PostgreSQL via Npgsql binary COPY. Wire bytes
/// match byte-for-byte. Comparison is bytewise — same as
/// <c>memcmp(hilbert_index, hilbert_index)</c> on the wire and the engine
/// <c>hilbert128_compare</c>, so PG B-tree on <c>hilbert_index</c> orders
/// identically to engine-side comparisons.
/// </summary>
[StructLayout(LayoutTypeId.Sequential, Pack = 1, Size = 16)]
public unsafe struct Hilbert128
{
    /// <summary>The 16 raw bytes.</summary>
    public fixed byte Bytes[16];

    /// <summary>Hilbert-encode a 4D coord <c>[x, y, z, m]</c> via the engine
    /// Skilling-2004 implementation.</summary>
    public static Hilbert128 Encode(ReadOnlySpan<double> coord)
    {
        if (coord.Length < 4) throw new ArgumentException("coord must have at least 4 elements", nameof(coord));
        Hilbert128 result = default;
        fixed (double* p = coord)
        {
            NativeInterop.Hilbert4dEncode(p, &result);
        }
        return result;
    }

    /// <summary>Decode this Hilbert index back to a 4D coord — inverse of
    /// <see cref="Encode"/>.</summary>
    public void Decode(Span<double> outCoord)
    {
        if (outCoord.Length < 4) throw new ArgumentException("outCoord must have at least 4 elements", nameof(outCoord));
        Hilbert128 self = this;
        fixed (double* p = outCoord)
        {
            NativeInterop.Hilbert4dDecode(&self, p);
        }
    }

    /// <summary>Byte-lexicographic compare — matches the engine
    /// <c>hilbert128_compare</c>.</summary>
    public int CompareToBytewise(Hilbert128 other)
    {
        Hilbert128 a = this, b = other;
        return NativeInterop.Hilbert128Compare(&a, &b);
    }

    /// <summary>Copy the 16 wire bytes into <paramref name="dest"/>; throws
    /// if dest is shorter than 16.</summary>
    public void WriteBytes(Span<byte> dest)
    {
        if (dest.Length < 16) throw new ArgumentException("dest must hold 16 bytes", nameof(dest));
        Hilbert128 self = this;
        new ReadOnlySpan<byte>(&self, 16).CopyTo(dest);
    }
}
