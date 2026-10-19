using System.Numerics;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// Backward (retrograde) chess analysis over witnessed trajectories and tablebase
/// boundaries. Unlike forward-only cellular automata where predecessors are unknown,
/// Laplace records ordered position projections and transitions, allowing terminal
/// checkmates and 3-4-5 Syzygy verdicts to propagate backwards into >5-piece positions
/// and extract high-conversion structural checkpoint rules.
/// </summary>
public static class ChessRetrograde
{
    public readonly record struct RetrogradeCheckpoint(
        Hash128 PositionId,
        Board Board,
        int Ply,
        int Men,
        ChessMove? BestMove,
        int Wdl,
        int PliesToClose,
        bool IsCheckmate);

    public readonly record struct WinningConditionRule(
        string Condition,
        int Games,
        int Wins,
        int Draws,
        int Losses,
        double EmpiricalWinRate,
        double ShrunkWinRate,
        int? FastestCheckmatePlies,
        double AveragePliesToFinish);

    /// <summary>
    /// Replays a game line and walks it backwards from the terminal state (or from the
    /// first tablebase-covered endgame state), propagating exact checkmate distance and
    /// Syzygy WDL/DTZ into earlier positions even when more than five pieces remain.
    /// </summary>
    public static IReadOnlyList<RetrogradeCheckpoint> UnrollLine(
        IReadOnlyList<string> sans,
        string? startFen = null,
        ISyzygyProber? prober = null)
    {
        ArgumentNullException.ThrowIfNull(sans);
        var modality = new ChessModality();
        if (ChessAnalyze.InitialState(startFen, modality) is not { } started)
            return [];

        var state = started.Initial;
        var boards = new List<Board>(sans.Count + 1) { state.Board.Clone() };
        var moves = new List<ChessMove>(sans.Count);
        foreach (string san in sans)
        {
            var mv = San.Resolve(state.Board, modality.LegalActions(state), san);
            if (mv is null) break;
            moves.Add(mv.Value);
            state = modality.Apply(state, mv.Value);
            boards.Add(state.Board.Clone());
        }

        int n = moves.Count;
        var checkpoints = new RetrogradeCheckpoint?[n + 1];
        int maxTbMen = prober is null ? 0 : Math.Min(ChessSyzygy.WitnessedMenCeiling, prober.Largest);

        // 1. Check terminal checkmate at the end of the line (any piece count).
        Board last = boards[n];
        var terminalLegal = MoveGen.Legal(last);
        if (terminalLegal.Count == 0 && MoveGen.InCheck(last, last.WhiteToMove))
        {
            checkpoints[n] = new RetrogradeCheckpoint(
                ChessPositionIdentity.PositionId(last),
                last,
                Ply: n,
                Men: Men(last),
                BestMove: null,
                Wdl: 0,
                PliesToClose: 0,
                IsCheckmate: true);
        }

        // 2. Walk backwards from ply n down to 0, propagating checkmate or Syzygy closure
        // into >5-piece predecessors and probing 1-ply capture closures from 6-man boards.
        for (int ply = n; ply >= 0; ply--)
        {
            Board cur = boards[ply];
            int men = Men(cur);

            // Propagate from ply + 1 if the child is already retrograde-solved.
            if (ply < n && checkpoints[ply + 1] is { } next)
            {
                int parentWdl = InvertWdl(next.Wdl);
                checkpoints[ply] = new RetrogradeCheckpoint(
                    ChessPositionIdentity.PositionId(cur),
                    cur,
                    Ply: ply,
                    Men: men,
                    BestMove: moves[ply],
                    Wdl: parentWdl,
                    PliesToClose: next.PliesToClose + 1,
                    IsCheckmate: next.IsCheckmate);
            }

            if (prober is null || !cur.Rules.IsStandard || cur.Castle != CastleRights.None)
                continue;

            // Direct Syzygy probe when <= maxTbMen.
            if (men >= 2 && men <= maxTbMen && prober.Probe(cur) is { } direct)
            {
                ChessMove? tbMove = ResolveSyzygyMove(cur, direct);
                var directCheckpoint = new RetrogradeCheckpoint(
                    ChessPositionIdentity.PositionId(cur),
                    cur,
                    Ply: ply,
                    Men: men,
                    BestMove: tbMove ?? (ply < n ? moves[ply] : null),
                    Wdl: direct.Wdl,
                    PliesToClose: Math.Abs(direct.Dtz),
                    IsCheckmate: false);
                checkpoints[ply] = SelectBetter(checkpoints[ply], directCheckpoint);
            }
            // One-ply retrograde Syzygy detection on (maxTbMen + 1)-piece positions (e.g. 6 men):
            // if a legal capture or promotion drops into a winning <=5-man tablebase state,
            // this >5-man position is already a proven Syzygy win.
            else if (men == maxTbMen + 1
                     && ProbeOnePlySyzygyClosure(cur, prober, maxTbMen) is { } closure)
            {
                var closureCheckpoint = new RetrogradeCheckpoint(
                    ChessPositionIdentity.PositionId(cur),
                    cur,
                    Ply: ply,
                    Men: men,
                    BestMove: closure.Move,
                    Wdl: closure.Wdl,
                    PliesToClose: closure.PliesToClose,
                    IsCheckmate: false);
                checkpoints[ply] = SelectBetter(checkpoints[ply], closureCheckpoint);
            }
        }

        // 3. Second backward pass so a newly discovered Syzygy closure at ply k propagates
        // all the way back to ply 0..k-1.
        for (int ply = n - 1; ply >= 0; ply--)
        {
            if (checkpoints[ply + 1] is not { } next) continue;
            Board cur = boards[ply];
            var propagated = new RetrogradeCheckpoint(
                ChessPositionIdentity.PositionId(cur),
                cur,
                Ply: ply,
                Men: Men(cur),
                BestMove: moves[ply],
                Wdl: InvertWdl(next.Wdl),
                PliesToClose: next.PliesToClose + 1,
                IsCheckmate: next.IsCheckmate);
            checkpoints[ply] = SelectBetter(checkpoints[ply], propagated);
        }

        var result = new List<RetrogradeCheckpoint>(n + 1);
        for (int ply = 0; ply <= n; ply++)
            if (checkpoints[ply] is { } cp)
                result.Add(cp);
        return result;
    }

    /// <summary>
    /// Unrolls a line backwards and seeds every retrograde checkpoint into
    /// <paramref name="table"/> so search selects the fastest checkmate or tablebase
    /// conversion in O(1) at interior nodes.
    /// </summary>
    public static int SeedSearchContinuations(
        SearchContinuationTable table,
        IReadOnlyList<string> sans,
        string? startFen = null,
        ISyzygyProber? prober = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var checkpoints = UnrollLine(sans, startFen, prober);
        foreach (var cp in checkpoints)
            table.SeedRetrogradeCheckpoint(cp.Board, cp.BestMove, cp.Wdl, cp.PliesToClose, cp.IsCheckmate);
        return checkpoints.Count;
    }

    /// <summary>
    /// Evaluates a >5-piece board for a 1-ply capture into a tablebase-solved state.
    /// </summary>
    public static (ChessMove Move, int Wdl, int PliesToClose)? ProbeOnePlySyzygyClosure(
        Board board, ISyzygyProber prober, int maxTbMen = ChessSyzygy.WitnessedMenCeiling)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(prober);
        if (!board.Rules.IsStandard || board.Castle != CastleRights.None) return null;

        (ChessMove Move, int Wdl, int PliesToClose)? best = null;
        foreach (var move in MoveGen.Legal(board))
        {
            bool captures = board.Squares[move.To] != Piece.Empty || (move.Flags & MoveFlags.EnPassant) != 0;
            if (!captures) continue;

            var next = board.Clone();
            MoveApply.Make(next, move);
            if (Men(next) > maxTbMen) continue;
            if (prober.Probe(next) is not { } childVerdict) continue;

            int parentWdl = InvertWdl(childVerdict.Wdl);
            int plies = Math.Abs(childVerdict.Dtz) + 1;
            if (parentWdl < 3) continue; // Only certify winning conversions on a single capture.
            if (best is null
                || parentWdl > best.Value.Wdl
                || (parentWdl == best.Value.Wdl && plies < best.Value.PliesToClose))
            {
                best = (move, parentWdl, plies);
            }
        }
        return best;
    }

    /// <summary>
    /// Walks a collection of games backwards from their outcomes and checkmates to extract
    /// single and conjunctive structural/tactical conditions whose empirical conversion
    /// rate meets <paramref name="minWinRate"/> (e.g. 0.70 for 7/10 games).
    /// Each game contributes at most one witness per condition per side.
    /// </summary>
    public static IReadOnlyList<WinningConditionRule> DiscoverWinningConditions(
        IEnumerable<(IReadOnlyList<string> Sans, GameOutcome Result, string? StartFen)> games,
        double minWinRate = 0.70,
        int minGames = 3)
    {
        ArgumentNullException.ThrowIfNull(games);
        var modality = new ChessModality();
        var stats = new Dictionary<string, (int Games, int Wins, int Draws, int Losses, int? FastestMate, long TotalPliesToFinish)>(
            StringComparer.Ordinal);

        foreach (var (sans, outcome, startFen) in games)
        {
            if (sans.Count == 0 || outcome.Winner is null && !outcome.IsDraw) continue;
            if (ChessAnalyze.InitialState(startFen, modality) is not { } started) continue;

            var state = started.Initial;
            var boards = new List<Board>(sans.Count + 1) { state.Board.Clone() };
            foreach (string san in sans)
            {
                var mv = San.Resolve(state.Board, modality.LegalActions(state), san);
                if (mv is null) break;
                state = modality.Apply(state, mv.Value);
                boards.Add(state.Board.Clone());
            }

            int totalPlies = boards.Count - 1;
            Board terminal = boards[^1];
            bool endedInMate = MoveGen.Legal(terminal).Count == 0
                               && MoveGen.InCheck(terminal, terminal.WhiteToMove);

            for (int side = 0; side <= 1; side++)
            {
                bool white = side == 0;
                var earliestPlyByCondition = new Dictionary<string, int>(StringComparer.Ordinal);

                // Walk backwards from the end of the game to find the earliest checkpoint
                // where each structural/tactical condition held for this side.
                for (int ply = totalPlies; ply >= 0; ply--)
                {
                    var active = ExtractConditions(boards[ply], white);
                    for (int i = 0; i < active.Count; i++)
                    {
                        earliestPlyByCondition[active[i]] = ply;
                        for (int j = i + 1; j < active.Count; j++)
                        {
                            string pair = string.CompareOrdinal(active[i], active[j]) < 0
                                ? $"{active[i]} + {active[j]}"
                                : $"{active[j]} + {active[i]}";
                            earliestPlyByCondition[pair] = ply;
                        }
                    }
                }

                PlyOutcome sideOutcome = outcome.ForMover(side);
                foreach (var (condition, firstPly) in earliestPlyByCondition)
                {
                    int pliesToFinish = Math.Max(1, totalPlies - firstPly);
                    var cur = stats.GetValueOrDefault(condition);
                    int wins = cur.Wins + (sideOutcome == PlyOutcome.Win ? 1 : 0);
                    int draws = cur.Draws + (sideOutcome == PlyOutcome.Draw ? 1 : 0);
                    int losses = cur.Losses + (sideOutcome == PlyOutcome.Loss ? 1 : 0);
                    int? fastestMate = cur.FastestMate;
                    if (sideOutcome == PlyOutcome.Win && endedInMate)
                        fastestMate = fastestMate is int prev ? Math.Min(prev, pliesToFinish) : pliesToFinish;

                    stats[condition] = (
                        cur.Games + 1,
                        wins,
                        draws,
                        losses,
                        fastestMate,
                        cur.TotalPliesToFinish + pliesToFinish);
                }
            }
        }

        var rules = new List<WinningConditionRule>();
        foreach (var (condition, s) in stats)
        {
            if (s.Games < minGames) continue;
            double empirical = (s.Wins + 0.5 * s.Draws) / s.Games;
            if (empirical < minWinRate) continue;
            double shrunk = 0.5 + (empirical - 0.5) * (s.Games / (s.Games + ChessShrink.ExactTransitionK0));
            rules.Add(new WinningConditionRule(
                condition,
                s.Games,
                s.Wins,
                s.Draws,
                s.Losses,
                empirical,
                shrunk,
                s.FastestMate,
                (double)s.TotalPliesToFinish / s.Games));
        }

        return rules
            .OrderBy(static r => r.FastestCheckmatePlies ?? int.MaxValue)
            .ThenByDescending(static r => r.ShrunkWinRate)
            .ThenByDescending(static r => r.Games)
            .ThenBy(static r => r.Condition, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Structural and tactical conditions active for <paramref name="white"/> on <paramref name="board"/>.
    /// </summary>
    public static IReadOnlyList<string> ExtractConditions(Board board, bool white)
    {
        ArgumentNullException.ThrowIfNull(board);
        var conditions = new List<string>(8);
        ulong ownPawns = 0, enemyPawns = 0;
        int ownBishops = 0, enemyBishops = 0;
        int ownMaterial = 0, enemyMaterial = 0;
        int ownRooksOpen = 0;
        int ownPassers = 0;

        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            Piece p = board.Squares[sq];
            if (p == Piece.Empty) continue;
            bool isOwn = Board.IsWhite(p) == white;
            Piece type = Board.TypeOf(p);
            int bit = (Board.RankOf(sq) << 3) | Board.FileOf(sq);
            int val = type switch
            {
                Piece.WPawn => 100,
                Piece.WKnight => 320,
                Piece.WBishop => 330,
                Piece.WRook => 500,
                Piece.WQueen => 900,
                _ => 0,
            };
            if (isOwn) ownMaterial += val; else enemyMaterial += val;
            if (type == Piece.WPawn)
            {
                if (isOwn) ownPawns |= 1UL << bit;
                else enemyPawns |= 1UL << bit;
            }
            else if (type == Piece.WBishop)
            {
                if (isOwn) ownBishops++;
                else enemyBishops++;
            }
        }

        if (ownBishops >= 2 && enemyBishops < 2)
            conditions.Add("bishop-pair");
        if (ownMaterial - enemyMaterial >= 200)
            conditions.Add("material-advantage");

        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            Piece p = board.Squares[sq];
            if (p == Piece.Empty || Board.IsWhite(p) != white) continue;
            Piece type = Board.TypeOf(p);
            int file = Board.FileOf(sq);
            int rank = Board.RankOf(sq);
            if (type == Piece.WRook)
            {
                ulong fileMask = Bitboards.FileMask(file);
                if ((ownPawns & fileMask) == 0 && (enemyPawns & fileMask) == 0)
                    ownRooksOpen++;
            }
            else if (type == Piece.WPawn)
            {
                if (IsPassedPawn(file, rank, white, enemyPawns))
                    ownPassers++;
            }
        }

        if (ownRooksOpen > 0) conditions.Add("rook-open-file");
        if (ownPassers > 0) conditions.Add("passed-pawn");

        if (ChessTacticGeometry.Forks(board, white).Count > 0)
            conditions.Add("fork");
        foreach (var ps in ChessTacticGeometry.PinsAndSkewers(board, white))
        {
            string label = ps.Kind == TacticKind.Pin ? "pin" : "skewer";
            if (!conditions.Contains(label)) conditions.Add(label);
        }

        return conditions;
    }

    private static bool IsPassedPawn(int file, int rank, bool white, ulong enemyPawns)
    {
        ulong mask = Bitboards.FileMask(file);
        if (file > 0) mask |= Bitboards.FileMask(file - 1);
        if (file < 7) mask |= Bitboards.FileMask(file + 1);

        ulong ahead = 0;
        if (white)
        {
            for (int r = rank + 1; r < 8; r++) ahead |= 0xFFUL << (r * 8);
        }
        else
        {
            for (int r = 0; r < rank; r++) ahead |= 0xFFUL << (r * 8);
        }
        return (enemyPawns & mask & ahead) == 0;
    }

    private static int InvertWdl(int wdl) => Math.Clamp(4 - wdl, 0, 4);

    private static int Men(Board b) => BitOperations.PopCount(b.OccupiedBB);

    private static ChessMove? ResolveSyzygyMove(Board board, SyzygyVerdict verdict)
    {
        if (verdict.From < 0 || verdict.To < 0) return null;
        int fromSq = Board.Sq(verdict.From & 7, verdict.From >> 3);
        int toSq = Board.Sq(verdict.To & 7, verdict.To >> 3);
        foreach (var candidate in MoveGen.Legal(board))
        {
            if (candidate.From == fromSq && candidate.To == toSq)
                return candidate;
        }
        return null;
    }

    private static RetrogradeCheckpoint SelectBetter(
        RetrogradeCheckpoint? current, RetrogradeCheckpoint candidate)
    {
        if (current is not { } cur) return candidate;
        if (candidate.Wdl > cur.Wdl) return candidate;
        if (candidate.Wdl < cur.Wdl) return cur;
        if (candidate.IsCheckmate && !cur.IsCheckmate) return candidate;
        if (!candidate.IsCheckmate && cur.IsCheckmate) return cur;
        if (candidate.Wdl >= 3)
            return candidate.PliesToClose < cur.PliesToClose ? candidate : cur;
        return candidate.PliesToClose > cur.PliesToClose ? candidate : cur;
    }
}
