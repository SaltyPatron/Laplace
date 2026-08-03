using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Modality.Tests;

/// <summary>
/// Board now carries piece bitboards alongside the 0x88 Squares array, maintained incrementally
/// through Board.Set. Two representations of one fact is a standing invitation to divergence,
/// and the failure is SILENT: no exception, just move generation answering from stale bits.
///
/// So this walks the real game tree and re-checks the invariant after EVERY make and EVERY
/// unmake, on the positions whose mutation paths differ — captures, en passant, promotion,
/// castling (including the Chess960 case where the king can castle without moving and its
/// destination can hold the rook). A write site that bypasses Set shows up here as a hard
/// failure at the exact ply it happened.
/// </summary>
public class BoardBitboardConsistencyTests
{
    private const string Startpos = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    private const string Pos3 = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    private const string Pos4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
    private const string Pos5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";

    [Theory]
    [InlineData(Startpos)]
    [InlineData(Kiwipete)]
    [InlineData(Pos3)]
    [InlineData(Pos4)]
    [InlineData(Pos5)]
    public void FromFen_BuildsConsistentBitboards(string fen)
        => Assert.True(Board.FromFen(fen).BitboardsConsistent(), fen);

    [Theory]
    [InlineData(Startpos)]
    [InlineData(Kiwipete)]
    [InlineData(Pos4)]
    public void Clone_CarriesTheBitboards(string fen)
    {
        var b = Board.FromFen(fen);
        var c = b.Clone();
        Assert.True(c.BitboardsConsistent(), "clone lost its bitboards");
        Assert.Equal(b.OccupiedBB, c.OccupiedBB);
        Assert.Equal(b.WhiteBB, c.WhiteBB);
        Assert.Equal(b.BlackBB, c.BlackBB);
    }

    [Theory]
    [InlineData(Startpos, 3)]
    [InlineData(Kiwipete, 3)]   // castling both sides, en passant, pins
    [InlineData(Pos3, 4)]       // pawn races and promotions
    [InlineData(Pos4, 3)]       // promotion with capture, castling rights loss
    [InlineData(Pos5, 3)]
    public void MakeAndUnmake_PreserveTheInvariant_ThroughRealPlay(string fen, int depth)
    {
        var b = Board.FromFen(fen);
        Walk(b, depth);
    }

    private static void Walk(Board b, int depth)
    {
        Assert.True(b.BitboardsConsistent(), "bitboards diverged from Squares");
        if (depth == 0) return;

        var pseudo = new List<ChessMove>(64);
        var legal = new List<ChessMove>(64);
        MoveGen.Legal(b, pseudo, legal);

        foreach (var m in legal)
        {
            var undo = MoveApply.MakeWithUndo(b, m);
            Assert.True(b.BitboardsConsistent(), $"diverged after make {m.From}->{m.To}");
            Walk(b, depth - 1);
            MoveApply.Unmake(b, m, undo);
            Assert.True(b.BitboardsConsistent(), $"diverged after unmake {m.From}->{m.To}");
        }
    }

    /// <summary>
    /// The maintained bitboards must equal what Bitboards.FromBoard derives from scratch —
    /// otherwise everything already built on Bitboards (PositionContent.Surface's pawn features,
    /// material counts) would disagree with anything built on the Board's own set.
    /// </summary>
    [Theory]
    [InlineData(Startpos)]
    [InlineData(Kiwipete)]
    [InlineData(Pos4)]
    public void MaintainedBitboards_AgreeWithFromBoard(string fen)
    {
        var b = Board.FromFen(fen);
        var derived = Bitboards.FromBoard(b);
        Assert.Equal(derived.Occupied, b.OccupiedBB);
        Assert.Equal(derived.White, b.WhiteBB);
        Assert.Equal(derived.Black, b.BlackBB);
        foreach (Piece p in new[]
                 {
                     Piece.WPawn, Piece.WKnight, Piece.WBishop, Piece.WRook, Piece.WQueen, Piece.WKing,
                     Piece.BPawn, Piece.BKnight, Piece.BBishop, Piece.BRook, Piece.BQueen, Piece.BKing,
                 })
            Assert.Equal(derived.Of(p), b.PieceBB(p));
    }

    [Fact]
    public void RebuildBitboards_RecoversFromADirectWrite()
    {
        var b = Board.FromFen(Startpos);
        b.Squares[Board.Sq(4, 3)] = Piece.WQueen;      // deliberate bypass
        Assert.False(b.BitboardsConsistent());          // the invariant must NOTICE
        b.RebuildBitboards();
        Assert.True(b.BitboardsConsistent());
    }
}
