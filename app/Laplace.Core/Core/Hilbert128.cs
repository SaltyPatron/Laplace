using System.Runtime.InteropServices;
using Laplace.Native;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
public unsafe struct Hilbert128
{
    public fixed byte Bytes[16];

    public static Hilbert128 Encode(ReadOnlySpan<double> coord)
    {
        if (coord.Length < 4) throw new ArgumentException("coord must have at least 4 elements", nameof(coord));
        lp_coord c = default;
        fixed (double* p = coord)
            LaplaceNative.lp_coord_trunc(p, &c);
        ulong h = LaplaceNative.lp_hilbert4(&c);
        Hilbert128 result = default;
        for (int i = 0; i < 8; i++)
            result.Bytes[i] = (byte)(h >> ((7 - i) * 8));
        return result;
    }

    public void Decode(Span<double> outCoord)
    {
        if (outCoord.Length < 4) throw new ArgumentException("outCoord must have at least 4 elements", nameof(outCoord));
        ulong h = 0;
        for (int i = 0; i < 8; i++)
            h = (h << 8) | Bytes[i];
        uint* grid = stackalloc uint[4];
        LaplaceNative.lp_hilbert4_decode(h, grid);
        for (int d = 0; d < 4; d++)
            outCoord[d] = ((grid[d] + 0.5) / 65536.0) * 2.0 - 1.0;
    }

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
