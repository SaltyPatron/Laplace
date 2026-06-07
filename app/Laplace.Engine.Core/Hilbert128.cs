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

    public int CompareToBytewise(Hilbert128 other)
    {
        Hilbert128 a = this, b = other;
        return NativeInterop.Hilbert128Compare(&a, &b);
    }

    public void WriteBytes(Span<byte> dest)
    {
        if (dest.Length < 16) throw new ArgumentException("dest must hold 16 bytes", nameof(dest));
        Hilbert128 self = this;
        new ReadOnlySpan<byte>(&self, 16).CopyTo(dest);
    }
}
