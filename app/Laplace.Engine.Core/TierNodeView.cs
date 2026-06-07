using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct TierNodeView
{
    public byte Tier;

    private fixed byte _pad[3];

    public uint ParentIdx;

    public uint FirstChildIdx;

    public uint ChildCount;

    public uint TextRangeOff;

    public uint TextRangeLen;

    public uint Atom;

    private uint _pad2;

    public Hash128 Id;

    public fixed double Coord[4];

    public Hilbert128 Hilbert;
}
