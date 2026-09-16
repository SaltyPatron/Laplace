using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public static class PhysicalityId
{
    // Compatibility lookup address for the current typed placement of entityId.
    // Keep it bit-identical to laplace_physicality_id_compute. Several exact
    // physicality bodies may be observed at this address while entityId stays
    // unchanged. Native physicality descriptors represent those bodies as
    // ordinary canonical content; this address is not their immutable form ID.
    // Derived geometry never replaces the realized entity's ordered identity.
    // LAYOUT IS LITTLE-ENDIAN BY SPECIFICATION, not by host accident (GH #904).
    // BitConverter writes the HOST's byte order, and the C twin
    // (laplace_physicality_id_compute) memcpy'd an int16_t, also host order: the
    // two agreed on every machine either has run on, and would mint DIFFERENT
    // physicality ids on a big-endian host for byte-identical content. An identity
    // axiom that holds only because nobody compiled it elsewhere is not an axiom.
    // Both sides now write the two type bytes explicitly little-endian, so the
    // pre-image is a property of the format rather than of the compiler. Output is
    // byte-identical to the previous form on every host in service; no reseed.
    // PhysicalityIdParityTests pins the 18-byte layout independently of this code.
    public static Hash128 Compute(Hash128 entityId, PhysicalityType type)
    {
        Span<byte> span = stackalloc byte[18];
        entityId.WriteBytes(span.Slice(0, 16));
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span.Slice(16, 2), (short)type);
        return Hash128.Blake3(span);
    }
}
