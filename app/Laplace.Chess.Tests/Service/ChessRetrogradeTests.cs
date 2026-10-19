using System.Linq;
using Laplace.Chess.Service;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessRetrogradeTests
{
    private sealed class FakeSyzygyProber(int largest, Func<Board, SyzygyVerdict?> probe) : ISyzygyProber
    {
        public int Largest => largest;
        public int? ProbeWdl(Board board) => probe(board)?.Wdl;
        public SyzygyVerdict? Probe(Board board) => probe(board);
    }

    [Fact]
    public void UnrollLine_PropagatesFastestCheckmateBackwardsAcrossFullBoard()
    {
        var table = new SearchContinuationTable();
        string[] slowMate = ["e4", "e5", "Bc4", "Nc6", "Qf3", "d6", "Nc3", "Nf6", "Qxf7#"];
        string[] fastMate = ["e4", "e5", "Bc4", "Nc6", "Qh5", "Nf6", "Qxf7#"];

        var unrolled = ChessRetrograde.UnrollLine(fastMate);
        Assert.Equal(8, unrolled.Count);
        Assert.All(unrolled, cp => Assert.True(cp.IsCheckmate));
        Assert.Equal(7, unrolled[0].PliesToClose);
        Assert.Equal(4, unrolled[0].Wdl);
        Assert.Equal(0, unrolled[^1].PliesToClose);
        Assert.Equal(0, unrolled[^1].Wdl);

        ChessRetrograde.SeedSearchContinuations(table, slowMate);
        ChessRetrograde.SeedSearchContinuations(table, fastMate);

        var branchBoard = Board.FromFen("r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/8/PPPP1PPP/RNBQK1NR w KQkq - 2 3");
        Assert.True(table.TryGet(ChessPositionIdentity.PositionId(branchBoard), out var entry));
        Assert.True(entry.IsRetrogradeSolved);
        Assert.True(entry.RetrogradeCheckmate);
        Assert.Equal(3, entry.RetrogradePlies);
        Assert.Equal("d1h5", entry.RetrogradeMove?.ToUci());

        var searchResult = new Search(ttBits: 12, continuations: table)
            .Think(branchBoard, new Search.Limits(MaxDepth: 3));
        Assert.Equal("d1h5", searchResult.BestMove?.ToUci());
        Assert.True(searchResult.Score > 29_000);
    }

    [Fact]
    public void UnrollLine_DetectsSixManCaptureIntoFiveManSyzygyAndPropagatesBackwards()
    {
        // 7-man -> 6-man -> 5-man capture sequence with no castling rights.
        const string startFen = "4k3/8/4r3/4R3/8/2B1n3/4P3/4K3 w - - 0 1";
        var prober = new FakeSyzygyProber(5, board =>
        {
            int men = ChessSyzygy.MenCount(board);
            if (men > 5) return null;
            // After 1. Rxe6+ Kd7 2. fxe3 (5 men, Black to move), Black is lost (Wdl = 0, Dtz = 6).
            return board.WhiteToMove
                ? new SyzygyVerdict(4, 5)
                : new SyzygyVerdict(0, -6);
        });

        // Only 1. Rxe6+ Kd7 is played in the line (leaving 6 men on the board, White to move),
        // where White has the capture 2. fxe3! dropping into the 5-man tablebase win.
        var checkpoints = ChessRetrograde.UnrollLine(["Rxe6+", "Kd7"], startFen, prober);
        Assert.Equal(3, checkpoints.Count);

        // Ply 2 has 6 men on the board, yet ProbeOnePlySyzygyClosure detects 2. Rxe3 -> 5-man win!
        var sixManAtPly2 = checkpoints[2];
        Assert.Equal(6, sixManAtPly2.Men);
        Assert.False(sixManAtPly2.IsCheckmate);
        Assert.Equal(4, sixManAtPly2.Wdl);
        Assert.Equal(7, sixManAtPly2.PliesToClose);
        Assert.Equal("e6e3", sixManAtPly2.BestMove?.ToUci());

        // And ply 0 (7 men on the board!) inherits the backward-propagated Syzygy win!
        var sevenManAtPly0 = checkpoints[0];
        Assert.Equal(7, sevenManAtPly0.Men);
        Assert.Equal(4, sevenManAtPly0.Wdl);
        Assert.Equal(9, sevenManAtPly0.PliesToClose);
    }

    [Fact]
    public void DiscoverWinningConditions_ExtractsSevenInTenRulesAndFastestCheckmate()
    {
        // 8 wins (including a 1-move checkmate from the condition) and 2 losses = 8/10 (80% >= 70%).
        const string fenWithBishopPairAndOpenRook = "6k1/5ppp/8/8/8/2B3B1/5PPP/3R2K1 w - - 0 1";
        var games = Enumerable.Range(0, 8)
            .Select(_ => ((IReadOnlyList<string>)new[] { "Rd8#" }, GameOutcome.WonBy(0), (string?)fenWithBishopPairAndOpenRook))
            .Concat(Enumerable.Range(0, 2)
                .Select(_ => ((IReadOnlyList<string>)new[] { "Kf1", "Kf8" }, GameOutcome.WonBy(1), (string?)fenWithBishopPairAndOpenRook)))
            .ToList();

        var rules = ChessRetrograde.DiscoverWinningConditions(games, minWinRate: 0.70, minGames: 5);
        Assert.NotEmpty(rules);

        var conjunctive = Assert.Single(rules, r => r.Condition == "bishop-pair + rook-open-file");
        Assert.Equal(10, conjunctive.Games);
        Assert.Equal(8, conjunctive.Wins);
        Assert.Equal(0.80, conjunctive.EmpiricalWinRate, 3);
        Assert.True(conjunctive.ShrunkWinRate > 0.65);
        Assert.Equal(1, conjunctive.FastestCheckmatePlies);
    }

    [Fact]
    public void ExtractMatingKernel_IsolatesCausalPiecesFromCrowdedBoardAndUnrollsCone()
    {
        Laplace.Engine.Core.CodepointPerfcache.LoadDefault();
        string[] scholarsMate = ["e4", "e5", "Bc4", "Nc6", "Qh5", "Nf6", "Qxf7#"];
        var constellations = ChessRetrograde.UnrollCausalConstellations(scholarsMate, maxLookbackPlies: 3);
        Assert.Equal(4, constellations.Count);

        // Terminal mating kernel (PliesToMate == 0) has only the ~6 causal pieces out of 31 on the board!
        var mateKernel = constellations[^1];
        Assert.Equal(0, mateKernel.PliesToMate);
        Assert.InRange(mateKernel.Atoms.Count, 5, 7);
        Assert.Contains(mateKernel.Atoms, a => a.Piece == Piece.BKing && a.Square0x88 == Board.AlgebraicToSquare("e8"));
        Assert.Contains(mateKernel.Atoms, a => a.Piece == Piece.WQueen && a.Square0x88 == Board.AlgebraicToSquare("f7"));
        Assert.Contains(mateKernel.Atoms, a => a.Piece == Piece.WBishop && a.Square0x88 == Board.AlgebraicToSquare("c4"));

        // 1 ply before mate (before Qxf7#), the constellation has WQueen@h5 + WBishop@c4 + BPawn@f7 + BKing@e8
        // and matches even when queenside pieces are rearranged!
        var onePlyBefore = constellations[^2];
        Assert.Equal(1, onePlyBefore.PliesToMate);
        Assert.Equal("h5f7", onePlyBefore.NextMove?.ToUci());

        var differentQueenside = Board.FromFen("2bqkbnr/pppp1ppp/2n2n2/4p2Q/2B1P3/8/PPPP1PPP/R1B1K1NR w KQk - 4 4");
        Assert.True(ChessRetrograde.BoardContainsConstellation(differentQueenside, onePlyBefore));
    }

    [Fact]
    public void ProjectSubBoardForSyzygy_ProjectsTenPieceBoardIntoFiveManSyzygyWin()
    {
        Laplace.Engine.Core.CodepointPerfcache.LoadDefault();
        // 10-piece board where White has K+Q+R near Black's bare King on h8, plus distant queenside pieces.
        var crowded = Board.FromFen("7k/5Q2/6K1/2n5/p7/P1N5/1p6/1P5R w - - 0 1");
        Assert.Equal(10, ChessSyzygy.MenCount(crowded));

        var prober = new FakeSyzygyProber(5, sub =>
        {
            int men = ChessSyzygy.MenCount(sub);
            if (men > 5) return null;
            int f7 = Board.AlgebraicToSquare("f7");
            int g7 = Board.AlgebraicToSquare("g7");
            return new SyzygyVerdict(4, 1, From: (Board.RankOf(f7) << 3) | Board.FileOf(f7), To: (Board.RankOf(g7) << 3) | Board.FileOf(g7));
        });

        var projected = ChessRetrograde.ProjectSubBoardForSyzygy(crowded, prober, maxMen: 5);
        Assert.NotNull(projected);
        Assert.Equal(5, projected!.Value.Men);
        Assert.Equal(4, projected.Value.Wdl);
        Assert.Equal(1, projected.Value.Dtz);
        Assert.Equal("f7g7", projected.Value.BestMove?.ToUci());
    }
}
