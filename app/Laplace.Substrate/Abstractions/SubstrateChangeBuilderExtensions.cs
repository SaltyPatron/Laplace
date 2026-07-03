using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class SubstrateChangeBuilderExtensions
{
    public static Hash128? AddContentEdge(
        this SubstrateChangeBuilder builder,
        Hash128 subjectId,
        byte[] content,
        string relationName,
        Hash128 sourceId,
        double sourceTrust = SourceTrust.AcademicCurated)
    {
        var targetId = ContentEmitter.Emit(builder, content, sourceId);
        if (targetId is null) return null;
        builder.AddAttestation(NativeAttestation.Categorical(
            subjectId, relationName, targetId.Value, sourceId, null, sourceTrust));
        return targetId;
    }

    public static Hash128? AddContentEdge(
        this SubstrateChangeBuilder builder,
        Hash128 subjectId,
        string content,
        string relationName,
        Hash128 sourceId,
        double sourceTrust = SourceTrust.AcademicCurated)
    {
        var targetId = ContentEmitter.Emit(builder, content, sourceId);
        if (targetId is null) return null;
        builder.AddAttestation(NativeAttestation.Categorical(
            subjectId, relationName, targetId.Value, sourceId, null, sourceTrust));
        return targetId;
    }

    public static Hash128 AddCategoryEdge(
        this SubstrateChangeBuilder builder,
        Hash128 subjectId,
        string relationName,
        string targetNodeName,
        Hash128 metaTypeId,
        Hash128 sourceId,
        ISet<Hash128> seen,
        double sourceTrust = SourceTrust.AcademicCurated)
    {
        var targetId = HighwayNodeEmitter.Emit(builder, targetNodeName, metaTypeId, sourceId, sourceTrust, seen);
        builder.AddAttestation(NativeAttestation.Categorical(
            subjectId, relationName, targetId, sourceId, null, sourceTrust));
        return targetId;
    }
}
