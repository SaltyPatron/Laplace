using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// Completion of one versioned source unit. The completion row rides with that unit's
/// evidence into the accepting control transaction (laplace.ingest_unit_completion), so
/// it is exactly as durable as the unit's claims. Entity presence alone never grants
/// this proof: independently committed carrier COPYs may survive an unsuccessful
/// evidence admission. A completion is operational state, never an attestation.
/// </summary>
public static class IngestUnitCompletion
{
    public static IngestUnitCompletionKey Key(
        Hash128 unitId, Hash128 ownerSourceId, int layerOrder, Hash128? contextId = null)
        => new IngestUnitCompletionKey(ownerSourceId, unitId, layerOrder, contextId).Validate();

    /// <summary>
    /// Call only after the complete unit has composed successfully. The owner is the
    /// source witness, so source eviction clears this completion with the owned output.
    /// A unit completion never satisfies whole-layer completion.
    /// </summary>
    public static void Emit(
        SubstrateChangeBuilder builder, Hash128 unitId, Hash128 ownerSourceId,
        int layerOrder, Hash128? contextId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.RecordUnitCompletion(Key(unitId, ownerSourceId, layerOrder, contextId));
    }
}
