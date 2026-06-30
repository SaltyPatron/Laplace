using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Canonical emitter for highway nodes — POS tags, relation types, entity type labels, and any
/// other named concept whose identity IS its content-addressed canonical name.
/// Replaces <see cref="VocabularyAnchor"/>: the id is always <c>blake3(utf8_bytes)</c> via
/// <see cref="HighwayPerfcache.NodeHash"/>, so all decomposers converge on the same entity.
/// </summary>
public static class HighwayNodeEmitter
{
    /// <summary>
    /// Emit a highway node entity. <paramref name="canonicalName"/> is both the identity key
    /// (its blake3 hash becomes the entity id) and the name linked via HAS_NAME_ALIAS. When
    /// <paramref name="parentId"/> is set, a <paramref name="parentRelation"/> edge is attested.
    /// Idempotent within a run via <paramref name="seen"/>.
    /// </summary>
    public static Hash128 Emit(
        SubstrateChangeBuilder builder,
        string canonicalName,
        Hash128 metaTypeId,
        Hash128 sourceId,
        double trust,
        ISet<Hash128> seen,
        Hash128? parentId = null,
        string parentRelation = "IS_A")
    {
        var id = HighwayPerfcache.NodeHash(canonicalName);
        if (!seen.Add(id)) return id;

        builder.AddEntity(new EntityRow(id, EntityTier.Word, metaTypeId, sourceId));

        if (parentId is { } parent)
        {
            builder.AddEntity(new EntityRow(parent, EntityTier.Word, metaTypeId, sourceId));
            builder.AddAttestation(NativeAttestation.Categorical(
                id, parentRelation, parent, sourceId, null, trust));
        }

        if (ContentWitnessBatch.Emit(builder, canonicalName, sourceId) is { } nameId)
            builder.AddAttestation(NativeAttestation.Categorical(
                id, "HAS_NAME_ALIAS", nameId, sourceId, null, trust));

        return id;
    }
}
