using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 16)]
public readonly record struct Hash128(ulong Hi, ulong Lo)
{
    public static readonly Hash128 Zero = new(0, 0);

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

    public static Hash128 OfCanonical(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
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

    public int CompareToBytewise(Hash128 other)
    {
        unsafe
        {
            Hash128 a = this;
            return NativeInterop.Hash128Compare(&a, &other);
        }
    }

    public bool EqualsBytewise(Hash128 other)
    {
        unsafe
        {
            Hash128 a = this;
            return NativeInterop.Hash128Equals(&a, &other) != 0;
        }
    }

    public byte[] ToBytes()
    {
        var b = new byte[16];
        WriteBytes(b);
        return b;
    }

    // Inverse of WriteBytes: reconstruct from the 16-byte raw layout (e.g. a Postgres bytea id).
    public static Hash128 FromBytes(ReadOnlySpan<byte> src)
    {
        if (src.Length < 16) throw new ArgumentException("src must hold 16 bytes", nameof(src));
        return MemoryMarshal.Read<Hash128>(src);
    }

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
