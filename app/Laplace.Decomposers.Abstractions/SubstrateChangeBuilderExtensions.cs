using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Extension methods on <see cref="SubstrateChangeBuilder"/> for common ingest edge patterns.
/// Eliminates null-check chains that recur 500+ lines across decomposers.
/// </summary>
public static class SubstrateChangeBuilderExtensions
{
    /// <summary>
    /// Emit a codepoint-walk content entity for <paramref name="content"/> (UTF-8 bytes) and attest
    /// <c>subjectId → relationName → contentEntity</c>. Returns the content entity's id,
    /// or null if the content was empty.
    /// </summary>
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

    /// <summary>
    /// Emit a codepoint-walk content entity for <paramref name="content"/> string and attest
    /// <c>subjectId → relationName → contentEntity</c>.
    /// </summary>
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

    /// <summary>
    /// Emit a highway node entity for <paramref name="targetNodeName"/> (id = blake3(utf8_bytes))
    /// and attest <c>subjectId → relationName → nodeEntity</c>.
    /// </summary>
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
