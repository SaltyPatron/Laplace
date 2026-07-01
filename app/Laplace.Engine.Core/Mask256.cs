using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct Mask256(ulong W0, ulong W1, ulong W2, ulong W3)
{
    public static readonly Mask256 Zero = default;

    public bool IsZero => (W0 | W1 | W2 | W3) == 0;

    public static Mask256 operator |(Mask256 a, Mask256 b) =>
        new(a.W0 | b.W0, a.W1 | b.W1, a.W2 | b.W2, a.W3 | b.W3);

    public static Mask256 operator &(Mask256 a, Mask256 b) =>
        new(a.W0 & b.W0, a.W1 & b.W1, a.W2 & b.W2, a.W3 & b.W3);

    public bool Test(byte bit) =>
        ((bit >> 6) switch { 0 => W0, 1 => W1, 2 => W2, _ => W3 } & (1UL << (bit & 63))) != 0;

    public Mask256 Set(byte bit)
    {
        ulong shifted = 1UL << (bit & 63);
        return (bit >> 6) switch
        {
            0 => new(W0 | shifted, W1, W2, W3),
            1 => new(W0, W1 | shifted, W2, W3),
            2 => new(W0, W1, W2 | shifted, W3),
            _ => new(W0, W1, W2, W3 | shifted),
        };
    }

    public byte[] ToByteArray()
    {
        var buf = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0, 8),  W0);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8, 8),  W1);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(16, 8), W2);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(24, 8), W3);
        return buf;
    }

    public static Mask256 FromByteArray(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 32) throw new ArgumentException("Need 32 bytes", nameof(bytes));
        return new(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(0, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(16, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24, 8)));
    }
}
