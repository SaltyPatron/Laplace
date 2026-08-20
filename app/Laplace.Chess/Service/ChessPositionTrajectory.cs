using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>Inverse of the typed board physicality writer for batched readback.</summary>
public static class ChessPositionTrajectory
{
    public static IReadOnlyDictionary<Hash128, Board> Decode(
        IReadOnlyList<NpgsqlSubstrateReads.NestedTrajectoryConstituentRow> rows)
    {
        var parents = new Dictionary<Hash128, SortedDictionary<int, List<(int Ord, Hash128 Id)>>>();
        foreach (var row in rows)
        {
            var parent = Hash128.FromBytes(row.ParentId);
            if (!parents.TryGetValue(parent, out var atoms))
                parents[parent] = atoms = [];
            if (!atoms.TryGetValue(row.NodeOrdinal, out var fields))
                atoms[row.NodeOrdinal] = fields = [];
            fields.Add((row.FieldOrdinal, Hash128.FromBytes(row.FieldId)));
        }

        var result = new Dictionary<Hash128, Board>(parents.Count);
        foreach (var (parent, atoms) in parents)
        {
            var encoded = new List<IReadOnlyList<Hash128>>(atoms.Count);
            foreach (var fields in atoms.Values)
                encoded.Add(fields.OrderBy(static f => f.Ord).Select(static f => f.Id).ToArray());
            if (ChessPositionIdentity.TryBoardFromAtomConstituents(encoded, out var board))
                result[parent] = board;
        }
        return result;
    }
}
