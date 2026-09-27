using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Realizes an identity whose stable id is governed separately from the bytes that name
/// it, such as a source. The id is unchanged; its physicality is a deterministic projection
/// onto the name's content, which is composed through the ordinary content spine.
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

        builder.AddEntity(id, tier, typeId);

        // When the governed id is the content root itself, the content spine already
        // emitted its physicality; no second one is staged.
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
