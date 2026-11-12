using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// Builds the per-search continuation cache outside the search, from the board alone: the opening book as
/// the fallback, the active line, each legal successor that mates, and a capture into a position the local
/// Syzygy tables win. Recorded evidence reaches the search through <see cref="SubstrateRootBias"/>.
/// </summary>
public static class SearchContinuationSeed
{
    /// <summary>The most men a probed position may hold: the tables this host carries.</summary>
    public const int TablebaseMenCeiling = 5;

    public static SearchContinuationTable ForUci(
        ChessModality modality,
        string? fen,
        IReadOnlyList<string>? uciMoves,
        ISyzygyProber? syzygyProber = null)
    {
        ArgumentNullException.ThrowIfNull(modality);
        var start = string.IsNullOrWhiteSpace(fen) ? modality.Initial() : modality.FromFen(fen);
        var origin = start.Board.Clone();
        var cursor = origin.Clone();
        var line = new List<ChessMove>();
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
            }
        }

        var table = new SearchContinuationTable(OpeningContinuationBook.Shared);
        var current = cursor;
        var prober = syzygyProber ?? ChessTablebaseRuntime.Prober;
        int maxTbMen = prober is null ? 0 : Math.Min(TablebaseMenCeiling, prober.Largest);

        foreach (var move in MoveGen.Legal(current))
        {
            var next = current.Clone();
            MoveApply.Make(next, move);

            var nextLegal = MoveGen.Legal(next);
            if (nextLegal.Count == 0 && MoveGen.InCheck(next, next.WhiteToMove))
            {
                table.SeedRetrogradeCheckpoint(next, null, wdl: 0, pliesToClose: 0, isCheckmate: true);
                table.SeedRetrogradeCheckpoint(current, move, wdl: 4, pliesToClose: 1, isCheckmate: true);
                continue;
            }

            if (prober is not null && next.Rules.IsStandard && next.Castle == CastleRights.None
                && ChessSyzygy.MenCount(next) == maxTbMen + 1
                && OnePlyClosure(next, prober, maxTbMen) is { } closure)
                table.SeedRetrogradeCheckpoint(
                    next, closure.Move, closure.Wdl, closure.PliesToClose, isCheckmate: false);
        }

        if (line.Count > 0)
            table.SeedActiveGame(origin, line);
        return table;
    }

    /// <summary>A capture from <paramref name="board"/> into a position the tables say its mover wins.</summary>
    internal static (ChessMove Move, int Wdl, int PliesToClose)? OnePlyClosure(
        Board board, ISyzygyProber prober, int maxTbMen)
    {
        (ChessMove Move, int Wdl, int PliesToClose)? best = null;
        foreach (var move in MoveGen.Legal(board))
        {
            bool captures = board.Squares[move.To] != Piece.Empty || (move.Flags & MoveFlags.EnPassant) != 0;
            if (!captures) continue;
            var next = board.Clone();
            MoveApply.Make(next, move);
            if (ChessSyzygy.MenCount(next) > maxTbMen) continue;
            if (prober.Probe(next) is not { } verdict) continue;
            int wdl = 4 - verdict.Wdl;                     // the mover's, the other side's inverted
            int plies = Math.Abs(verdict.Dtz) + 1;
            if (wdl < 3) continue;                         // only a winning conversion is certified
            if (best is null || wdl > best.Value.Wdl || (wdl == best.Value.Wdl && plies < best.Value.PliesToClose))
                best = (move, wdl, plies);
        }
        return best;
    }
}
