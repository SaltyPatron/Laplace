using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// A durable receipt for one completed, versioned source unit. The receipt rides
/// with that unit's evidence in the accepting control transaction. Entity presence
/// alone never grants this proof; independently committed carrier COPYs may survive
/// an unsuccessful evidence admission.
/// </summary>
public static class IngestUnitCompletion
{
    private static readonly Lazy<HashSet<Hash128>> RelationTypes = new(() =>
        Enumerable.Range(0, LayerCompletion.MaxMarkedLayer + 1)
            .Select(RelationTypeId).ToHashSet());

    internal static bool IsRelationType(Hash128 id) => RelationTypes.Value.Contains(id);

    public static Hash128 RelationTypeId(int layerOrder)
    {
        if ((uint)layerOrder > LayerCompletion.MaxMarkedLayer)
            throw new ArgumentOutOfRangeException(nameof(layerOrder), layerOrder,
                $"ingest layer must be in 0..{LayerCompletion.MaxMarkedLayer}");
        return Hash128.OfCanonical($"substrate/type/HasUnitCompleted/{layerOrder}/v1");
    }

    public static Hash128 AttestationId(
        Hash128 unitId, Hash128 ownerSourceId, int layerOrder, Hash128? contextId = null)
        => NativeAttestation.ComputeId(
            unitId, RelationTypeId(layerOrder), unitId, ownerSourceId, contextId);

    /// <summary>
    /// Call only after the complete unit has composed successfully. Source is its
    /// actual owner, so source eviction retracts this receipt with the owned output.
    /// Its separate operational relation cannot satisfy whole-source completion.
    /// </summary>
    public static void Emit(
        SubstrateChangeBuilder builder, Hash128 unitId, Hash128 ownerSourceId,
        int layerOrder, Hash128? contextId = null)
    {
        var typeId = RelationTypeId(layerOrder);
        CanonicalNamedIdentity.Declare(
            builder,
            typeId,
            EntityTier.Word,
            BootstrapIntentBuilder.RelationTypeMetaTypeId,
            $"substrate/type/HasUnitCompleted/{layerOrder}/v1",
            ownerSourceId);
        builder.AddAttestation(NativeAttestation.CategoricalResolved(
            unitId, typeId, unitId, ownerSourceId, contextId,
            RelationTypeRank.Mandate * SourceTrust.SubstrateMandate));
    }
}
