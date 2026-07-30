using System.Linq;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

// Static exchange evaluation against hand-computed swap-offs. Values are the engine's
// own table (Search.PieceValue): P=100 N=320 B=330 R=500 Q=900.
public sealed class SeeTests
{
    private static int SeeOf(string fen, string uci)
    {
        var b = Board.FromFen(fen);
        var m = MoveGen.Legal(b).Single(x => x.ToUci() == uci);
        return See.Evaluate(b, m);
    }

    [Fact]
    public void RookTakesUndefendedPawn_WinsPawn()
        => Assert.Equal(100, SeeOf("k7/8/8/3p4/8/8/3R4/K7 w - - 0 1", "d2d5"));

    [Fact]
    public void RookTakesPawnDefendedByPawn_LosesRookForPawn()
        // Rxd6 exd6: +100 - 500 = -400.
        => Assert.Equal(-400, SeeOf("k7/4p3/3p4/8/8/8/3R4/K7 w - - 0 1", "d2d6"));

    [Fact]
    public void RookTakesKnightDefendedByPawn_LosesTheExchange()
        // Rxc5 dxc5: +320 - 500 = -180 (the exchange-sac shape).
        => Assert.Equal(-180, SeeOf("k7/8/3p4/2n5/8/8/8/K1R5 w - - 0 1", "c1c5"));

    [Fact]
    public void XRayBattery_DefenderDeclinesRecapture()
        // Rxd6 wins the pawn; Qxd6 would lose the queen to the doubled rook behind
        // (x-ray through the vacated d3), so black stands pat: SEE = +100.
        => Assert.Equal(100, SeeOf("k7/3q4/3p4/8/8/3R4/3R4/K7 w - - 0 1", "d3d6"));

    [Fact]
    public void QuietMoveOntoAttackedSquare_LosesThePiece()
        // Nd5?? exd5 with no recapture: SEE = -320.
        => Assert.Equal(-320, SeeOf("k7/8/4p3/8/8/2N5/8/K7 w - - 0 1", "c3d5"));

    [Fact]
    public void QuietKnightToDefendedSquare_StillLosesKnightForPawn()
        // Nd5 exd5 exd5: -320 + 100 = -220 (defense does not save a N-for-P trade).
        => Assert.Equal(-220, SeeOf("k7/8/4p3/8/4P3/2N5/8/K7 w - - 0 1", "c3d5"));

    [Fact]
    public void KingRecapture_AllowedWhenSquareUndefended()
        // Rxc5+ Kxc5: +100 - 500 = -400.
        => Assert.Equal(-400, SeeOf("8/8/8/2p5/3k4/8/8/2R1K3 w - - 0 1", "c1c5"));

    [Fact]
    public void KingRecapture_RefusedWhenSquareStillDefended()
        // Qa5 also attacks c5, so after Rxc5 the king cannot legally recapture: SEE = +100.
        => Assert.Equal(100, SeeOf("8/8/8/Q1p5/3k4/8/8/2R1K3 w - - 0 1", "c1c5"));

    [Fact]
    public void EnPassant_CountsTheCapturedPawn()
        => Assert.Equal(100, SeeOf("k7/8/8/3pP3/8/8/8/K7 w - d6 0 2", "e5d6"));

    [Fact]
    public void EqualPawnTrade_IsZero()
        // exd5 Qxd5: +100 - 100 = 0.
        => Assert.Equal(0, SeeOf("k2q4/8/8/3p4/4P3/8/8/K7 w - - 0 1", "e4d5"));
}
