using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>Native's immutable 64-byte highway record: canonical identity,
/// fixed coordinates, spatial key, list slot and recursive DAG height.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public readonly struct HighwayRecord
{
    [FieldOffset(0)] public readonly Hash128 Hash;
    [FieldOffset(16)] public readonly long FixedX;
    [FieldOffset(24)] public readonly long FixedY;
    [FieldOffset(32)] public readonly long FixedZ;
    [FieldOffset(40)] public readonly long FixedM;
    [FieldOffset(48)] public readonly ulong NativeHilbert;
    [FieldOffset(56)] public readonly uint Slot;
    [FieldOffset(60)] public readonly uint Tier;
    public double CoordX => Math.ScaleB(FixedX, -53);
    public double CoordY => Math.ScaleB(FixedY, -53);
    public double CoordZ => Math.ScaleB(FixedZ, -53);
    public double CoordM => Math.ScaleB(FixedM, -53);
}
