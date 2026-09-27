using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Ingestion;

/// <summary>
/// Run-level admission accounting: every entity a run admits has a physicality by the end
/// of the run. Rows are tracked across changes, so one phase may declare content that a
/// later phase places.
/// </summary>
internal sealed class EntityAdmissionTracker
{
    private readonly Dictionary<Hash128, PendingEntity> _contentAwaitingPhysicality = new();
    private readonly object _gate = new();

    internal void Observe(SubstrateChange change)
    {
        if (change.Entities.IsDefaultOrEmpty
            && change.Physicalities.IsDefaultOrEmpty
            && change.IntentStages.IsDefaultOrEmpty)
            return;

        // Admission covers the whole change: the managed row arrays and the rows staged in
        // native IntentStages, where shared content and recipe composition land.
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
            }

            void ObserveEntity(EntityRow entity)
            {
                if (placedHere.Contains(entity.Id))
                    return;

                // Every entity is realized: the provider supplies the content, composition or
                // typed structure from which the pipeline emits its physicality. An unplaced
                // entity stays pending until a later phase of the run places it.
                _contentAwaitingPhysicality.TryAdd(
                    entity.Id,
                    new PendingEntity(
                        entity.Id,
                        entity.TypeId,
                        change.Metadata.SourceContentUnitName));
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

    internal sealed record PendingEntity(Hash128 Id, Hash128 TypeId, string UnitName);
}
