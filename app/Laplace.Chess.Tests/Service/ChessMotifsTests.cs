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

    // ---- multi-ply window: the sacrifice family ----

    /// Replay a SAN sequence into the window DetectGame consumes. fen == null means the
    /// standard array (StandardStart = true), matching how ChessAnalyze builds it.
    private static ChessMotifs.ReplayWindow Replay(string? fen, string[] sans, int[]? evals = null)
    {
        var board = Board.FromFen(fen ?? ChessModality.StartFen);
        var boards = new List<Board> { board };
        var moves = new List<ChessMove>();
        foreach (var san in sans)
        {
            var mv = San.Resolve(board, MoveGen.Legal(board), san)
                     ?? throw new InvalidOperationException($"unresolvable SAN {san}");
            var next = board.Clone();
            MoveApply.Make(next, mv);
            moves.Add(mv);
            boards.Add(next);
            board = next;
        }
        return new ChessMotifs.ReplayWindow(boards, moves, evals, StandardStart: fen is null);
    }

    [Fact]
    public void DetectGame_KingsGambitDeclined_TagsGambitOffered()
    {
        var tags = ChessMotifs.DetectGame(Replay(null, ["e4", "e5", "f4", "d6"]));
        Assert.Contains("gambit", tags[2]);
        Assert.Contains("sacrifice_offered", tags[2]);
        Assert.DoesNotContain("sacrifice", tags[2]);
    }

    [Fact]
    public void DetectGame_KingsGambitAccepted_TagsGambitSacrifice()
    {
        var tags = ChessMotifs.DetectGame(Replay(null, ["e4", "e5", "f4", "exf4"]));
        Assert.Contains("gambit", tags[2]);
        Assert.Contains("sacrifice", tags[2]);
        Assert.DoesNotContain("sacrifice_offered", tags[2]);
    }

    [Fact]
    public void DetectGame_QueensGambitAccepted_TagsGambitSacrifice()
    {
        var tags = ChessMotifs.DetectGame(Replay(null, ["d4", "d5", "c4", "dxc4"]));
        Assert.Contains("gambit", tags[2]);
        Assert.Contains("sacrifice", tags[2]);
    }

    [Fact]
    public void DetectGame_QueenSacAccepted_TagsQueenSac()
    {
        // Qd5 walks into the e6 pawn; exd5 accepts. No evals -> material window decides.
        var tags = ChessMotifs.DetectGame(Replay("k7/8/4p3/8/8/8/8/K2Q4 w - - 0 1", ["Qd5", "exd5"]));
        Assert.Contains("sacrifice", tags[0]);
        Assert.Contains("queen_sac", tags[0]);
        Assert.DoesNotContain("gambit", tags[0]); // not from the standard array
    }

    [Fact]
    public void DetectGame_QueenSacDeclined_TagsOfferedOnly()
    {
        var tags = ChessMotifs.DetectGame(Replay("k7/8/4p3/8/8/8/8/K2Q4 w - - 0 1", ["Qd5", "Kb8"]));
        Assert.Contains("sacrifice_offered", tags[0]);
        Assert.DoesNotContain("sacrifice", tags[0]);
        Assert.DoesNotContain("queen_sac", tags[0]);
    }

    [Fact]
    public void DetectGame_RookTakesKnightRecaptured_TagsExchangeSac()
    {
        // Rxc5 dxc5: rook given for a minor — the exchange sacrifice.
        var tags = ChessMotifs.DetectGame(Replay("k7/8/3p4/2n5/8/8/8/K1R5 w - - 0 1", ["Rxc5", "dxc5"]));
        Assert.Contains("sacrifice", tags[0]);
        Assert.Contains("exchange_sac", tags[0]);
        Assert.DoesNotContain("queen_sac", tags[0]);
    }

    [Fact]
    public void DetectGame_EvalCollapse_VetoesSacrificeAsBlunder()
    {
        // Same accepted queen sac, but the annotated eval charges the full material bill
        // (white-POV: even before, -900 after) — the engine calls it a blunder, not a sac.
        var tags = ChessMotifs.DetectGame(
            Replay("k7/8/4p3/8/8/8/8/K2Q4 w - - 0 1", ["Qd5", "exd5"], evals: [0, -900]));
        Assert.DoesNotContain("sacrifice", tags[0]);
        Assert.DoesNotContain("queen_sac", tags[0]);
    }

    [Fact]
    public void DetectGame_EvalHolds_CorroboratesSacrifice()
    {
        // The eval concedes far less than the queen's value — compensation exists.
        var tags = ChessMotifs.DetectGame(
            Replay("k7/8/4p3/8/8/8/8/K2Q4 w - - 0 1", ["Qd5", "exd5"], evals: [0, -50]));
        Assert.Contains("sacrifice", tags[0]);
        Assert.Contains("queen_sac", tags[0]);
    }

    [Fact]
    public void DetectGame_EqualTrade_TagsNothing()
    {
        // exd5 Qxd5 is an even pawn trade: SEE = 0, no sacrifice shapes.
        var tags = ChessMotifs.DetectGame(Replay("k2q4/8/8/3p4/4P3/8/8/K7 w - - 0 1", ["exd5", "Qxd5"]));
        Assert.DoesNotContain("sacrifice", tags[0]);
        Assert.DoesNotContain("sacrifice_offered", tags[0]);
    }

    [Fact]
    public void DetectGame_QuietOpening_TagsNothing()
    {
        var tags = ChessMotifs.DetectGame(Replay(null, ["e4", "e5", "Nf3", "Nc6", "Bb5", "a6"]));
        Assert.All(tags, t => Assert.Empty(t));
    }

    [Fact]
    public void DetectGame_LegalTrap_TagsKnightSacOffered()
    {
        // Légal: 5.Nxe5 leaves the knight en prise (SEE < 0); black grabs the queen
        // instead of taking it — an offered (declined) sacrifice, and no gambit tag
        // (the offered unit is a knight, not a pawn).
        var tags = ChessMotifs.DetectGame(Replay(null,
            ["e4", "e5", "Nf3", "d6", "Bc4", "Bg4", "Nc3", "g6", "Nxe5", "Bxd1", "Bxf7+", "Ke7", "Nd5#"]));
        Assert.Contains("sacrifice_offered", tags[8]);
        Assert.All(tags, t => Assert.DoesNotContain("gambit", t));
    }
}
