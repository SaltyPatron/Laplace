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
