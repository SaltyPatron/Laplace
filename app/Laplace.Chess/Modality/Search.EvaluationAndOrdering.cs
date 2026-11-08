using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Laplace.Engine.Core;

namespace Laplace.Modality.Chess;

public sealed partial class Search
{
    private static int ScoreToTt(int score, int ply) => score switch
    {
        >= MateThreshold => score + ply,
        <= -MateThreshold => score - ply,
        _ => score,
    };

    private static int ScoreFromTt(int score, int ply) => score switch
    {
        >= MateThreshold => score - ply,
        <= -MateThreshold => score + ply,
        _ => score,
    };

    private int RetrogradeScore(SearchContinuationTable.Entry continuation, int ply)
    {
        int wdl = continuation.RetrogradeWdl!.Value;
        int distance = Math.Min(Math.Abs(continuation.RetrogradePlies!.Value), 500);
        if (continuation.RetrogradeCheckmate)
        {
            return wdl switch
            {
                0 or 1 => -(Mate - ply - distance),
                2 => DrawScoreAtPly(ply),
                3 or 4 => Mate - ply - distance,
                _ => 0,
            };
        }
        return wdl switch
        {
            0 => -20_000 + distance + ply,
            1 => -10_000 + distance + ply,
            2 => DrawScoreAtPly(ply),
            3 => 10_000 - distance - ply,
            4 => 20_000 - distance - ply,
            _ => 0,
        };
    }

    private const int TablebaseMen = 5;

    private static int Men(Board b) => BitOperations.PopCount(b.OccupiedBB);

    /// <summary>
    /// When every legal root move is solved at five men or fewer, keep the moves that
    /// preserve the best WDL and, among wins and losses, the best DTZ. A mate the
    /// table does not store outranks a longer tablebase win.
    /// </summary>
    private void FilterRootTablebase(Board b, List<ChessMove> moves)
    {
        if (_tablebase is null || !b.Rules.IsStandard || Men(b) > TablebaseMen || moves.Count == 0)
            return;

        var scored = new (int Wdl, int Dtz)[moves.Count];
        var known = new bool[moves.Count];
        int bestWdl = int.MinValue;
        for (int i = 0; i < moves.Count; i++)
        {
            var undo = MoveApply.MakeWithUndo(b, moves[i]);
            bool solved = false;
            int childWdl = 2, childDtz = 0;
            if (MoveGen.Legal(b).Count == 0)
            {
                solved = true;
                childWdl = MoveGen.InCheck(b, b.WhiteToMove) ? 0 : 2;
                childDtz = 0;
            }
            else if (Men(b) <= TablebaseMen && _tablebase(b) is { } verdict)
            {
                solved = true;
                childWdl = verdict.Wdl;
                childDtz = Math.Abs(verdict.Dtz);
            }
            MoveApply.Unmake(b, moves[i], undo);
            if (!solved) return;
            int parentWdl = childWdl switch
            {
                0 => 4,
                1 => 3,
                2 => 2,
                3 => 1,
                4 => 0,
                _ => 2,
            };
            scored[i] = (parentWdl, childDtz);
            known[i] = true;
            if (parentWdl > bestWdl) bestWdl = parentWdl;
        }

        bool rankDtz = bestWdl != 2;
        bool shorter = bestWdl >= 3;
        int bestDtz = shorter ? int.MaxValue : int.MinValue;
        if (rankDtz)
        {
            for (int i = 0; i < moves.Count; i++)
            {
                if (!known[i] || scored[i].Wdl != bestWdl) continue;
                bestDtz = shorter ? Math.Min(bestDtz, scored[i].Dtz) : Math.Max(bestDtz, scored[i].Dtz);
            }
        }

        int kept = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            if (!known[i] || scored[i].Wdl != bestWdl) continue;
            if (rankDtz && scored[i].Dtz != bestDtz) continue;
            moves[kept++] = moves[i];
        }
        if (kept == 0 || kept == moves.Count) return;
        moves.RemoveRange(kept, moves.Count - kept);
    }

    private int Quiesce(
        Board b,
        int alpha,
        int beta,
        int ply,
        int repetitionStart,
        ulong repetitionSignature)
    {
        if (_ct.IsCancellationRequested) { _aborted = true; return 0; }
        if (_nodes >= _maxNodes || TimeUp()) { _aborted = true; return 0; }
        _nodes++;

        if (ChessModality.RuleWinner(b) is { } winner) return winner == (b.WhiteToMove ? 0 : 1) ? Mate - ply : -Mate + ply;
        if (ply > 0 && ((!b.Rules.Drops && b.HalfmoveClock >= 100)
                        || IsInsufficientMaterial(b)
                        || IsThreefoldCurrent(repetitionStart)))
            return DrawScoreAtPly(ply);

        bool inCheck = MoveGen.InCheck(b, b.WhiteToMove);
        if (!inCheck)
        {
            int standPat = Evaluation.Evaluate(b, _terms, _mgPst, _egPst)
                           + (_activePositionEvaluator?.Evaluate(b) ?? 0);
            if (_continuations is not null
                && _continuations.TryGet(ChessPositionIdentity.PositionId(b), out var seeded))
                standPat += seeded.LeafNudge(standPat);
            if (standPat >= beta) return beta;
            if (standPat > alpha) alpha = standPat;
        }

        var moves = LegalAt(b, ply);
        if (moves.Count == 0) return inCheck ? -(Mate - ply) : DrawScoreAtPly(ply);

        // In-place, order-preserving compaction — Quiesce runs at every
        // horizon node, and the old Where().ToList() allocated a closure, an
        // iterator, and a list per node (millions per move at the node cap).
        var considered = moves;
        if (!inCheck)
        {
            int w = 0;
            for (int i = 0; i < moves.Count; i++)
                if (IsCaptureOrPromo(b, moves[i]))
                    moves[w++] = moves[i];
            moves.RemoveRange(w, moves.Count - w);
            if (moves.Count == 0) return alpha;
        }

        OrderCaptures(b, considered);
        foreach (var m in considered)
        {
            bool resetRepetition = ResetsRepetition(b, m);
            var undo = MoveApply.MakeWithUndo(b, m);
            Hash128 childId = ChessPositionIdentity.PositionId(b);
            _repetitionHistory.Add(childId);
            int childRepetitionStart = resetRepetition
                ? _repetitionHistory.Count - 1
                : repetitionStart;
            ulong childRepetitionSignature = resetRepetition
                ? RepetitionAppend(0, childId)
                : RepetitionAppend(repetitionSignature, childId);

            int score = -Quiesce(
                b, -beta, -alpha, ply + 1,
                childRepetitionStart, childRepetitionSignature);

            _repetitionHistory.RemoveAt(_repetitionHistory.Count - 1);
            MoveApply.Unmake(b, m, undo);
            if (_aborted) return 0;
            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }
        return alpha;
    }

    private bool IsThreefoldCurrent(int repetitionStart)
        => CountCurrent(_repetitionHistory, repetitionStart) >= 3;

    private static int CountCurrent(IReadOnlyList<Hash128> history, int repetitionStart)
    {
        if (history.Count == 0) return 0;
        Hash128 current = history[^1];
        int count = 0;
        for (int i = Math.Max(0, repetitionStart); i < history.Count; i++)
            if (history[i] == current && ++count >= 3) return count;
        return count;
    }

    private static bool ResetsRepetition(Board b, ChessMove move)
    {
        Piece moving = b.MovingPiece(move);
        bool pawn = Board.TypeOf(moving) == Piece.WPawn;
        bool capture = b.Squares[move.To] != Piece.Empty
                       || (move.Flags & MoveFlags.EnPassant) != 0;
        return !b.Rules.Drops && (pawn || capture);
    }

    // The TT value of a board can depend on how many times repetition-relevant positions have
    // already occurred. Fold the ordered reversible-history segment into the TT key instead of
    // reusing one board score across incompatible played histories.
    private static ulong RepetitionSignature(IReadOnlyList<Hash128> history, int start)
    {
        ulong sig = 0;
        for (int i = Math.Max(0, start); i < history.Count; i++)
            sig = RepetitionAppend(sig, history[i]);
        return sig;
    }

    private static ulong RepetitionAppend(ulong sig, Hash128 id)
    {
        ulong x = id.Hi ^ RotateLeft(id.Lo, 23) ^ 0x9E3779B97F4A7C15UL;
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return RotateLeft(sig, 11) ^ x;
    }

    private static ulong TtKey(Board b, ulong repetitionSignature)
        => Zobrist.Hash(b) ^ RotateLeft(repetitionSignature, 17);

    private static ulong RotateLeft(ulong value, int count)
        => (value << count) | (value >> (64 - count));

    // Internal: See (static exchange evaluation) and the motif detectors read this same
    // table — the engine's one piece-value fact (one implementation per fact).
    internal static readonly int[] PieceValue = { 0, 100, 320, 330, 500, 900, 20000 };

    private void Order(Board b, List<ChessMove> moves, ChessMove ttMove, int ply, ChessMove? preferred = null)
    {
        var k0 = ply < MaxPly ? _killers[ply, 0] : default;
        var k1 = ply < MaxPly ? _killers[ply, 1] : default;
        moves.Sort((x, y) => OrderScore(b, y, ttMove, k0, k1, ply, preferred)
            .CompareTo(OrderScore(b, x, ttMove, k0, k1, ply, preferred)));
    }

    private int OrderScore(
        Board b, ChessMove move, ChessMove ttMove, ChessMove k0, ChessMove k1, int ply,
        ChessMove? preferred)
    {
        int score = Score(b, move, ttMove, k0, k1);
        // Below a transposition move, above a capture: the cached continuation is tried early
        // and a proven table move can still lead.
        if (preferred is { } hint && move == hint) score += 900_000;
        if (ply == 0 && _rootBonusByUci?.TryGetValue(move.ToUci(), out int bonus) == true)
            score += bonus * 1_000;
        return score;
    }

    private static int Score(Board b, ChessMove m, ChessMove ttMove, ChessMove k0, ChessMove k1)
    {
        if (m == ttMove) return 1_000_000;
        var victim = b.Squares[m.To];
        if (victim != Piece.Empty)
            return 100_000 + PieceValue[Math.Abs((sbyte)victim)] * 10 - PieceValue[Math.Abs((sbyte)b.MovingPiece(m))];
        if ((m.Flags & MoveFlags.EnPassant) != 0) return 100_000 + 100 * 10 - 100;
        if ((m.Flags & MoveFlags.Promotion) != 0) return 90_000 + PieceValue[Math.Abs((sbyte)m.Promotion)];
        if (m == k0 || m == k1) return 80_000;
        return 0;
    }

    private static void OrderCaptures(Board b, List<ChessMove> caps)
        => caps.Sort((x, y) => Mvv(b, y).CompareTo(Mvv(b, x)));

    private static int Mvv(Board b, ChessMove m)
    {
        var victim = b.Squares[m.To];
        int v = victim != Piece.Empty ? PieceValue[Math.Abs((sbyte)victim)] : 100;
        return v * 10 - PieceValue[Math.Abs((sbyte)b.MovingPiece(m))];
    }

    private void RecordKiller(Board b, ChessMove m, int ply)
    {
        if (ply >= MaxPly) return;
        if (b.Squares[m.To] != Piece.Empty || (m.Flags & MoveFlags.EnPassant) != 0) return;
        if (_killers[ply, 0] == m) return;
        _killers[ply, 1] = _killers[ply, 0];
        _killers[ply, 0] = m;
    }

    private void ClearKillers() => Array.Clear(_killers, 0, _killers.Length);

    private static bool IsCaptureOrPromo(Board b, ChessMove m)
        => b.Squares[m.To] != Piece.Empty || (m.Flags & (MoveFlags.EnPassant | MoveFlags.Promotion)) != 0;

    private static bool IsInsufficientMaterial(Board b)
    {
        if (!b.Rules.IsStandard) return ChessModality.IsInsufficientMaterial(b);
        int whiteKnights = 0, whiteBishops = 0, blackKnights = 0, blackBishops = 0;
        bool whiteBishopOnLight = false, whiteBishopOnDark = false;
        bool blackBishopOnLight = false, blackBishopOnDark = false;

        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            Piece p = b.Squares[sq];
            if (p == Piece.Empty) continue;
            switch (Board.TypeOf(p))
            {
                case Piece.WPawn:
                case Piece.WRook:
                case Piece.WQueen:
                    return false;
                case Piece.WKnight:
                    if (Board.IsWhite(p)) whiteKnights++; else blackKnights++;
                    break;
                case Piece.WBishop:
                    bool light = ((Board.FileOf(sq) + Board.RankOf(sq)) & 1) == 1;
                    if (Board.IsWhite(p))
                    {
                        whiteBishops++;
                        if (light) whiteBishopOnLight = true; else whiteBishopOnDark = true;
                    }
                    else
                    {
                        blackBishops++;
                        if (light) blackBishopOnLight = true; else blackBishopOnDark = true;
                    }
                    break;
                case Piece.WKing:
                    break;
            }
        }

        int whiteMinors = whiteKnights + whiteBishops;
        int blackMinors = blackKnights + blackBishops;
        if (whiteMinors == 0 && blackMinors == 0) return true;
        if (whiteMinors == 1 && blackMinors == 0) return true;
        if (blackMinors == 1 && whiteMinors == 0) return true;
        if (whiteKnights == 0 && blackKnights == 0 && whiteBishops >= 1 && blackBishops >= 1)
        {
            bool anyLight = whiteBishopOnLight || blackBishopOnLight;
            bool anyDark = whiteBishopOnDark || blackBishopOnDark;
            if (!(anyLight && anyDark)) return true;
        }
        return false;
    }
}
