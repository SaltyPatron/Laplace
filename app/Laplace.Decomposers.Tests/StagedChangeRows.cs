using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Decomposers.Tests;

/// <summary>
/// The rows a change states, whether carried as managed rows or in native stages: a
/// decomposer that stages natively states the same records as one that builds them.
/// Staged claims carry their subject, relation, object and context.
/// </summary>
internal static class StagedChangeRows
{
    public static IEnumerable<EntityRow> Entities(SubstrateChange change)
    {
        foreach (var row in change.Entities) yield return row;
        var stages = Stages(change);
        if (stages.Count == 0) yield break;
        var parsed = CopyTupleParser.ParseEntities(
            stages.Select(static stage => stage.TupleBuffer(IntentStageTable.Entities)).ToList());
        for (int i = 0; i < parsed.Ids.Count; i++)
            yield return new EntityRow(parsed.Ids[i], (byte)parsed.Tiers[i], parsed.TypeIds[i]);
    }

    public static IEnumerable<AttestationRow> Attestations(SubstrateChange change)
    {
        foreach (var row in change.Attestations) yield return row;
        var stages = Stages(change);
        if (stages.Count == 0) yield break;
        var parsed = CopyTupleParser.ParseAttestations(
            stages.Select(static stage => stage.TupleBuffer(IntentStageTable.Attestations)).ToList());
        for (int i = 0; i < parsed.Ids.Count; i++)
            yield return new AttestationRow(
                parsed.Ids[i], parsed.SubjectIds[i], parsed.TypeIds[i],
                parsed.ObjectIds[i] == default ? null : parsed.ObjectIds[i],
                default,
                parsed.ContextIds[i] == default ? null : parsed.ContextIds[i],
                AttestationOutcome.Confirm, 0, parsed.Counts[i], 0, 0,
                SumScoreFp1e9: parsed.SumScores[i], FoldReplayable: parsed.FoldReplayable[i]);
    }

    /// <summary>Each form a change states: its entity, type and packed trajectory (x y z m per vertex).</summary>
    public static IEnumerable<(Hash128 EntityId, short Type, double[]? TrajectoryXyzm)> Forms(SubstrateChange change)
    {
        foreach (var row in change.Physicalities) yield return (row.EntityId, (short)row.Type, row.TrajectoryXyzm);
        var stages = Stages(change);
        if (stages.Count == 0) yield break;
        var parsed = CopyTupleParser.ParsePhysicalities(
            stages.Select(static stage => stage.TupleBuffer(IntentStageTable.Physicalities)).ToList());
        for (int i = 0; i < parsed.Ids.Count; i++)
            yield return (parsed.EntityIds[i], parsed.Types[i],
                parsed.TrajectoriesEwkb[i] is { } ewkb ? LineStringZm(ewkb) : null);
    }

    // A PostGIS EWKB LineString ZM: byte order, type (with SRID flag), optional SRID,
    // vertex count, then x y z m per vertex.
    private static double[] LineStringZm(byte[] ewkb)
    {
        bool little = ewkb[0] == 1;
        uint U32(int at) => little
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ewkb.AsSpan(at))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ewkb.AsSpan(at));
        uint type = U32(1);
        int at = 5 + ((type & 0x20000000u) != 0 ? 4 : 0);
        int points = checked((int)U32(at));
        at += 4;
        var xyzm = new double[points * 4];
        for (int k = 0; k < xyzm.Length; k++)
        {
            long bits = little
                ? System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(ewkb.AsSpan(at + 8 * k))
                : System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(ewkb.AsSpan(at + 8 * k));
            xyzm[k] = BitConverter.Int64BitsToDouble(bits);
        }
        return xyzm;
    }

    private static List<IntentStage> Stages(SubstrateChange change) =>
        change.IntentStages.IsDefaultOrEmpty ? []
            : change.IntentStages.Where(static stage => !stage.IsInvalid).ToList();
}
