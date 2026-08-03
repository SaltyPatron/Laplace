using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Modality.Tests;

/// <summary>
/// Motif detection from geometry, and — the point of the exercise — motif KEYS that are
/// position-independent so the cell they name accumulates witnesses across every game the
/// motif ever appears in. A position appears once (92.0% of MOVE cells have witness_count = 1);
/// "knight forks king and rook" appears constantly.
/// </summary>
public class ChessTacticGeometryTests
{
    // White Nd5 attacks c7 (rook) and e7 (king) — the textbook royal fork.
    private const string KnightForksKingRook = "8/2r1k3/8/3N4/8/8/8/4K3 w - - 0 1";
    // White Bb5 pins the c6 knight against the e8 king.
    private const string BishopPinsKnight = "4k3/8/2n5/1B6/8/8/8/4K3 w - - 0 1";
    // White Ra1 skewers the a-file: black QUEEN a4 in front, ROOK a8 behind.
    private const string RookSkewersQueen = "r3k3/8/8/8/q7/8/8/R3K3 w - - 0 1";
    private const string Startpos = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    [Fact]
    public void Fork_DetectsKnightForkingKingAndRook()
    {
        var forks = ChessTacticGeometry.Forks(Board.FromFen(KnightForksKingRook), byWhite: true);
        Assert.Single(forks);
        var f = forks[0];
        Assert.Equal(TacticKind.Fork, f.Kind);
        Assert.Equal(Piece.WKnight, f.Attacker);
        // Victims canonically ordered richest-first, so the king leads regardless of scan order.
        Assert.Equal(Piece.BKing, f.Victim1);
        Assert.Equal(Piece.BRook, f.Victim2);
    }

    [Fact]
    public void Fork_IgnoresDoubleAttacksOnLesserPieces()
    {
        // A queen attacking two pawns is not a fork anyone plays for; the attacker-value test
        // is what keeps the motif alphabet meaningful instead of firing every move.
        var forks = ChessTacticGeometry.Forks(Board.FromFen("8/8/1p1p4/8/2Q5/8/8/4K2k w - - 0 1"), byWhite: true);
        Assert.Empty(forks);
    }

    [Fact]
    public void Pin_DetectsBishopPinningKnightToKing()
    {
        var pins = ChessTacticGeometry.PinsAndSkewers(Board.FromFen(BishopPinsKnight), byWhite: true);
        var pin = Assert.Single(pins);
        Assert.Equal(TacticKind.Pin, pin.Kind);
        Assert.Equal(Piece.WBishop, pin.Attacker);
        Assert.Equal(Piece.BKnight, pin.Victim1);   // front, lesser
        Assert.Equal(Piece.BKing, pin.Victim2);     // back, greater
    }

    [Fact]
    public void Skewer_IsDistinguishedFromPinByWhichEndIsWorthMore()
    {
        var hits = ChessTacticGeometry.PinsAndSkewers(Board.FromFen(RookSkewersQueen), byWhite: true);
        var sk = Assert.Single(hits, h => h.Kind == TacticKind.Skewer);
        Assert.Equal(Piece.WRook, sk.Attacker);
        Assert.Equal(Piece.BQueen, sk.Victim1);   // front, MORE valuable -> skewer
        Assert.Equal(Piece.BRook, sk.Victim2);
    }

    [Fact]
    public void Startpos_HasNoTactics()
    {
        var b = Board.FromFen(Startpos);
        Assert.Empty(ChessTacticGeometry.Forks(b, byWhite: true));
        Assert.Empty(ChessTacticGeometry.Forks(b, byWhite: false));
        Assert.Empty(ChessTacticGeometry.PinsAndSkewers(b, byWhite: true));
        Assert.Empty(ChessTacticGeometry.PinsAndSkewers(b, byWhite: false));
    }

    /// <summary>
    /// THE property that makes this worth attesting: the same motif in two unrelated positions
    /// must produce the SAME key, so its consensus cell accumulates rather than splitting.
    /// </summary>
    [Fact]
    public void Key_IsIdenticalForTheSameMotifInUnrelatedPositions()
    {
        var a = ChessTacticGeometry.Forks(Board.FromFen(KnightForksKingRook), byWhite: true)[0];
        // Same royal fork, different squares, different extra material on the board.
        var b = ChessTacticGeometry.Forks(
            Board.FromFen("8/5r2/4k3/6N1/8/3P4/8/4K3 w - - 0 1"), byWhite: true);
        var royal = Assert.Single(b, f => f.Victim1 == Piece.BKing && f.Victim2 == Piece.BRook);
        Assert.Equal(a.Key, royal.Key);
    }

    [Fact]
    public void Key_SeparatesDistinctMotifs()
    {
        var fork = ChessTacticGeometry.Forks(Board.FromFen(KnightForksKingRook), byWhite: true)[0];
        var pin = ChessTacticGeometry.PinsAndSkewers(Board.FromFen(BishopPinsKnight), byWhite: true)[0];
        var skewer = ChessTacticGeometry.PinsAndSkewers(Board.FromFen(RookSkewersQueen), byWhite: true)
            .Find(h => h.Kind == TacticKind.Skewer);
        Assert.NotEqual(fork.Key, pin.Key);
        Assert.NotEqual(pin.Key, skewer.Key);
        Assert.NotEqual(fork.Key, skewer.Key);
    }

    [Fact]
    public void Key_IsColourNormalised_SoMirroredMotifsShareACell()
    {
        // Black knight forking a white king+rook is the SAME claim as the white version.
        var white = ChessTacticGeometry.Forks(Board.FromFen(KnightForksKingRook), byWhite: true)[0];
        var black = ChessTacticGeometry.Forks(
            Board.FromFen("4k3/8/8/8/8/3n4/8/2R1K3 b - - 0 1"), byWhite: false);
        var mirrored = Assert.Single(black, f => f.Victim1 == Piece.WKing && f.Victim2 == Piece.WRook);
        Assert.Equal(white.Key, mirrored.Key);
    }
}
