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
        string parentRelation = "IS_A",
        System.Collections.Concurrent.ConcurrentDictionary<string, byte>? readbackNames = null)
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

        // GH #1041: the node's name is VOCABULARY, not content. The old
        // ContentEmitter.Emit here staged a full text DAG for every tag string
        // ("NNP" as a word entity, "Number=Sing" as a sentence) — identifiers
        // minted as prose. The id is blake3(canonicalName), which is exactly
        // realize.canonical_id(name), so registering the name in
        // laplace.canonical_names (via the readback set → register_canonicals
        // at run end) gives realize.render its arm-1 hit with no ladder rows.
        VocabularyNames.Track(readbackNames, canonicalName);

        return id;
    }
}
