using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Mirror of the engine POD <c>tier_node_view_t</c>
/// (engine/core/include/laplace/core/tier_tree.h). Returned by
/// <see cref="TierTree.GetNode"/>.
///
/// Field order + padding match the C struct exactly (Pack = 1 layout with
/// explicit padding fields). The engine writes one of these per node into a
/// caller-supplied output buffer; the C# struct accepts that directly via
/// ref param.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct TierNodeView
{
    /// <summary>0 = atom (codepoint / pixel / ...), 1+ = composed.</summary>
    public byte Tier;

    /// <summary>Explicit padding to match engine struct alignment.</summary>
    private fixed byte _pad[3];

    /// <summary>Parent index; <see cref="TierTree.Invalid"/> for the root.</summary>
    public uint ParentIdx;

    /// <summary>First-child index; <see cref="TierTree.Invalid"/> if this is a leaf.</summary>
    public uint FirstChildIdx;

    /// <summary>Number of contiguous children at <see cref="FirstChildIdx"/>.</summary>
    public uint ChildCount;

    /// <summary>Byte offset into the source content the node was decomposed from.</summary>
    public uint TextRangeOff;

    /// <summary>Byte length of the source range.</summary>
    public uint TextRangeLen;

    /// <summary>Leaf-only: the atom value (codepoint for text, etc.).</summary>
    public uint Atom;

    /// <summary>Explicit padding to 8-byte alignment for <see cref="Id"/>.</summary>
    private uint _pad2;

    /// <summary>BLAKE3-128 identifier. Zero pre-compose; populated by
    /// <see cref="HashComposer"/>.</summary>
    public Hash128 Id;

    /// <summary>XYZM 4D coord. Zero pre-compose; populated by
    /// <see cref="HashComposer"/>.</summary>
    public fixed double Coord[4];

    /// <summary>4D Hilbert index. Zero pre-compose; populated by
    /// <see cref="HashComposer"/>.</summary>
    public Hilbert128 Hilbert;
}
