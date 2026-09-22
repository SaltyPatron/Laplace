using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

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
        if (change.Entities.IsDefaultOrEmpty
            && change.Physicalities.IsDefaultOrEmpty
            && change.IntentStages.IsDefaultOrEmpty)
            return;

        // Admission is a property of the COMPLETE source stream, not only the managed
        // compatibility arrays. Shared content/recipe composition lives primarily in
        // native IntentStages; ignoring those rows made the run-level E/P invariant blind
        // to the production path and allowed a green receipt to prove only the sidecar.
        var placedHere = new HashSet<Hash128>();
        foreach (var physicality in change.Physicalities)
            placedHere.Add(physicality.EntityId);

        var nativeEntities = new List<EntityRow>();
        if (!change.IntentStages.IsDefaultOrEmpty)
        {
            foreach (var stage in change.IntentStages)
            {
                if (stage.IsInvalid) continue;

                if (stage.PhysicalityCount > 0)
                {
                    var physicalities = CopyTupleParser.ParsePhysicalities(
                        [stage.TupleBuffer(IntentStageTable.Physicalities)]);
                    foreach (Hash128 entityId in physicalities.EntityIds)
                        placedHere.Add(entityId);
                }

                if (stage.EntityCount > 0)
                    nativeEntities.AddRange(CopyTupleParser.DecodeEntityRows(
                        [stage.TupleBuffer(IntentStageTable.Entities)]));
            }
        }

        lock (_gate)
        {
            foreach (Hash128 entityId in placedHere)
            {
                _contentAwaitingPhysicality.Remove(entityId);
                _governedWithoutPhysicality.Remove(entityId);
            }

            void ObserveEntity(EntityRow entity)
            {
                if (placedHere.Contains(entity.Id))
                    return;

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

            foreach (var entity in change.Entities)
                ObserveEntity(entity);
            foreach (var entity in nativeEntities)
                ObserveEntity(entity);
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
