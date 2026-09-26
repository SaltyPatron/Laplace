using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

/// <summary>
/// A test that drains a decomposer without a writer must release each apply barrier
/// itself: ArtifactDecomposerMultiPhase waits on one between dependency levels, and no
/// writer is there to complete it.
/// </summary>
internal static class WriterlessDrain
{
    public static async IAsyncEnumerable<SubstrateChange> WithoutWriter(
        this IAsyncEnumerable<SubstrateChange> changes,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (SubstrateChange change in changes.WithCancellation(ct))
        {
            change.ApplyBarrier?.Complete();
            yield return change;
        }
    }
}

/// <summary>
/// A change carries its rows in its native intent stages as well as (for legacy emitters)
/// its managed arrays; the staged rows are what persists, so a test reads both.
/// </summary>
internal static class StagedRows
{
    public static IEnumerable<EntityRow> AllEntities(this SubstrateChange change) =>
        change.IntentStages.IsDefaultOrEmpty
            ? change.Entities
            : change.Entities.Concat(Laplace.SubstrateCRUD.Npgsql.CopyTupleParser.DecodeEntityRows(
                Buffers(change, IntentStageTable.Entities)));

    public static IEnumerable<AttestationRow> AllAttestations(this SubstrateChange change)
    {
        if (change.IntentStages.IsDefaultOrEmpty) return change.Attestations;
        var staged = new List<AttestationRow>();
        Laplace.SubstrateCRUD.Npgsql.CopyTupleParser.DecodeAttestations(
            Buffers(change, IntentStageTable.Attestations), staged);
        return change.Attestations.Concat(staged);
    }

    public static IEnumerable<Hash128> AllPhysicalityEntityIds(this SubstrateChange change) =>
        change.IntentStages.IsDefaultOrEmpty
            ? change.Physicalities.Select(static p => p.EntityId)
            : change.Physicalities.Select(static p => p.EntityId).Concat(
                Laplace.SubstrateCRUD.Npgsql.CopyTupleParser.ParsePhysicalities(
                    Buffers(change, IntentStageTable.Physicalities)).EntityIds);

    private static List<(IntPtr Ptr, long Len)> Buffers(SubstrateChange change, IntentStageTable table) =>
        change.IntentStages.Select(stage => stage.TupleBuffer(table)).ToList();
}
