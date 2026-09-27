using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Modality.Tests;

/// <summary>
/// The table-driven attack test agrees with the mailbox ray walk on every square of every
/// position, for both colours. Positions are the perft suite plus endgame and Chess960 shapes:
/// file wrapping shows on a/h-file pieces, blocker handling in dense middlegames, and long
/// empty rays in endgames.
/// </summary>
public class MoveGenBitboardEquivalenceTests
{
    // The standard perft positions: they exercise castling, en passant, promotion and pins.
    private const string Startpos = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    private const string Pos3 = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    private const string Pos4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
    private const string Pos5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";
    private const string Pos6 = "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10";
    // Sparse endgames: long unobstructed rays, where an off-by-one in the mask shows up loudly.
    private const string Endgame1 = "8/8/8/3k4/8/8/3K4/7Q w - - 0 1";
    private const string Endgame2 = "8/1p6/8/8/8/8/6P1/K6k w - - 0 1";
    // a/h-file heavy: the file-wrap case for knights, kings and pawns.
    private const string EdgeFiles = "k6K/p6p/N6n/8/8/n6N/P6P/8 w - - 0 1";

    public static TheoryData<string> Positions => new()
    {
        Startpos, Kiwipete, Pos3, Pos4, Pos5, Pos6, Endgame1, Endgame2, EdgeFiles,
    };

    [Theory]
    [MemberData(nameof(Positions))]
    public void BitboardAttackTest_MatchesMailboxWalk_EverySquareBothColours(string fen)
    {
        var b = Board.FromFen(fen);
        var bb = Bitboards.FromBoard(b);

        for (int sq0x88 = 0; sq0x88 < 128; sq0x88++)
        {
            if ((sq0x88 & 0x88) != 0) { sq0x88 += 7; continue; }
            int bit = Bitboards.Bit(sq0x88);

            foreach (bool byWhite in new[] { true, false })
            {
                bool mailbox = MoveGen.IsSquareAttacked(b, sq0x88, byWhite);
                bool table = MoveGen.IsSquareAttackedBB(bb, bit, byWhite);
                Assert.True(mailbox == table,
                    $"fen={fen} square0x88={sq0x88} bit={bit} byWhite={byWhite}: " +
                    $"mailbox={mailbox} table={table}");
            }
        }
    }

    /// <summary>
    /// Walks the game tree a few plies deep and re-checks after every make, covering
    /// occupancies that arise from play (captures, promotions, castles), not only fixed FENs.
    /// </summary>
    [Theory]
    [InlineData(Startpos, 3)]
    [InlineData(Kiwipete, 2)]
    [InlineData(Pos4, 2)]
    public void BitboardAttackTest_MatchesMailboxWalk_ThroughRealPlay(string fen, int depth)
    {
        var b = Board.FromFen(fen);
        Walk(b, depth);
    }

    private static void Walk(Board b, int depth)
    {
        AssertAgrees(b);
        if (depth == 0) return;

        var pseudo = new List<ChessMove>(64);
        var legal = new List<ChessMove>(64);
        MoveGen.Legal(b, pseudo, legal);

        foreach (var m in legal)
        {
            var undo = MoveApply.MakeWithUndo(b, m);
            Walk(b, depth - 1);
            MoveApply.Unmake(b, m, undo);
        }
    }

    private static void AssertAgrees(Board b)
    {
        var bb = Bitboards.FromBoard(b);
        for (int sq0x88 = 0; sq0x88 < 128; sq0x88++)
        {
            if ((sq0x88 & 0x88) != 0) { sq0x88 += 7; continue; }
            int bit = Bitboards.Bit(sq0x88);
            foreach (bool byWhite in new[] { true, false })
            {
                Assert.Equal(
                    MoveGen.IsSquareAttacked(b, sq0x88, byWhite),
                    MoveGen.IsSquareAttackedBB(bb, bit, byWhite));
            }
        }
    }
}
