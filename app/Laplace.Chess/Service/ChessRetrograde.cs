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
    /// Minimal causal sub-board constellation of <c>piece×square</c> atoms at
    /// <see cref="ChessCompose.SegmentTier"/> that causes or forces a checkmate.
    /// </summary>
    public readonly record struct CausalConstellation(
        Hash128 ConstellationId,
        IReadOnlyList<(Piece Piece, int Square0x88, Hash128 AtomId)> Atoms,
        double[] Coord,
        int PliesToMate,
        ChessMove? NextMove);

    /// <summary>
    /// Result of projecting a &gt;5-piece board onto a local &lt;=5-piece sub-board and
    /// probing the Syzygy tablebase.
    /// </summary>
    public readonly record struct SubBoardSyzygyProjection(
        Hash128 ConstellationId,
        Board SubBoard,
        int Men,
        int Wdl,
        int Dtz,
        ChessMove? BestMove);

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

    /// <summary>
    /// Extracts the minimal causal <c>piece×square</c> kernel of a checkmated board:
    /// the mated king, the checking piece(s), any attacker defending a contact checker,
    /// friendly pieces blocking the king's flight squares, and enemy pieces controlling
    /// the king's flight squares. Bystander pieces elsewhere on the board are excluded.
    /// </summary>
    public static CausalConstellation? ExtractMatingKernel(Board board)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (MoveGen.Legal(board).Count != 0 || !MoveGen.InCheck(board, board.WhiteToMove))
            return null;

        bool defenderWhite = board.WhiteToMove;
        bool attackerWhite = !defenderWhite;
        Piece defenderKing = defenderWhite ? Piece.WKing : Piece.BKing;
        ulong kingBb = board.PieceBB(defenderKing);
        if (kingBb == 0) return null;

        int kingBit = BitOperations.TrailingZeroCount(kingBb);
        int kingSq = BitTo0x88(kingBit);
        ulong kernelBits = 1UL << kingBit;

        ulong occ = board.OccupiedBB;
        ulong checkers = AttackersOfBit(board, kingBit, occ, attackerWhite);
        kernelBits |= checkers;

        // Any attacker defending a checking piece that the king could otherwise capture.
        ulong remCheckers = checkers;
        while (remCheckers != 0)
        {
            int cBit = BitOperations.TrailingZeroCount(remCheckers);
            remCheckers &= remCheckers - 1;
            kernelBits |= AttackersOfBit(board, cBit, occ, attackerWhite);
        }

        // Flight squares of the mated king: friendly blockers + enemy controllers when king steps out.
        ulong occNoKing = occ & ~(1UL << kingBit);
        foreach (int delta in MoveGen.KingDeltas)
        {
            int flightSq = kingSq + delta;
            if (!Board.OnBoard(flightSq)) continue;
            int flightBit = (Board.RankOf(flightSq) << 3) | Board.FileOf(flightSq);
            Piece occPiece = board.Squares[flightSq];
            if (occPiece != Piece.Empty && Board.IsWhite(occPiece) == defenderWhite)
            {
                // Friendly piece blocking the king's escape square.
                kernelBits |= 1UL << flightBit;
            }
            else
            {
                // Attacker piece(s) controlling this escape square.
                ulong flightOcc = (occNoKing & ~(1UL << flightBit)) | (1UL << flightBit);
                kernelBits |= AttackersOfBit(board, flightBit, flightOcc, attackerWhite);
            }
        }

        return ComposeConstellation(board, kernelBits, pliesToMate: 0, nextMove: null);
    }

    /// <summary>
    /// Replays a game ending in checkmate and walks its causal cone backwards up to
    /// <paramref name="maxLookbackPlies"/> plies, producing content-addressed
    /// <see cref="ChessCompose.SegmentTier"/> constellations at each ply before mate.
    /// </summary>
    public static IReadOnlyList<CausalConstellation> UnrollCausalConstellations(
        IReadOnlyList<string> sans,
        string? startFen = null,
        int maxLookbackPlies = 6)
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
        if (ExtractMatingKernel(boards[n]) is not { } terminalKernel)
            return [];

        ulong trackedSquaresMask = 0;
        foreach (var (_, sq0x88, _) in terminalKernel.Atoms)
            trackedSquaresMask |= 1UL << ((Board.RankOf(sq0x88) << 3) | Board.FileOf(sq0x88));

        var list = new List<CausalConstellation>(Math.Min(n + 1, maxLookbackPlies + 1))
        {
            terminalKernel,
        };

        int stopPly = Math.Max(0, n - Math.Max(0, maxLookbackPlies));
        for (int ply = n - 1; ply >= stopPly; ply--)
        {
            ChessMove move = moves[ply];
            int fromBit = (Board.RankOf(move.From) << 3) | Board.FileOf(move.From);
            int toBit = (Board.RankOf(move.To) << 3) | Board.FileOf(move.To);

            // Pull the destination square back to its origin square at ply, and keep any
            // piece that was captured on toBit if it belonged to the causal cone.
            if ((trackedSquaresMask & (1UL << toBit)) != 0)
            {
                trackedSquaresMask &= ~(1UL << toBit);
                trackedSquaresMask |= 1UL << fromBit;
                if (boards[ply].Squares[move.To] != Piece.Empty)
                    trackedSquaresMask |= 1UL << toBit;
            }
            else
            {
                trackedSquaresMask |= 1UL << fromBit;
            }

            if (ComposeConstellation(boards[ply], trackedSquaresMask, n - ply, move) is { } prior)
                list.Add(prior);
        }

        list.Reverse();
        return list;
    }

    /// <summary>
    /// True when <paramref name="board"/> contains every <c>(Piece, Square)</c> atom of
    /// <paramref name="constellation"/>, regardless of any additional pieces on the board.
    /// </summary>
    public static bool BoardContainsConstellation(Board board, CausalConstellation constellation)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (constellation.Atoms is null || constellation.Atoms.Count == 0) return false;
        foreach (var (piece, sq0x88, _) in constellation.Atoms)
        {
            if (board.Squares[sq0x88] != piece)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Projects a &gt;5-piece board onto its highest-urgency &lt;=<paramref name="maxMen"/>-piece
    /// sub-board (retaining both kings plus the attackers/defenders closest to the enemy king
    /// and advanced passed pawns) and probes <paramref name="prober"/> to detect underlying
    /// Syzygy tablebase wins even when bystander pieces remain on the board.
    /// </summary>
    public static SubBoardSyzygyProjection? ProjectSubBoardForSyzygy(
        Board board,
        ISyzygyProber prober,
        int maxMen = ChessSyzygy.WitnessedMenCeiling)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(prober);
        int cap = Math.Min(maxMen, prober.Largest);
        if (cap < 3) return null;

        ulong wKingBb = board.PieceBB(Piece.WKing);
        ulong bKingBb = board.PieceBB(Piece.BKing);
        if (wKingBb == 0 || bKingBb == 0) return null;

        int wKingBit = BitOperations.TrailingZeroCount(wKingBb);
        int bKingBit = BitOperations.TrailingZeroCount(bKingBb);
        int wKingSq = BitTo0x88(wKingBit);
        int bKingSq = BitTo0x88(bKingBit);
        int targetKingSq = board.WhiteToMove ? bKingSq : wKingSq;

        var candidates = new List<(int Sq0x88, Piece Piece, int Score)>(16);
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            Piece p = board.Squares[sq];
            if (p is Piece.Empty or Piece.WKing or Piece.BKing) continue;
            bool isMover = Board.IsWhite(p) == board.WhiteToMove;
            int chebyshev = Math.Max(
                Math.Abs(Board.FileOf(sq) - Board.FileOf(targetKingSq)),
                Math.Abs(Board.RankOf(sq) - Board.RankOf(targetKingSq)));
            int mat = Board.TypeOf(p) switch
            {
                Piece.WQueen => 900,
                Piece.WRook => 500,
                Piece.WBishop => 330,
                Piece.WKnight => 320,
                Piece.WPawn => 150 + (Board.IsWhite(p) ? Board.RankOf(sq) * 30 : (7 - Board.RankOf(sq)) * 30),
                _ => 0,
            };
            int urgency = (isMover ? 1_000 : 400) + mat - chebyshev * 60;
            candidates.Add((sq, p, urgency));
        }

        candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        var sub = new Board
        {
            WhiteToMove = board.WhiteToMove,
            Castle = CastleRights.None,
            EpSquare = -1,
            HalfmoveClock = 0,
            FullmoveNumber = board.FullmoveNumber,
            Rules = ChessVariantRules.Standard,
        };
        sub.Set(wKingSq, Piece.WKing);
        sub.Set(bKingSq, Piece.BKing);

        ulong mask = (1UL << wKingBit) | (1UL << bKingBit);
        int men = 2;
        for (int i = 0; i < candidates.Count && men < cap; i++)
        {
            var (sq, p, _) = candidates[i];
            sub.Set(sq, p);
            // If adding this piece puts the side NOT to move in illegal check, remove it.
            if (MoveGen.InCheck(sub, !sub.WhiteToMove))
            {
                sub.Set(sq, Piece.Empty);
                continue;
            }
            mask |= 1UL << ((Board.RankOf(sq) << 3) | Board.FileOf(sq));
            men++;
        }

        if (men < 3 || MoveGen.InCheck(sub, !sub.WhiteToMove))
            return null;
        if (prober.Probe(sub) is not { } verdict)
            return null;

        ChessMove? bestMove = ResolveSyzygyMove(sub, verdict);
        if (bestMove is { } mv && !MoveGen.Legal(board).Contains(mv))
            bestMove = null;

        var composed = ComposeConstellation(sub, mask, Math.Abs(verdict.Dtz), bestMove);
        if (composed is not { } c) return null;

        return new SubBoardSyzygyProjection(
            c.ConstellationId,
            sub,
            men,
            verdict.Wdl,
            verdict.Dtz,
            bestMove);
    }

    private static CausalConstellation? ComposeConstellation(
        Board board, ulong squareMask, int pliesToMate, ChessMove? nextMove)
    {
        var atoms = new List<(Piece Piece, int Square0x88, ChessNode Node)>(BitOperations.PopCount(squareMask));
        ulong rem = squareMask;
        while (rem != 0)
        {
            int bit = BitOperations.TrailingZeroCount(rem);
            rem &= rem - 1;
            int sq = BitTo0x88(bit);
            Piece p = board.Squares[sq];
            if (p == Piece.Empty) continue;
            var node = ChessCompose.PieceSquareNode(p, Board.FileOf(sq), Board.RankOf(sq));
            atoms.Add((p, sq, node));
        }

        if (atoms.Count == 0) return null;
        atoms.Sort(static (a, b) => a.Node.Id.CompareToBytewise(b.Node.Id));

        var ids = new Hash128[atoms.Count];
        var coords = new double[atoms.Count * 4];
        var publicAtoms = new (Piece Piece, int Square0x88, Hash128 AtomId)[atoms.Count];
        for (int i = 0; i < atoms.Count; i++)
        {
            ids[i] = atoms[i].Node.Id;
            atoms[i].Node.Coord.CopyTo(coords, i * 4);
            publicAtoms[i] = (atoms[i].Piece, atoms[i].Square0x88, atoms[i].Node.Id);
        }

        Hash128 id = Hash128.Merkle(ChessCompose.SegmentTier, ids);
        double[] centroid = Math4d.KarcherMean(coords);
        return new CausalConstellation(id, publicAtoms, centroid, pliesToMate, nextMove);
    }

    private static ulong AttackersOfBit(Board b, int targetBit, ulong occ, bool byWhite)
    {
        ulong pawns = b.PieceBB(byWhite ? Piece.WPawn : Piece.BPawn);
        ulong knights = b.PieceBB(byWhite ? Piece.WKnight : Piece.BKnight);
        ulong kings = b.PieceBB(byWhite ? Piece.WKing : Piece.BKing);
        ulong bishops = b.PieceBB(byWhite ? Piece.WBishop : Piece.BBishop);
        ulong rooks = b.PieceBB(byWhite ? Piece.WRook : Piece.BRook);
        ulong queens = b.PieceBB(byWhite ? Piece.WQueen : Piece.BQueen);

        return (ChessAttacks.Pawn(targetBit, !byWhite) & pawns)
             | (ChessAttacks.Knight(targetBit) & knights)
             | (ChessAttacks.King(targetBit) & kings)
             | (ChessAttacks.Bishop(targetBit, occ) & (bishops | queens))
             | (ChessAttacks.Rook(targetBit, occ) & (rooks | queens));
    }

    private static int BitTo0x88(int bit64) => Board.Sq(bit64 & 7, bit64 >> 3);

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
