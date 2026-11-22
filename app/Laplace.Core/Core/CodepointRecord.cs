using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Explicit, Size = 64)]
public readonly struct CodepointRecord
{
    [FieldOffset(0)] public readonly Hash128 Hash;
    [FieldOffset(16)] public readonly long FixedX;
    [FieldOffset(24)] public readonly long FixedY;
    [FieldOffset(32)] public readonly long FixedZ;
    [FieldOffset(40)] public readonly long FixedM;
    [FieldOffset(48)] public readonly ulong NativeHilbert;
    [FieldOffset(56)] public readonly uint UcaOrder;
    [FieldOffset(60)] public readonly uint Pad;

    public double CoordX => Math.ScaleB(FixedX, -53);
    public double CoordY => Math.ScaleB(FixedY, -53);
    public double CoordZ => Math.ScaleB(FixedZ, -53);
    public double CoordM => Math.ScaleB(FixedM, -53);

    public Hilbert128 Hilbert => Hilbert128.Encode(stackalloc double[] { CoordX, CoordY, CoordZ, CoordM });

    public uint Codepoint =>
        CodepointPerfcache.TryLookupCodepoint(Hash, out var cp)
            ? cp
            : throw new InvalidOperationException("The record is not in the active Native scalar floor.");

    public uint Flags => CodepointPerfcache.GetFlagsWord(Codepoint);
    public byte GraphemeBreak => (byte)((Flags & 0x0000000Fu) >> 0);
    public byte WordBreak => (byte)((Flags & 0x000001F0u) >> 4);
    public byte SentenceBreak => (byte)((Flags & 0x00001E00u) >> 9);
    public byte IndicConjunctBreak => (byte)((Flags & 0x00006000u) >> 13);
    public byte CombiningClass => (byte)((Flags & 0x007F8000u) >> 15);
}
