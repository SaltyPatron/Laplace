using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Laplace.Engine.Core;

/// <summary>
/// 16-byte BLAKE3-truncated content-addressable identifier — exact memory
/// layout match for <c>hash128_t {uint64_t hi, lo}</c> in the engine
/// (engine/core/include/laplace/core/hash128.h).
///
/// Used as substrate entity / physicality / attestation primary key and
/// throughout the IDecomposer / SubstrateChange / SubstrateCRUD surfaces.
/// Wire bytes match <c>bytea(16)</c> in PostgreSQL via Npgsql binary COPY —
/// no hex round-trip per STANDARDS.md ID discipline.
///
/// <c>readonly record struct</c> with <see cref="LayoutKind.Sequential"/>
/// produces a 16-byte blittable POD that crosses the C ABI with zero
/// marshalling — direct memory-equivalent passing for <c>[LibraryImport]</c>
/// signatures expecting <c>hash128_t*</c> or by-value <c>hash128_t</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 16)]
public readonly record struct Hash128(ulong Hi, ulong Lo)
{
    /// <summary>Zero hash. Useful as a "no value" sentinel and as the
    /// canonical empty-input hash for tests.</summary>
    public static readonly Hash128 Zero = new(0, 0);

    /// <summary>BLAKE3-128 of the given byte span. Wraps the engine
    /// <c>hash128_blake3</c>.</summary>
    public static Hash128 Blake3(ReadOnlySpan<byte> data)
    {
        Hash128 result;
        unsafe
        {
            fixed (byte* p = data)
            {
                NativeInterop.Hash128Blake3(p, (nuint)data.Length, &result);
            }
        }
        return result;
    }

    /// <summary>BLAKE3-128 of the UTF-8 encoding of <paramref name="canonical"/>.
    /// The conventional way to derive a substrate source / type / kind
    /// entity ID from a canonical name string (per ADR 0042 bootstrap
    /// + ADR 0051 IDecomposer.SourceId).</summary>
    public static Hash128 OfCanonical(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        // Stackalloc for short strings; heap for long. UTF-8 byte count is
        // bounded by 4 * char count — for substrate canonical names this is
        // always under a few hundred bytes.
        int maxBytes = Encoding.UTF8.GetMaxByteCount(canonical.Length);
        if (maxBytes <= 512)
        {
            Span<byte> buf = stackalloc byte[maxBytes];
            int n = Encoding.UTF8.GetBytes(canonical, buf);
            return Blake3(buf.Slice(0, n));
        }
        byte[] heap = Encoding.UTF8.GetBytes(canonical);
        return Blake3(heap);
    }

    /// <summary>Merkle composition: <c>BLAKE3(tier_byte ‖ child_id_bytes)</c>
    /// over child IDs in given order. Wraps the engine
    /// <c>hash128_merkle</c>.</summary>
    public static Hash128 Merkle(byte tier, ReadOnlySpan<Hash128> children)
    {
        Hash128 result;
        unsafe
        {
            fixed (Hash128* p = children)
            {
                NativeInterop.Hash128Merkle(tier, p, (nuint)children.Length, &result);
            }
        }
        return result;
    }

    /// <summary>Byte-lexicographic compare — matches both <c>memcmp(bytea, bytea)</c>
    /// on the wire and the engine <c>hash128_compare</c>.</summary>
    public int CompareToBytewise(Hash128 other)
    {
        unsafe
        {
            Hash128 a = this;
            return NativeInterop.Hash128Compare(&a, &other);
        }
    }

    /// <summary>True iff every byte equals — matches engine
    /// <c>hash128_equals</c>.</summary>
    public bool EqualsBytewise(Hash128 other)
    {
        unsafe
        {
            Hash128 a = this;
            return NativeInterop.Hash128Equals(&a, &other) != 0;
        }
    }

    /// <summary>Returns the 16 wire bytes in the same order the engine
    /// writes them. Use with Npgsql binary COPY or any other byte-oriented
    /// transport — never hex-encode.</summary>
    public byte[] ToBytes()
    {
        var b = new byte[16];
        WriteBytes(b);
        return b;
    }

    /// <summary>Writes 16 wire bytes into <paramref name="dest"/>; throws
    /// if dest is shorter than 16.</summary>
    public void WriteBytes(Span<byte> dest)
    {
        if (dest.Length < 16) throw new ArgumentException("dest must hold 16 bytes", nameof(dest));
        unsafe
        {
            Hash128 self = this;
            new ReadOnlySpan<byte>(&self, 16).CopyTo(dest);
        }
    }
}
