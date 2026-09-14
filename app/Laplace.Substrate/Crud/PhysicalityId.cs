using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public static class PhysicalityId
{
    // Physicality identity is CONTENT-derived, exactly like entity identity, and
    // must stay bit-identical to the native physicality_id_compute in
    // engine/core/src/content_witness_batch.c. entityId is already the current
    // BLAKE3-derived 128-bit content address: for multi-child composition,
    // hash128_merkle's preimage is the Merkle domain plus the ORDERED child-id
    // sequence. The retained tier argument is explicitly ignored by hash128.c;
    // singleton composition preserves the child id. This is a finite executable
    // address, not a theorem of global injectivity over the unbounded composition
    // domain. Geometry (centroid coord + trajectory) is a DERIVED physical
    // realization of that content and does not replace exact identity (Substrate
    // Invariant Rule #1: content identity is exact under the current recipe;
    // centroid/hilbert identity is not -- centroids can collide, e.g. cat/act).
    // So physicality identity is (entityId, type) ONLY; coord/trajectory are stored
    // as payload but never enter the id. Hashing float geometry made identity
    // fragile to sub-ULP divergence across compose paths and re-ingests, forging
    // spurious duplicate physicalities (observed: 319 chess-move entities).
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
