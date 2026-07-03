using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Explicit, Size = 80)]
public readonly struct CodepointRecord
{
    [FieldOffset(0)] public readonly uint Codepoint;
    [FieldOffset(4)] public readonly uint UcaOrder;
    [FieldOffset(8)] public readonly double CoordX;
    [FieldOffset(16)] public readonly double CoordY;
    [FieldOffset(24)] public readonly double CoordZ;
    [FieldOffset(32)] public readonly double CoordM;
    [FieldOffset(40)] public readonly Hilbert128 Hilbert;
    [FieldOffset(56)] public readonly Hash128 Hash;
    [FieldOffset(72)] public readonly uint Flags;
    [FieldOffset(76)] public readonly uint Pad;

    public byte GraphemeBreak => (byte)((Flags & 0x0000000Fu) >> 0);
    public byte WordBreak => (byte)((Flags & 0x000001F0u) >> 4);
    public byte SentenceBreak => (byte)((Flags & 0x00001E00u) >> 9);
    public byte IndicConjunctBreak => (byte)((Flags & 0x00006000u) >> 13);
    public byte CombiningClass => (byte)((Flags & 0x007F8000u) >> 15);
}
