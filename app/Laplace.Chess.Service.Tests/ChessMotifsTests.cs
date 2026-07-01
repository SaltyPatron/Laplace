using System.Linq;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessMotifsTests
{
    private static (Board Before, ChessMove Move, Board After) Play(string fen, string uci)
    {
        var before = Board.FromFen(fen);
        var move = MoveGen.Legal(before).Single(m => m.ToUci() == uci);
        var after = before.Clone();
        MoveApply.Make(after, move);
        return (before, move, after);
    }

    [Fact]
    public void DetectAtPly_KnightForksKingAndRook_TagsFork()
    {
        // Na6-c7 forks the black king (e8) and rook (a8) — the textbook "royal fork".
        var (before, move, after) = Play("r3k3/8/N7/8/8/8/8/4K3 w - - 0 1", "a6c7");
        Assert.Contains("fork", ChessMotifs.DetectAtPly(before, move, after));
    }

    [Fact]
    public void DetectAtPly_BishopMovesOffFile_TagsDiscoveredCheck()
    {
        // Rook a1 is masked by the bishop on a4; moving the bishop off the a-file exposes
        // Ra1-a8+ without the bishop itself attacking the king.
        var (before, move, after) = Play("k7/8/8/8/B7/8/8/R3K3 w - - 0 1", "a4b5");
        Assert.Contains("discovered_check", ChessMotifs.DetectAtPly(before, move, after));
    }

    [Fact]
    public void DetectAtPly_CapturesUndefendedPiece_TagsHangingPieceWon()
    {
        // Qa1xh8 captures a knight with no black piece anywhere near enough to recapture.
        var (before, move, after) = Play("k6n/8/8/8/8/8/8/Q3K3 w - - 0 1", "a1h8");
        Assert.Contains("hanging_piece_won", ChessMotifs.DetectAtPly(before, move, after));
    }

    [Fact]
    public void DetectAtPly_QuietPawnPush_TagsNothing()
    {
        var (before, move, after) = Play(ChessModality.StartFen, "e2e4");
        Assert.Empty(ChessMotifs.DetectAtPly(before, move, after));
    }

    [Fact]
    public void DetectNamedTrap_ScholarsMateSequence_ReturnsScholarsMate()
        => Assert.Equal("ScholarsMate",
            ChessMotifs.DetectNamedTrap(["e4", "e5", "Bc4", "Nc6", "Qh5", "Nf6", "Qxf7#"]));

    [Fact]
    public void DetectNamedTrap_UnrelatedGame_ReturnsNull()
        => Assert.Null(ChessMotifs.DetectNamedTrap(["d4", "d5", "c4", "e6"]));
}
