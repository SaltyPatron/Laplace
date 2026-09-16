using System.Collections.Immutable;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

public sealed class SearchTests
{
    private const int MateThreshold = 29_000;

    private sealed class FavorBias : IRootBias
    {
        private readonly string _uci;
        private readonly int _cp;
        public FavorBias(string uci, int cp) { _uci = uci; _cp = cp; }
        public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
        {
            var b = new int[moves.Count];
            for (int i = 0; i < moves.Count; i++) if (moves[i].ToUci() == _uci) b[i] = _cp;
            return b;
        }
    }

    private sealed class UniformBias(int centipawns) : IRootBias
    {
        public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
            => Enumerable.Repeat(centipawns, moves.Count).ToArray();
    }

    private sealed class CountingZeroBias : IRootBias
    {
        public int Calls { get; private set; }
        public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
        {
            Calls++;
            return new int[moves.Count];
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void AllZeroRootBias_PreservesClassicalResultAndNodeCount(int depth)
    {
        var board = Board.FromFen(ChessModality.StartFen);
        var limits = new Search.Limits(MaxDepth: depth);
        var provider = new CountingZeroBias();
        var guided = new Search(rootBias: provider, ttBits: 14);
        var classical = new Search(ttBits: 14);

        Assert.Equal(classical.Think(board, limits), guided.Think(board, limits));
        Assert.Equal(1, provider.Calls);

        // A new go re-observes its provider once, even when the prior go found no bonuses.
        Assert.Equal(classical.Think(board, limits), guided.Think(board, limits));
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public void CompletedIterations_ReportCumulativeWorkAndExactCompletedResult()
    {
        var board = Board.FromFen(ChessModality.StartFen);
        var state = new ChessState(board);
        var iterations = new List<Search.Iteration>();
        var result = new Search(ttBits: 14).Think(state,
            new Search.Limits(MaxDepth: 4), onIterationCompleted: iterations.Add);

        Assert.Equal(new[] { 1, 2, 3, 4 }, iterations.Select(i => i.Depth));
        Assert.All(iterations, i => Assert.Contains(i.BestMove, MoveGen.Legal(board)));
        for (int i = 1; i < iterations.Count; i++)
        {
            Assert.True(iterations[i].Nodes > iterations[i - 1].Nodes);
            Assert.True(iterations[i].ElapsedMilliseconds >= iterations[i - 1].ElapsedMilliseconds);
        }
        var last = iterations[^1];
        Assert.Equal(new Search.Result(last.BestMove, last.Score, last.Depth, last.Nodes), result);
        Assert.Equal(ChessModality.StartFen, board.ToFen());
    }

    [Fact]
    public void CancellationAfterCompletedIteration_RetainsThatMoveScoreAndDepth()
    {
        using var cancellation = new CancellationTokenSource();
        var iterations = new List<Search.Iteration>();
        var result = new Search(ttBits: 14).Think(Board.FromFen(ChessModality.StartFen),
            new Search.Limits(MaxDepth: 64), cancellation.Token, iteration =>
            {
                iterations.Add(iteration);
                if (iteration.Depth == 2) cancellation.Cancel();
            });

        Assert.Equal(new[] { 1, 2 }, iterations.Select(i => i.Depth));
        var last = iterations[^1];
        Assert.NotEqual(0, last.Score); // cancellation's unfinished call returns zero internally
        Assert.Equal(last.BestMove, result.BestMove);
        Assert.Equal(last.Score, result.Score);
        Assert.Equal(last.Depth, result.Depth);
        Assert.Equal(last.Nodes, result.Nodes);
    }

    [Fact]
    public void NodeLimitInsideDeeperIteration_RetainsLastCompletedResult()
    {
        var board = Board.FromFen(ChessModality.StartFen);
        var completed = new Search(ttBits: 14).Think(board, new Search.Limits(MaxDepth: 2));
        var iterations = new List<Search.Iteration>();
        long budget = completed.Nodes + 40;
        var result = new Search(ttBits: 14).Think(board,
            new Search.Limits(MaxDepth: 64, MaxNodes: budget), onIterationCompleted: iterations.Add);

        Assert.Equal(new[] { 1, 2 }, iterations.Select(i => i.Depth));
        Assert.Equal(completed.BestMove, result.BestMove);
        Assert.Equal(completed.Score, result.Score);
        Assert.Equal(completed.Depth, result.Depth);
        Assert.Equal(budget, result.Nodes);
        Assert.True(result.Nodes > iterations[^1].Nodes);
    }

    [Theory]
    [InlineData(ChessModality.StartFen, true)]
    [InlineData("7k/6Q1/5K2/8/8/8/8/8 b - - 0 1", false)]
    public void NoCompletedIteration_DoesNotPublishProgress(string fen, bool hasLegalMove)
    {
        var board = Board.FromFen(fen);
        var iterations = new List<Search.Iteration>();
        var result = new Search(ttBits: 14).Think(board,
            new Search.Limits(MaxDepth: 64, MaxNodes: 0), onIterationCompleted: iterations.Add);

        Assert.Empty(iterations);
        Assert.Equal(0, result.Depth);
        Assert.Equal(0, result.Nodes);
        if (hasLegalMove) Assert.Contains(result.BestMove!.Value, MoveGen.Legal(board));
        else Assert.Null(result.BestMove);
    }

    [Fact]
    public void RootBias_SteersSelection_TheSubstrateSeam()
    {
        var b = Board.FromFen(ChessModality.StartFen);
        var unguided = new Search().Think(b, new Search.Limits(MaxDepth: 4));
        var guided = new Search(EvalTerm.All, new FavorBias("a2a3", 500)).Think(b, new Search.Limits(MaxDepth: 4));
        Assert.NotEqual("a2a3", unguided.BestMove!.Value.ToUci());
        Assert.Equal("a2a3", guided.BestMove!.Value.ToUci());
    }

    [Fact]
    public void RootBias_Null_IsPureClassical()
    {
        var b = Board.FromFen("6k1/5ppp/8/8/8/8/8/4R1K1 w - - 0 1");
        var withZero = new Search(EvalTerm.All, new FavorBias("a1a1", 0)).Think(b, new Search.Limits(MaxDepth: 3));
        var pure = new Search().Think(b, new Search.Limits(MaxDepth: 3));
        Assert.Equal(pure.BestMove!.Value.ToUci(), withZero.BestMove!.Value.ToUci());
    }

    [Fact]
    public void RootSteeringScore_IsNotReusedAsInteriorPositionTruth()
    {
        var root = Board.FromFen(ChessModality.StartFen);
        var bias = new UniformBias(100);
        var reused = new Search(EvalTerm.All, bias, ttBits: 12);

        // Prime every child as a separately steered decision root. Those scores must not
        // become context-free values when the parent subsequently reaches the same boards.
        foreach (var move in MoveGen.Legal(root))
        {
            var child = root.Clone();
            MoveApply.Make(child, move);
            _ = reused.Think(child, new Search.Limits(MaxDepth: 1));
        }

        var actual = reused.Think(root, new Search.Limits(MaxDepth: 2));
        var expected = new Search(EvalTerm.All, bias, ttBits: 12)
            .Think(root, new Search.Limits(MaxDepth: 2));

        Assert.Equal(expected.BestMove, actual.BestMove);
        Assert.Equal(expected.Score, actual.Score);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(75, 0, 0)]
    [InlineData(-75, 0, 0)]
    [InlineData(175, 0, -50)]
    [InlineData(-175, 0, 50)]
    [InlineData(475, 0, -200)]
    [InlineData(-475, 0, 200)]
    [InlineData(475, 1, 200)]
    [InlineData(-475, 1, -200)]
    public void DrawUtility_IsContextual_NotUniversalContempt(
        int rootAdvantageCp, int ply, int expected)
        => Assert.Equal(expected, Search.ContextualDrawScore(rootAdvantageCp, ply));

    [Fact]
    public void ExactTablebaseDraw_NeutralizesApparentMaterialAdvantage()
    {
        // Deliberately give White a queen so classical evaluation says "winning", then make
        // the selected exact provider say the root is a draw. Exact WDL must define the root's
        // draw stance instead of static material manufacturing contempt for a proven draw.
        var board = Board.FromFen("7k/8/8/8/8/8/5Q2/4K3 w - - 0 1");
        var search = new Search(tablebase: _ => new SearchTablebaseVerdict(Wdl: 2, Dtz: 0));
        var result = search.Think(board, new Search.Limits(MaxDepth: 2));

        Assert.NotNull(result.BestMove);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void InsufficientMaterialRoot_IsAlreadyTerminalDraw()
    {
        // Classical material sees a bishop, but chess law has already closed K+B v K as a draw.
        // Search must not manufacture a move or contempt score after the game is terminal.
        var board = Board.FromFen("7k/8/8/8/8/8/5B2/4K3 w - - 0 1");
        var result = new Search().Think(board, new Search.Limits(MaxDepth: 2));

        Assert.Null(result.BestMove);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void PreRootThreefold_IsTerminalBeforeMoveSelection()
    {
        var board = Board.FromFen("7k/8/8/8/8/8/5Q2/4K3 w - - 0 1");
        Hash128 root = ChessPositionIdentity.PositionId(board);
        var state = new ChessState(board, ImmutableList.Create(root, root, root));

        var result = new Search().Think(state, new Search.Limits(MaxDepth: 2));

        Assert.Null(result.BestMove);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void WinningSide_AvoidsMoveThatCreatesThirdOccurrence()
    {
        var board = Board.FromFen("7k/8/8/8/8/8/5Q2/4K3 w - - 0 1");
        var legal = MoveGen.Legal(board);
        var repeat = Assert.Single(legal, static m => m.ToUci() == "f2f3");
        Hash128 root = ChessPositionIdentity.PositionId(board);
        Hash128 repeatedChild = NextId(board, repeat);
        var state = new ChessState(
            board,
            ImmutableList.Create(repeatedChild, root, repeatedChild, root));

        var result = new Search().Think(state, new Search.Limits(MaxDepth: 1));

        Assert.NotNull(result.BestMove);
        Assert.NotEqual(repeat, result.BestMove!.Value);
        Assert.True(result.Score > 0, $"winning side should retain winning utility, score={result.Score}");
    }

    [Fact]
    public void LosingSide_TakesAvailableThirdOccurrenceDraw()
    {
        var board = Board.FromFen("7k/8/8/8/8/8/5Q2/4K3 b - - 0 1");
        var legal = MoveGen.Legal(board);
        Assert.True(legal.Count > 1);
        var repeat = legal[0];
        Hash128 root = ChessPositionIdentity.PositionId(board);
        Hash128 repeatedChild = NextId(board, repeat);
        var state = new ChessState(
            board,
            ImmutableList.Create(repeatedChild, root, repeatedChild, root));

        var result = new Search().Think(state, new Search.Limits(MaxDepth: 1));

        Assert.Equal(repeat, result.BestMove);
        Assert.True(result.Score > 0, $"draw rescue should be positive from losing root POV, score={result.Score}");
    }

    [Fact]
    public void SecondOccurrence_IsOrdinaryPosition_NotFalseThreefold()
    {
        var board = Board.FromFen("7k/8/8/8/8/8/5Q2/4K3 w - - 0 1");
        var repeat = Assert.Single(MoveGen.Legal(board), static m => m.ToUci() == "f2f3");
        Hash128 root = ChessPositionIdentity.PositionId(board);
        Hash128 child = NextId(board, repeat);
        var withOnePriorOccurrence = new ChessState(board, ImmutableList.Create(child, root));

        var historyAware = new Search().Think(
            withOnePriorOccurrence, new Search.Limits(MaxDepth: 1));
        var snapshotOnly = new Search().Think(
            board, new Search.Limits(MaxDepth: 1));

        Assert.Equal(snapshotOnly.BestMove, historyAware.BestMove);
        Assert.Equal(snapshotOnly.Score, historyAware.Score);
    }

    private static Hash128 NextId(Board board, ChessMove move)
    {
        var next = board.Clone();
        MoveApply.Make(next, move);
        return ChessPositionIdentity.PositionId(next);
    }

    private static Search.Result Think(string fen, int depth)
        => new Search().Think(Board.FromFen(fen), new Search.Limits(MaxDepth: depth));

    [Fact]
    public void FindsMateInOne_BackRank()
    {
        var r = Think("6k1/5ppp/8/8/8/8/8/4R1K1 w - - 0 1", depth: 3);
        Assert.NotNull(r.BestMove);
        Assert.Equal("e1e8", r.BestMove!.Value.ToUci());
        Assert.True(r.Score >= MateThreshold, $"should see forced mate, score={r.Score}");
    }

    [Fact]
    public void FindsForcedMateInTwo()
    {
        var r = Think("7k/5K2/8/8/8/8/8/7Q w - - 0 1", depth: 4);
        Assert.True(r.Score >= MateThreshold, $"should force mate in two, score={r.Score}");
    }

    [Fact]
    public void WinsHangingQueen()
    {
        var r = Think("4k3/8/8/8/7q/8/8/4K2R w - - 0 1", depth: 4);
        Assert.NotNull(r.BestMove);
        Assert.Equal("h1h4", r.BestMove!.Value.ToUci());
    }

    [Fact]
    public void Quiescence_DoesNotGrabDefendedPawn()
    {
        var r = Think("4k3/8/2p5/3p4/8/8/8/3RK3 w - - 0 1", depth: 4);
        Assert.NotNull(r.BestMove);
        Assert.NotEqual("d1d5", r.BestMove!.Value.ToUci());
    }

    [Fact]
    public void KQvK_IsRecognisedWinning()
    {
        var r = Think("7k/8/8/8/8/8/5Q2/4K3 w - - 0 1", depth: 4);
        Assert.True(r.Score > 500, $"K+Q vs K is winning, score={r.Score}");
    }

    [Fact]
    public void RespectsTimeBudget_StillReturnsLegalMove()
    {
        var b = Board.FromFen("r1bqkbnr/pppp1ppp/2n5/1B2p3/4P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 3 3");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new Search().Think(b, new Search.Limits(MaxDepth: 64, MaxTimeMs: 100));
        sw.Stop();
        Assert.NotNull(r.BestMove);
        Assert.Contains(r.BestMove!.Value.ToUci(), MoveGen.Legal(b).Select(m => m.ToUci()));
        Assert.True(sw.ElapsedMilliseconds < 1500, $"100ms budget overran: {sw.ElapsedMilliseconds}ms");
        Assert.True(r.Depth >= 1);
    }

    [Fact]
    public void ReturnsLegalMove_FromStartPosition()
    {
        var b = Board.FromFen(ChessModality.StartFen);
        var r = new Search().Think(b, new Search.Limits(MaxDepth: 4));
        Assert.NotNull(r.BestMove);
        var legal = MoveGen.Legal(b).Select(m => m.ToUci());
        Assert.Contains(r.BestMove!.Value.ToUci(), legal);
    }
}
