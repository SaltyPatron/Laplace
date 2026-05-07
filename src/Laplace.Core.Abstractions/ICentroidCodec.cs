namespace Laplace.Core.Abstractions;

/// <summary>
/// Codec for the v1.0 substrate centroid mantissa ABI. Encodes a
/// <see cref="CentroidPayloadV1"/> into the low mantissa bits of a 4D
/// position, and decodes it back. Geometry on bits 38..51 is preserved
/// (~14 bits per axis, 11 orders of magnitude better than the
/// super-Fibonacci spacing of 10^-3). Phase 2 / Track D.
/// </summary>
public interface ICentroidCodec
{
    /// <summary>Stuff payload into mantissa bits, return the perturbed position.</summary>
    Point4D Encode(Point4D position, CentroidPayloadV1 payload);

    /// <summary>Recover the payload from mantissa bits.</summary>
    CentroidPayloadV1 Decode(Point4D position);

    /// <summary>
    /// Strip payload bits (zero them) and return a geometry-only copy of
    /// the position. Use this before feeding a centroid into vertex_centroid
    /// or slerp when computing the next-tier centroid; restuff via Encode
    /// with the new tier's payload.
    /// </summary>
    Point4D StripPayload(Point4D position);
}
