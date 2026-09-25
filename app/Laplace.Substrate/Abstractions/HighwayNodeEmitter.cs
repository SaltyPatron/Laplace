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
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);
        Hash128 id = ContentEmitter.RootId(canonicalName)
            ?? throw new InvalidOperationException(
                $"vocabulary content could not be composed: {canonicalName}");
        if (!seen.Add(id)) return id;

        Hash128 admitted = ContentEmitter.Emit(builder, canonicalName, sourceId)
            ?? throw new InvalidOperationException(
                $"vocabulary content could not be admitted: {canonicalName}");
        if (admitted != id)
            throw new InvalidOperationException(
                $"vocabulary identity changed during admission: {canonicalName}");


        if (parentId is { } parent)
            builder.AddAttestation(NativeAttestation.Categorical(
                id, parentRelation, parent, sourceId, null, trust));

        VocabularyNames.Track(readbackNames, canonicalName);
        return id;
    }
}
