using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// One canonical realization for identities whose stable id is governed independently
/// from the human-readable bytes that name them. The identity stays unchanged; its
/// physicality is a deterministic projection onto canonical content emitted through the
/// ordinary content spine. This is the shared replacement for bare source/type/relation
/// vocabulary entities.
/// </summary>
public static class CanonicalNamedIdentity
{
    public static Hash128 Declare(
        SubstrateChangeBuilder builder,
        Hash128 id,
        byte tier,
        Hash128 typeId,
        string canonicalName,
        Hash128 sourceId,
        long observedAtUnixUs = IntentStage.PgEpochUnixUs)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);

        OrderedCompositionComponent component = ContentEmitter.StageComponent(
            builder, canonicalName, sourceId)
            ?? throw new InvalidOperationException(
                $"canonical identity '{canonicalName}' could not be composed");

        builder.AddEntity(id, tier, typeId, sourceId);

        // When the governed id is itself the ordinary content root, the content spine
        // already emitted the exact physicality. Do not invent a second representation.
        if (component.Id == id)
            return id;

        Span<double> coord = stackalloc double[4]
        {
            component.CoordX, component.CoordY, component.CoordZ, component.CoordM
        };
        builder.AddPhysicality(new PhysicalityRow(
            PhysicalityId.Compute(id, PhysicalityType.Projection),
            id,
            sourceId,
            PhysicalityType.Projection,
            coord[0], coord[1], coord[2], coord[3],
            Hilbert128.Encode(coord),
            Trajectory.Build([component.Id]),
            1,
            null,
            null,
            observedAtUnixUs));
        return id;
    }
}
