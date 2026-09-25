using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// Layer and file completion. Both are operational state in
/// laplace.ingest_layer_completion / laplace.ingest_unit_completion, recorded by the
/// writer in the control transaction that accepts the change carrying them. Neither
/// is testimony: no attestation, no entity, no relation id.
/// </summary>
public static class LayerCompletion
{
    /// <summary>Ingest layers occupy one byte; the completion tables check the same range.</summary>
    public const int MaxLayer = IngestUnitCompletionKey.MaxLayer;

    /// <summary>
    /// Per-file completion: the file's content identity is the unit, the decomposer
    /// that completed it is the witness. Re-ingesting the same bytes finds the row and
    /// true-skips before any compose work; another decomposer consuming the same bytes
    /// cannot inherit it. Recorded in the same change as the file's content, so a
    /// completed file implies committed content.
    /// </summary>
    public static void RecordFile(
        SubstrateChangeBuilder builder, Hash128 fileRoot, Hash128 decomposerSourceId,
        int layerOrder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.RecordUnitCompletion(
            new IngestUnitCompletionKey(decomposerSourceId, fileRoot, layerOrder).Validate());
    }

    /// <summary>
    /// The terminal change of a full successful extraction: the decomposer's source
    /// witness has completed its layer. Carries no evidence rows.
    /// </summary>
    public static SubstrateChange Build(IDecomposer decomposer)
    {
        ArgumentNullException.ThrowIfNull(decomposer);
        var builder = new SubstrateChangeBuilder(
            decomposer.SourceId, $"layer-complete/{decomposer.LayerOrder}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0);
        builder.RecordLayerCompletion(
            new IngestLayerCompletionKey(decomposer.SourceId, decomposer.LayerOrder).Validate());
        return builder.Build();
    }
}
