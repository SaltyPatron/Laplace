using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public static class PhysicalityId
{
    // Physicality identity is CONTENT-derived, exactly like entity identity, and
    // must stay bit-identical to the native physicality_id_compute in
    // engine/core/src/content_witness_batch.c. entityId is already
    // Blake3-Merkle(tier, childIds) -- an exact, collision-resistant hash of the
    // content -- and the geometry (centroid coord + trajectory) is a DERIVED,
    // non-exact function of that same content (Substrate Invariant Rule #1:
    // "Content-hash identity is exact. Centroid/hilbert identity is not" --
    // centroids collide, e.g. cat/act share a centroid). So identity is
    // (entityId, type) ONLY; coord/trajectory are stored as payload but never
    // enter the id. Hashing the float geometry made identity fragile to sub-ULP
    // float divergence across the compose paths and re-ingests, forging spurious
    // duplicate physicalities (observed: 319 chess-move entities).
    public static Hash128 Compute(Hash128 entityId, PhysicalityType type)
    {
        Span<byte> span = stackalloc byte[18];
        entityId.WriteBytes(span.Slice(0, 16));
        BitConverter.TryWriteBytes(span.Slice(16, 2), (short)type);
        return Hash128.Blake3(span);
    }
}
