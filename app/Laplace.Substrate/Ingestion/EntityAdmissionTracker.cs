using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// Run-level admission accounting for managed rows. Native tier-tree stages compose
/// entities and physicalities together; managed rows are tracked across changes so an
/// ETL phase may declare content before a later phase places it without weakening the
/// terminal invariant.
/// </summary>
internal sealed class EntityAdmissionTracker
{
    private readonly Dictionary<Hash128, PendingEntity> _contentAwaitingPhysicality = new();
    private readonly HashSet<Hash128> _governedWithoutPhysicality = new();
    private readonly object _gate = new();

    internal void Observe(SubstrateChange change)
    {
        if (change.Entities.IsDefaultOrEmpty && change.Physicalities.IsDefaultOrEmpty)
            return;

        var placedHere = new HashSet<Hash128>();
        foreach (var physicality in change.Physicalities)
            placedHere.Add(physicality.EntityId);

        lock (_gate)
        {
            foreach (var physicality in change.Physicalities)
            {
                _contentAwaitingPhysicality.Remove(physicality.EntityId);
                _governedWithoutPhysicality.Remove(physicality.EntityId);
            }

            foreach (var entity in change.Entities)
            {
                if (placedHere.Contains(entity.Id))
                    continue;

                if (EntityIdentityPolicy.RequiresPhysicality(entity.TypeId))
                {
                    _contentAwaitingPhysicality.TryAdd(
                        entity.Id,
                        new PendingEntity(
                            entity.Id,
                            entity.TypeId,
                            change.Metadata.SourceContentUnitName));
                }
                else
                {
                    _governedWithoutPhysicality.Add(entity.Id);
                }
            }
        }
    }

    internal PendingEntity[] SnapshotPendingContent()
    {
        lock (_gate) return _contentAwaitingPhysicality.Values.ToArray();
    }

    internal int GovernedWithoutPhysicalityCount
    {
        get { lock (_gate) return _governedWithoutPhysicality.Count; }
    }

    internal sealed record PendingEntity(Hash128 Id, Hash128 TypeId, string UnitName);
}
