using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public static class PhysicalityId
{
    // Address of one typed physicality of an entity: BLAKE3-128 over the 16-byte
    // entity id followed by the physicality type as int16 little-endian (18 bytes).
    // The entity id is the Merkle content identity; the physicality is a typed
    // realization of that same entity, not a second identity. The type bytes are
    // written little-endian explicitly so the pre-image is fixed by the format,
    // not by host byte order, and matches native laplace_physicality_id_compute.
    public static Hash128 Compute(Hash128 entityId, PhysicalityType type)
    {
        Span<byte> span = stackalloc byte[18];
        entityId.WriteBytes(span.Slice(0, 16));
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span.Slice(16, 2), (short)type);
        return Hash128.Blake3(span);
    }
}
