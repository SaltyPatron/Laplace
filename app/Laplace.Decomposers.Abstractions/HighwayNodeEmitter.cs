using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class HighwayNodeEmitter
{
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
