using System.Numerics;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

public static partial class ChessRetrograde
{
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
        return UnrollCausalConstellations(boards, moves, maxLookbackPlies);
    }

    /// <summary>
    /// Walks the causal mating cone backwards over an already-replayed board and move sequence.
    /// </summary>
    public static IReadOnlyList<CausalConstellation> UnrollCausalConstellations(
        IReadOnlyList<Board> boards,
        IReadOnlyList<ChessMove> moves,
        int maxLookbackPlies = 6)
    {
        ArgumentNullException.ThrowIfNull(boards);
        ArgumentNullException.ThrowIfNull(moves);
        int n = moves.Count;
        if (boards.Count != n + 1 || n == 0)
            return [];
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
    /// Canonical surface for a causal mating constellation, ordered by square so it
    /// realizes directly in SQL via <c>realize.batch</c>.
    /// </summary>
    public static string Surface(CausalConstellation constellation)
    {
        if (constellation.Atoms is null || constellation.Atoms.Count == 0)
            return "chess/mating-kernel/empty";
        var ordered = constellation.Atoms
            .OrderBy(static a => a.Square0x88)
            .ThenBy(static a => (int)a.Piece);
        var sb = new StringBuilder("chess/mating-kernel/");
        bool first = true;
        foreach (var (piece, sq0x88, _) in ordered)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(PieceSymbol(piece));
            sb.Append('@');
            sb.Append(Board.SquareToAlgebraic(sq0x88));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Deposits retrograde causal mating kernels and their distance-to-mate (<c>HAS_DTZ</c>)
    /// and <c>OUTCOME</c> testimony when a replayed game ends in checkmate.
    /// </summary>
    public static void AppendGame(
        SubstrateChangeBuilder b,
        Hash128 lineId,
        IReadOnlyList<Board> boards,
        IReadOnlyList<ChessMove> moves,
        GameOutcome result,
        Hash128 playingId,
        Hash128 analysisSourceId,
        Hash128 outcomeSourceId,
        double witnessWeight = 0.9,
        int maxLookbackPlies = 4)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(boards);
        ArgumentNullException.ThrowIfNull(moves);
        if (boards.Count == 0 || moves.Count == 0 || boards.Count != moves.Count + 1)
            return;

        var constellations = UnrollCausalConstellations(boards, moves, maxLookbackPlies);
        if (constellations.Count == 0) return;

        // The side that delivered checkmate is the mover of the final ply.
        int winnerSide = boards[^1].WhiteToMove ? 1 : 0;
        long score = ChessGraph.ScoreFp1e9(result.ForMover(winnerSide));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in constellations)
        {
            string surface = Surface(c);
            if (!seen.Add(surface)) continue;
            if (ContentEmitter.Emit(b, surface, outcomeSourceId) is not { } kernelId)
                continue;

            b.AddAttestation(NativeAttestation.Aggregated(
                subject: kernelId,
                typeId: ChessVocabulary.OutcomeType,
                obj: ChessVocabulary.OutcomeObject,
                sourceId: outcomeSourceId,
                contextId: playingId,
                games: 1,
                sumScoreFp1e9: score,
                witnessWeight: witnessWeight));

            if (ContentEmitter.Emit(
                    b,
                    c.PliesToMate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    analysisSourceId) is { } dtzId)
            {
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    kernelId, ChessVocabulary.HasDtzType, dtzId,
                    analysisSourceId, playingId, witnessWeight));
            }

            b.AddAttestation(NativeAttestation.CategoricalResolved(
                lineId, ChessVocabulary.FeatureType, kernelId,
                analysisSourceId, playingId, witnessWeight));
        }
    }

    private static char PieceSymbol(Piece p) => p switch
    {
        Piece.WPawn => 'P',
        Piece.WKnight => 'N',
        Piece.WBishop => 'B',
        Piece.WRook => 'R',
        Piece.WQueen => 'Q',
        Piece.WKing => 'K',
        Piece.BPawn => 'p',
        Piece.BKnight => 'n',
        Piece.BBishop => 'b',
        Piece.BRook => 'r',
        Piece.BQueen => 'q',
        Piece.BKing => 'k',
        _ => '?',
    };

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
