namespace Laplace.Core.Abstractions;

/// <summary>
/// HilbertCurveService — 4D Skilling 2003 space-filling curve. Maps a
/// quantized 4D point (each axis in [-1, +1]) to a 64-bit Hilbert index
/// preserving locality. Used by physicality.hilbert_index for B-tree
/// locality probes complementing the 4D GiST/SP-GiST indexes, and at
/// generator time when emitting the compiled codepoint table.
/// </summary>
public interface IHilbertCurve
{
    /// <summary>64-bit Hilbert index for an S³ POINT4D.</summary>
    ulong Index(Point4D point);

    /// <summary>Inverse: 64-bit Hilbert index → quantized POINT4D.</summary>
    Point4D Decode(ulong index);
}
