using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Builds the per-search continuation cache outside the search. The active line and
/// one ply of legal successors are local. Witnessed centipawns are one bounded read
/// of stored <c>HAS_EVAL</c> sums. Opening moves come from the shared catalog book.
/// </summary>
public static class SearchContinuationSeed
{
    public static SearchContinuationTable ForUci(
        ChessModality modality, string? fen, IReadOnlyList<string>? uciMoves, NpgsqlDataSource? witnessed)
    {
        ArgumentNullException.ThrowIfNull(modality);
        var start = string.IsNullOrWhiteSpace(fen) ? modality.Initial() : modality.FromFen(fen);
        var origin = start.Board.Clone();
        var cursor = origin.Clone();
        var line = new List<ChessMove>();
        var positions = new List<Board> { cursor.Clone() };
        if (uciMoves is not null)
        {
            foreach (var uci in uciMoves)
            {
                ChessMove? found = null;
                foreach (var candidate in MoveGen.Legal(cursor))
                {
                    if (candidate.ToUci() != uci) continue;
                    found = candidate;
                    break;
                }
                if (found is null) break;
                line.Add(found.Value);
                MoveApply.Make(cursor, found.Value);
                positions.Add(cursor.Clone());
            }
        }

        var current = positions[^1];
        foreach (var move in MoveGen.Legal(current))
        {
            var next = current.Clone();
            MoveApply.Make(next, move);
            positions.Add(next);
        }

        var table = new SearchContinuationTable(OpeningContinuationBook.Shared);
        if (line.Count > 0)
            table.SeedActiveGame(origin, line);
        if (witnessed is not null)
            SeedWitnessed(table, positions, witnessed);
        return table;
    }

    /// <summary>
    /// Inverse of <see cref="PgnEvals.EvalSumFp1e9"/>. Null when the stored probability
    /// is outside the open unit interval.
    /// </summary>
    public static int? Centipawns(long sumScoreFp1e9, long observations)
    {
        if (observations <= 0) return null;
        double p = sumScoreFp1e9 / (observations * 1_000_000_000d);
        if (p <= 1e-6 || p >= 1d - 1e-6) return null;
        double cp = -400d * Math.Log((1d - p) / p);
        if (double.IsNaN(cp) || double.IsInfinity(cp)) return null;
        return (int)Math.Round(Math.Clamp(cp, -20_000d, 20_000d));
    }

    private static void SeedWitnessed(SearchContinuationTable table, List<Board> positions, NpgsqlDataSource dataSource)
    {
        var boards = new Dictionary<Hash128, Board>();
        foreach (var board in positions)
            boards.TryAdd(ChessPositionIdentity.PositionId(board), board);
        var raw = new byte[boards.Count][];
        int index = 0;
        foreach (var id in boards.Keys)
            raw[index++] = id.ToBytes();

        var rows = NpgsqlSubstrateReads.HasEvalSums(
            dataSource,
            ChessVocabulary.HasEvalType.ToBytes(),
            ChessVocabulary.HasEvalObject.ToBytes(),
            raw);
        var byId = new Dictionary<Hash128, NpgsqlSubstrateReads.EvalSumRow>(rows.Count);
        foreach (var row in rows)
            byId[Hash128.FromBytes(row.Position)] = row;

        foreach (var (id, board) in boards)
        {
            if (!byId.TryGetValue(id, out var row)) continue;
            if (Centipawns(row.SumScoreFp1e9, row.Observations) is not int cp) continue;
            table.SeedWitnessedEvaluation(board, cp, row.Observations);
        }
    }
}
