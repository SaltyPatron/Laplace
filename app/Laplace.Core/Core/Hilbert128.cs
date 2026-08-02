using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
public unsafe struct Hilbert128
{
    public fixed byte Bytes[16];

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

    public void Decode(Span<double> outCoord)
    {
        if (outCoord.Length < 4) throw new ArgumentException("outCoord must have at least 4 elements", nameof(outCoord));
        Hilbert128 self = this;
        fixed (double* p = outCoord)
        {
            NativeInterop.Hilbert4dDecode(&self, p);
        }
    }

    /// <summary>memcmp of the 16 packed bytes — managed, no P/Invoke per compare.</summary>
    public int CompareToBytewise(Hilbert128 other)
    {
        for (int i = 0; i < 16; i++)
        {
            int d = Bytes[i] - other.Bytes[i];
            if (d != 0) return d;
        }
        return 0;
    }

    public void WriteBytes(Span<byte> dest)
    {
        if (dest.Length < 16) throw new ArgumentException("dest must hold 16 bytes", nameof(dest));
        Hilbert128 self = this;
        new ReadOnlySpan<byte>(&self, 16).CopyTo(dest);
    }

    /// <summary>
    /// Load the packed 128-bit 1D Hilbert index (S³ locality order) from its
    /// 16-byte big-endian wire / <c>bytea</c> form. Not a 64-bit integer.
    /// </summary>
    public static Hilbert128 FromBytes(ReadOnlySpan<byte> src)
    {
        if (src.Length != 16)
            throw new ArgumentException("hilbert_index is a 128-bit value (16 bytes)", nameof(src));
        Hilbert128 result = default;
        src.CopyTo(new Span<byte>(&result, 16));
        return result;
    }

    public byte[] ToByteArray()
    {
        var bytes = new byte[16];
        WriteBytes(bytes);
        return bytes;
    }
}
